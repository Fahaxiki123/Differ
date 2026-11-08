using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Differ.App.Controls;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class InteractionVisualTests
{
    [Fact]
    public void ChangedTextSegmentsUseForegroundWithoutInlineBackground()
    {
        RunSta(() =>
        {
            var textControl = new InlineDiffTextBlock
            {
                DiffKind = DiffKind.Modified,
                Segments = BuildSegments()
            };
            var wordControl = new WordInlineDiffTextBlock
            {
                DiffKind = DiffKind.Modified,
                Side = WordDiffSide.Base,
                Segments = BuildSegments()
            };

            AssertChangedRunHasSingleLayer(textControl);
            AssertChangedRunHasSingleLayer(wordControl);
        });
    }

    [Fact]
    public void WordSelectionOverlayDoesNotChangeRowHeight()
    {
        RunSta(() =>
        {
            var view = new WordDiffView();
            var item = new ListBoxItem
            {
                Content = new Border { Height = 42 },
                Style = Assert.IsType<Style>(
                    view.Resources["WordRowItemStyle"])
            };
            var constraint = new Size(500, double.PositiveInfinity);

            item.Measure(constraint);
            var unselectedHeight = item.DesiredSize.Height;
            item.IsSelected = true;
            item.Measure(constraint);

            Assert.Equal(unselectedHeight, item.DesiredSize.Height);
        });
    }

    [Fact]
    public void TextAndSpreadsheetScrollbarsRenderWhileDragging()
    {
        RunSta(() =>
        {
            var textView = new TextDiffView();
            var spreadsheetView = new SpreadsheetWorksheetDiffView();

            AssertLiveScrolling(textView, "BaseTextGrid");
            AssertLiveScrolling(textView, "MineTextGrid");
            AssertLiveScrolling(spreadsheetView, "BaseSpreadsheetGrid");
            AssertLiveScrolling(spreadsheetView, "MineSpreadsheetGrid");
        });
    }

    private static InlineDiffSegment[] BuildSegments()
    {
        return
        [
            new InlineDiffSegment("same", false),
            new InlineDiffSegment("changed", true)
        ];
    }

    private static void AssertChangedRunHasSingleLayer(TextBlock control)
    {
        var changedRun = Assert.IsType<Run>(control.Inlines.LastInline);
        Assert.Null(changedRun.Background);
        Assert.NotNull(changedRun.Foreground);
        Assert.Equal(FontWeights.SemiBold, changedRun.FontWeight);
    }

    private static void AssertLiveScrolling(
        FrameworkElement view,
        string gridName)
    {
        var grid = Assert.IsType<DataGrid>(view.FindName(gridName));
        Assert.False(ScrollViewer.GetIsDeferredScrollingEnabled(grid));
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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
    }
}
