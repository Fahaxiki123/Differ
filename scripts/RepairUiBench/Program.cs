using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Differ.App;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Differ.Formats.Excel;
using Differ.Formats.Word;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.RepairUiBench;

internal static class Program
{
    private const double TargetOperationMs = 100;
    private const double TargetFrameMs = 1000d / 60;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static int _exitCode;
    private static long _actionStarted;
    private static string _activeOperation = string.Empty;
    private static int _activeEvent;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("RepairUiBench <fixed-sample-directory> <output.json> [--visible] [--operation=name] [--events=100]");
            return 2;
        }
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        application.Dispatcher.BeginInvoke(async () =>
        {
            try { await RunAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args.Contains("--visible", StringComparer.Ordinal),
                args.FirstOrDefault(value => value.StartsWith("--operation=", StringComparison.Ordinal))?[12..],
                int.Parse(args.FirstOrDefault(value => value.StartsWith("--events=", StringComparison.Ordinal))?[9..] ?? "100", CultureInfo.InvariantCulture)); }
            catch (Exception exception) { Console.Error.WriteLine(exception); _exitCode = 1; }
            finally { application.Shutdown(); }
        });
        application.Run();
        return _exitCode;
    }

    private static async Task RunAsync(string samples, string output, bool visible, string? operationFilter, int events)
    {
        if (events is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(events));
        string[] supportedOperations = ["excel-medium.small-scroll", "excel-medium.visible-cell", "excel-medium.scrollbar-jump",
            "excel-medium.remote-cell", "word-medium.small-scroll", "word-medium.scrollbar-jump", "word-medium.next-region",
            "excel-multi.sheet-switch", "loaded-file.switch"];
        if (operationFilter is not null && !supportedOperations.Contains(operationFilter, StringComparer.Ordinal))
            throw new ArgumentException("Unknown --operation. Available: " + string.Join(", ", supportedOperations));
        var sourceRoot = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BenchSourceRoot").Value!;
        var report = new Report
        {
            RecordedUtc = DateTimeOffset.UtcNow,
            SourceRoot = sourceRoot,
            Version = typeof(DiffWindow).Assembly.GetName().Version?.ToString() ?? "unknown",
            Environment = new { OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                LogicalProcessors = System.Environment.ProcessorCount, Machine = System.Environment.MachineName,
                ViewportDip = "1400 x 820", Offscreen = !visible },
            Budget = new { OperationP95Ms = TargetOperationMs, FrameIntervalMs = TargetFrameMs, EventsPerOperation = events,
                UiNoResponseLimitMs = 200 },
            Method = "Operations measure action through WPF ApplicationIdle plus layout; heartbeat is a 16.67 ms Input-priority DispatcherTimer. "
                + "Scroll samples are scheduled at approximately 60 Hz with asynchronous waits. Offscreen layout timings and heartbeat are not physical monitor frame presentation or native mouse/keyboard latency. "
                + "One fresh process supplies first-load measurements; operations are warm after first views and sheets have been visited. Input files are never changed."
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var sampleLog = output + ".samples.jsonl";
        File.WriteAllText(sampleLog, string.Empty);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            var started = Interlocked.Read(ref _actionStarted);
            if (started == 0 || Stopwatch.GetElapsedTime(started).TotalSeconds < 20) return;
            try
            {
                File.WriteAllText(output + ".watchdog.json", JsonSerializer.Serialize(new
                {
                    Operation = _activeOperation, Event = Volatile.Read(ref _activeEvent),
                    ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Result = "FAILED: UI action or dispatcher drain did not complete in 20 seconds; owned benchmark process terminated",
                    PartialSamples = sampleLog, ExitCode = 124
                }, JsonOptions));
            }
            finally { System.Environment.Exit(124); }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var temporary = Path.Combine(Path.GetTempPath(), "Differ.RepairUiBench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var phase = "setup";
        var heartbeatSamples = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var previousTick = Stopwatch.GetTimestamp();
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(TargetFrameMs) };
        heartbeat.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            if (!heartbeatSamples.TryGetValue(phase, out var values)) heartbeatSamples[phase] = values = [];
            values.Add(Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds);
            previousTick = now;
        };
        heartbeat.Start();
        try
        {
            using var preferences = new UserPreferencesService(Path.Combine(temporary, "preferences.json"));
            using var scheduler = new ComparisonLoadScheduler();
            var store = new ExternalDiffSessionStore(Path.Combine(temporary, "sessions"));
            using var workspace = new ComparisonWorkspaceViewModel(new DiffCoordinator([new ExcelDiffAdapter(), new WordDiffAdapter()]),
                scheduler, store, NullLoggerFactory.Instance, preferences);
            var resources = LoadResources(sourceRoot);
            using var themes = new ThemeService(resources, Dispatcher.CurrentDispatcher, new FixedTheme());
            themes.InitializeFromSystem();
            var window = new DiffWindow(workspace, themes)
            {
                Width = 1400, Height = 820, Left = visible ? 80 : -20_000, Top = visible ? 60 : -20_000,
                WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
                ShowActivated = visible, ShowInTaskbar = visible
            };
            window.Resources.MergedDictionaries.Add(resources);
            window.Show();
            await DrainAsync(window);
            var loaded = new Dictionary<string, DiffWindowViewModel>(StringComparer.Ordinal);
            var cases = new[] { ("excel-medium", "xlsx"), ("word-medium", "docx"), ("excel-multi", "xlsx") }
                .Where(value => operationFilter is null || operationFilter == "loaded-file.switch"
                    || operationFilter.StartsWith(value.Item1 + ".", StringComparison.Ordinal)).ToArray();
            foreach (var (name, extension) in cases)
            {
                phase = name + ".first-load";
                var oldPath = Path.Combine(samples, name + ".base." + extension);
                var newPath = Path.Combine(samples, name + ".mine." + extension);
                if (!File.Exists(oldPath) || !File.Exists(newPath)) throw new FileNotFoundException("Run RepairBench once to generate all fixed samples.");
                var oldHash = await HashAsync(oldPath);
                var newHash = await HashAsync(newPath);
                var timer = Stopwatch.StartNew();
                var session = await store.CreateAsync(new ExternalDiffRequest
                {
                    BaseFilePath = oldPath, MineFilePath = newPath, OriginalFileName = name + "." + extension,
                    BaseDisplayName = name + " BASE", MineDisplayName = name + " WORKING", BaseRevision = "controlled"
                }, CancellationToken.None);
                var preparationMs = timer.Elapsed.TotalMilliseconds;
                var owner = workspace.AddSession(session);
                await WaitUntilAsync(() => !owner.IsBusy && (owner.CurrentDocument is not null || owner.HasError), TimeSpan.FromMinutes(3));
                await DrainAsync(window);
                if (owner.HasError) throw new InvalidOperationException(owner.ErrorText);
                loaded.Add(name, owner);
                report.Inputs.Add(new { Name = name, BaseBytes = new FileInfo(oldPath).Length, MineBytes = new FileInfo(newPath).Length,
                    BaseSha256 = oldHash, MineSha256 = newHash, FilePreparationMs = preparationMs,
                    FirstOperableLayoutMs = timer.Elapsed.TotalMilliseconds, owner.DifferenceCount,
                    SheetCount = owner.WorksheetTabs.Count, TextRows = owner.TextRows.Count,
                    SpreadsheetRows = owner.WorksheetTabs.Sum(sheet => sheet.Rows.Count),
                    PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 });
                Console.WriteLine($"{name}: first operable layout {timer.Elapsed.TotalMilliseconds:F1} ms");
            }

            if (loaded.ContainsKey("excel-medium"))
            {
                workspace.SelectedTab = loaded["excel-medium"];
                await DrainAsync(window);
                var excelGrid = FindVisible<DataGrid>(window, value => value.Name == "MineSpreadsheetGrid");
                var excelScroll = FindVisuals<ScrollViewer>(excelGrid).First();
                excelScroll.ScrollToTop();
                await DrainAsync(window);
                await MeasureAsync("excel-medium.small-scroll", _ => { excelScroll.LineDown(); excelScroll.LineDown(); excelScroll.LineDown(); });
                await MeasureAsync("excel-medium.visible-cell", index =>
                {
                    var cells = FindVisuals<DataGridCell>(excelGrid).Where(cell => cell.IsVisible && cell.Column.DisplayIndex > 0
                        && IsInsideViewport(cell, excelScroll)).ToArray();
                    if (cells.Length == 0) throw new InvalidOperationException("No visible business cell is available for the selection benchmark.");
                    var cell = cells[index % cells.Length];
                    var info = new DataGridCellInfo(cell.DataContext, cell.Column);
                    excelGrid.CurrentCell = info;
                    excelGrid.SelectedCells.Clear();
                    excelGrid.SelectedCells.Add(info);
                });
                await MeasureAsync("excel-medium.scrollbar-jump", index => excelScroll.ScrollToVerticalOffset(
                    ((index * 37) % 100) / 99d * excelScroll.ScrollableHeight));
                await MeasureAsync("excel-medium.remote-cell", index =>
                {
                    // This intentionally exercises distant navigation and is
                    // reported separately from visible-cell selection above.
                    var row = excelGrid.Items[Math.Min(excelGrid.Items.Count - 1, (index * 97) % excelGrid.Items.Count)];
                    // Column zero is the row-number gutter, not a business cell.
                    var column = excelGrid.Columns[1 + index % Math.Min(8, excelGrid.Columns.Count - 1)];
                    excelGrid.ScrollIntoView(row, column);
                    excelGrid.CurrentCell = new DataGridCellInfo(row, column);
                    excelGrid.SelectedCells.Clear();
                    excelGrid.SelectedCells.Add(new DataGridCellInfo(row, column));
                });
            }

            if (loaded.ContainsKey("word-medium"))
            {
                workspace.SelectedTab = loaded["word-medium"];
                await DrainAsync(window);
                var wordList = FindVisible<ListBox>(window, value => value.Name == "WordRows");
                var wordScroll = FindVisuals<ScrollViewer>(wordList).First();
                wordScroll.ScrollToTop();
                await DrainAsync(window);
                await MeasureAsync("word-medium.small-scroll", _ => { wordScroll.LineDown(); wordScroll.LineDown(); wordScroll.LineDown(); });
                await MeasureAsync("word-medium.scrollbar-jump", index => wordScroll.ScrollToVerticalOffset(
                    ((index * 37) % 100) / 99d * wordScroll.ScrollableHeight));
                await MeasureAsync("word-medium.next-region", _ => workspace.SelectedTab.NextDifferenceCommand.Execute(null));
            }

            if (loaded.ContainsKey("excel-multi"))
            {
            workspace.SelectedTab = loaded["excel-multi"];
            foreach (var sheet in workspace.SelectedTab.WorksheetTabs)
            {
                workspace.SelectedTab.SelectedWorksheet = sheet;
                await DrainAsync(window);
            }
            await MeasureAsync("excel-multi.sheet-switch", index =>
            {
                var owner = loaded["excel-multi"];
                owner.SelectedWorksheet = owner.WorksheetTabs[index % owner.WorksheetTabs.Count];
            });
            }
            var tabs = loaded.Values.ToArray();
            foreach (var tab in tabs) { workspace.SelectedTab = tab; await DrainAsync(window); }
            await MeasureAsync("loaded-file.switch", index => workspace.SelectedTab = tabs[index % tabs.Length]);

            phase = "integrity";
            report.InputsUnchanged = true;
            foreach (var (name, extension) in cases)
            {
                // Compare to session fingerprint independently of timestamp.
                var owner = loaded[name];
                if (await HashAsync(Path.Combine(samples, name + ".base." + extension)) != await HashAsync(owner.Session.BaseSnapshotPath)
                    || await HashAsync(Path.Combine(samples, name + ".mine." + extension)) != await HashAsync(owner.Session.MineSnapshotPath))
                    report.InputsUnchanged = false;
            }
            heartbeat.Stop();
            report.Heartbeat = heartbeatSamples.ToDictionary(pair => pair.Key, pair => (object)new
            {
                Intervals = Describe(pair.Value), TargetIntervalMs = TargetFrameMs,
                MaxExcessDelayMs = pair.Value.Count == 0 ? 0 : Math.Max(0, pair.Value.Max() - TargetFrameMs),
                Over200Ms = pair.Value.Count(value => value - TargetFrameMs > 200),
                RawIntervalMs = pair.Value
            }, StringComparer.Ordinal);
            report.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
            File.WriteAllText(output, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine(output);
            window.Close();
            workspace.Dispose();

            async Task MeasureAsync(string name, Action<int> operation)
            {
                if (operationFilter is not null && name != operationFilter) return;
                phase = name + ".warmup";
                report.ActiveOperation = phase;
                File.WriteAllText(output, JsonSerializer.Serialize(report, JsonOptions));
                for (var index = 0; index < 5; index++)
                {
                    StartAction(phase, index);
                    operation(index); await DrainAsync(window);
                    EndAction(phase, index);
                }
                phase = name;
                report.ActiveOperation = phase;
                var values = new List<double>(events);
                var allocated = GC.GetTotalAllocatedBytes(true);
                var workingSet = System.Environment.WorkingSet;
                var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                for (var index = 0; index < events; index++)
                {
                    StartAction(name, index);
                    var watch = Stopwatch.StartNew();
                    operation(index);
                    await DrainAsync(window);
                    values.Add(watch.Elapsed.TotalMilliseconds);
                    EndAction(name, index);
                    // Keep the dispatcher available between events; this is not
                    // a blocking Thread.Sleep and is excluded from action time.
                    var remaining = TargetFrameMs - watch.Elapsed.TotalMilliseconds;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
                }
                var statistics = Describe(values);
                report.Operations.Add(new
                {
                    Name = name, Statistics = statistics, SamplesMs = values,
                    TargetP95Ms = TargetOperationMs, Passed100MsP95 = statistics.P95Ms <= TargetOperationMs,
                    Meets60HzLayoutBudgetP95 = statistics.P95Ms <= TargetFrameMs,
                    AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                    WorkingSetBefore = workingSet, WorkingSetAfter = System.Environment.WorkingSet,
                    Gc = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - gc[index]).ToArray()
                });
                File.WriteAllText(output, JsonSerializer.Serialize(report, JsonOptions));
                Console.WriteLine($"{name}: p50={statistics.P50Ms:F2} p95={statistics.P95Ms:F2} max={statistics.MaxMs:F2} ms; p95<=100: {statistics.P95Ms <= TargetOperationMs}");
            }

            void StartAction(string operation, int index)
            {
                _activeOperation = operation;
                Volatile.Write(ref _activeEvent, index);
                File.AppendAllText(sampleLog, JsonSerializer.Serialize(new { Operation = operation, Event = index, State = "started" }) + System.Environment.NewLine);
                Interlocked.Exchange(ref _actionStarted, Stopwatch.GetTimestamp());
            }

            void EndAction(string operation, int index)
            {
                var started = Interlocked.Exchange(ref _actionStarted, 0);
                File.AppendAllText(sampleLog, JsonSerializer.Serialize(new { Operation = operation, Event = index, State = "completed",
                    ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }) + System.Environment.NewLine);
            }
        }
        finally
        {
            heartbeat.Stop();
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Differ.RepairUiBench"));
            if (Path.GetDirectoryName(Path.GetFullPath(temporary)) == expectedParent && Directory.Exists(temporary))
                Directory.Delete(temporary, true);
        }
    }

    private static async Task DrainAsync(Window window)
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("The comparison never reached a ready state.");
            await Task.Delay(10);
        }
    }

    private static ResourceDictionary LoadResources(string sourceRoot)
    {
        var document = XDocument.Load(Path.Combine(sourceRoot, "src", "Differ.App", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = document.Root!.Element(presentation + "Application.Resources")!.Element(presentation + "ResourceDictionary")!;
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/Differ.App;component/App.xaml", UriKind.Absolute) };
        context.XmlnsDictionary.Add(string.Empty, presentation.NamespaceName);
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        return (ResourceDictionary)XamlReader.Parse(resources.ToString(), context);
    }

    private static T FindVisible<T>(DependencyObject root, Func<T, bool> predicate) where T : FrameworkElement =>
        FindVisuals<T>(root).First(value => value.IsVisible && predicate(value));

    private static bool IsInsideViewport(FrameworkElement element, ScrollViewer viewport)
    {
        var point = element.TransformToAncestor(viewport).Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return point.X >= 0 && point.Y >= 0 && point.X < viewport.ActualWidth && point.Y < viewport.ActualHeight;
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (var nested in FindVisuals<T>(child)) yield return nested;
        }
    }

    private static Statistics Describe(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        double Percentile(double value) => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * value) - 1, 0, sorted.Length - 1)];
        return new(values.Count, Percentile(0.5), Percentile(0.95), sorted.Length == 0 ? 0 : sorted[^1]);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed record Statistics(int Count, double P50Ms, double P95Ms, double MaxMs);
    private sealed class FixedTheme : ISystemThemeMonitor
    {
        public AppTheme CurrentTheme => AppTheme.Light;
        public event EventHandler? ThemeChanged { add { } remove { } }
    }
    private sealed class Report
    {
        public DateTimeOffset RecordedUtc { get; init; }
        public string SourceRoot { get; init; } = "";
        public string Version { get; init; } = "";
        public object? Environment { get; init; }
        public object? Budget { get; init; }
        public string Method { get; init; } = "";
        public List<object> Inputs { get; } = [];
        public List<object> Operations { get; } = [];
        public Dictionary<string, object> Heartbeat { get; set; } = [];
        public bool InputsUnchanged { get; set; }
        public long PeakWorkingSetBytes { get; set; }
        public string ActiveOperation { get; set; } = "";
    }
}
