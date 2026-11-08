using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Differ.App.Controls;
using Differ.App.ViewModels;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

[Collection("UI performance")]
public sealed class PerformanceSwitchTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public void LoadedFileSwitchMeetsLatencyBudget()
    {
        var measurements = RunSta(() =>
        {
            var items = Enumerable.Range(0, 10)
                .Select(index => new FormatItem(index % 3))
                .ToArray();
            var host = new PersistentContentHost
            {
                ItemsSource = items,
                ContentTemplateSelector = new FormatTemplateSelector()
            };
            using var window = ShowOffscreen(host);
            WarmAll(host, items);

            var result = MeasureSwitches(host, items, 30);
            Assert.Equal(10, host.CachedContentCount);
            return result;
        });

        Assert.InRange(Percentile95(measurements), 0, 80);
        Assert.InRange(measurements.Max(), 0, 150);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void LoadedWorksheetSwitchMeetsLatencyBudget()
    {
        var measurements = RunSta(() =>
        {
            var worksheets = Enumerable.Range(0, 10)
                .Select(BuildWorksheet)
                .ToArray();
            var template = new DataTemplate
            {
                VisualTree = new FrameworkElementFactory(
                    typeof(SpreadsheetWorksheetDiffView))
            };
            var host = new PersistentContentHost
            {
                ItemsSource = worksheets,
                ContentTemplate = template
            };
            using var window = ShowOffscreen(host);
            WarmAll(host, worksheets);

            var result = MeasureSwitches(host, worksheets, 30);
            Assert.Equal(10, host.CachedContentCount);
            return result;
        });

        Assert.InRange(Percentile95(measurements), 0, 100);
        Assert.InRange(measurements.Max(), 0, 180);
    }

    private static WorksheetTabViewModel BuildWorksheet(int sheetIndex)
    {
        const int columnCount = 8;
        var columns = Enumerable.Range(0, columnCount)
            .Select(index => new SpreadsheetColumnViewModel(
                ((char)('A' + index)).ToString(),
                96,
                index == 3))
            .ToArray();
        var rows = Enumerable.Range(1, 80)
            .Select(rowIndex => BuildSpreadsheetRow(
                sheetIndex,
                rowIndex,
                columnCount))
            .ToArray();
        var overviewRows = rows
            .Select(row => new DiffRowViewModel(
                row.StableId,
                row.StableId,
                row.Kind,
                row.BaseRowNumber,
                string.Empty,
                row.MineRowNumber,
                string.Empty,
                $"Sheet{sheetIndex + 1}",
                0))
            .ToArray();
        var section = new SpreadsheetSectionViewModel(
            $"Sheet{sheetIndex + 1}",
            columns,
            rows);
        return new WorksheetTabViewModel(
            section.Name,
            rows.Count(row => row.Kind != DiffKind.Unchanged),
            section,
            overviewRows);
    }

    private static SpreadsheetRowViewModel BuildSpreadsheetRow(
        int sheetIndex,
        int rowIndex,
        int columnCount)
    {
        var kind = rowIndex % 19 == 0
            ? DiffKind.Modified
            : DiffKind.Unchanged;
        var baseCells = new SpreadsheetCellViewModel[columnCount];
        var mineCells = new SpreadsheetCellViewModel[columnCount];
        for (var columnIndex = 0;
             columnIndex < columnCount;
             columnIndex++)
        {
            var columnKind = columnIndex == 3 ? kind : DiffKind.Unchanged;
            var baseText = $"S{sheetIndex + 1}-R{rowIndex}-C{columnIndex + 1}";
            var mineText = columnKind == DiffKind.Modified
                ? $"{baseText}-changed"
                : baseText;
            baseCells[columnIndex] = BuildCell(
                rowIndex,
                columnIndex,
                baseText,
                columnKind);
            mineCells[columnIndex] = BuildCell(
                rowIndex,
                columnIndex,
                mineText,
                columnKind);
        }

        return new SpreadsheetRowViewModel(
            $"sheet:{sheetIndex}:row:{rowIndex}",
            rowIndex,
            rowIndex,
            kind,
            baseCells,
            mineCells);
    }

    private static SpreadsheetCellViewModel BuildCell(
        int rowIndex,
        int columnIndex,
        string text,
        DiffKind kind)
    {
        var segments = string.IsNullOrEmpty(text)
            ? Array.Empty<InlineDiffSegment>()
            : [new InlineDiffSegment(text, kind != DiffKind.Unchanged)];
        var address = $"{(char)('A' + columnIndex)}{rowIndex}";
        return new SpreadsheetCellViewModel(
            address,
            text,
            text,
            $"{address}{Environment.NewLine}{text}",
            kind,
            segments,
            segments);
    }

    private static WindowScope ShowOffscreen(UIElement content)
    {
        var window = new Window
        {
            Width = 1200,
            Height = 720,
            Left = -20_000,
            Top = -20_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = content
        };
        window.Show();
        FlushRenderQueue();
        return new WindowScope(window);
    }

    private static void WarmAll<T>(
        PersistentContentHost host,
        IReadOnlyList<T> items)
        where T : class
    {
        foreach (var item in items)
        {
            host.SelectedItem = item;
            FlushRenderQueue();
        }
    }

    private static double[] MeasureSwitches<T>(
        PersistentContentHost host,
        IReadOnlyList<T> items,
        int iterations)
        where T : class
    {
        var measurements = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            host.SelectedItem = items[index % items.Count];
            FlushRenderQueue();
            measurements[index] = stopwatch.Elapsed.TotalMilliseconds;
        }

        return measurements;
    }

    private static void FlushRenderQueue()
    {
        // Loaded runs below Render. Drain it as well so WarmAll completes
        // column setup before measuring already-loaded worksheet switches.
        Dispatcher.CurrentDispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);
    }

    private static double Percentile95(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        var index = Math.Clamp(
            (int)Math.Ceiling(ordered.Length * 0.95) - 1,
            0,
            ordered.Length - 1);
        return ordered[index];
    }

    private static T RunSta<T>(Func<T> action)
    {
        Exception? failure = null;
        T? result = default;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

    private sealed record FormatItem(int Kind);

    private sealed class FormatTemplateSelector : DataTemplateSelector
    {
        private readonly DataTemplate _spreadsheet = BuildTemplate(
            typeof(SpreadsheetDiffView));
        private readonly DataTemplate _text = BuildTemplate(
            typeof(TextDiffView));
        private readonly DataTemplate _word = BuildTemplate(
            typeof(WordDiffView));

        public override DataTemplate SelectTemplate(
            object item,
            DependencyObject container)
        {
            return ((FormatItem)item).Kind switch
            {
                0 => _spreadsheet,
                1 => _text,
                _ => _word
            };
        }

        private static DataTemplate BuildTemplate(Type viewType)
        {
            return new DataTemplate
            {
                VisualTree = new FrameworkElementFactory(viewType)
            };
        }
    }

    private sealed class WindowScope(Window window) : IDisposable
    {
        public void Dispose()
        {
            window.Close();
            FlushRenderQueue();
        }
    }
}
