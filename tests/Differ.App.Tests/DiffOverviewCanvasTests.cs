using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Differ.App.Controls;
using Differ.App.ViewModels;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class DiffOverviewCanvasTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(100_000)]
    public void BuildBucketsIsBoundedByAvailablePixels(int rowCount)
    {
        var rows = BuildRows(rowCount);

        var buckets = DiffOverviewCanvas.BuildBuckets(rows, 512);

        Assert.InRange(buckets.Length, 1, 512);
        Assert.Equal(1, DiffOverviewCanvas.VisualElementCount);
    }

    [Fact]
    public void BuildBucketsPreservesDifferenceMarkerWhenRowsAreAggregated()
    {
        var rows = BuildRows(10_000).ToArray();
        rows[4_999] = BuildRow(4_999, DiffKind.Modified);

        var buckets = DiffOverviewCanvas.BuildBuckets(rows, 100);

        Assert.Contains(
            buckets,
            bucket => bucket.Row.Kind == DiffKind.Modified);
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(49, 4)]
    [InlineData(99.9, 9)]
    [InlineData(100, 9)]
    [InlineData(180, 9)]
    public void ResolveBucketIndexTracksAndClampsPointerPosition(
        double pointerY,
        int expectedIndex)
    {
        Assert.Equal(
            expectedIndex,
            DiffOverviewCanvas.ResolveBucketIndex(pointerY, 100, 10));
    }

    [Fact]
    public void SelectingAcrossBucketsExecutesEachNewTargetOnce()
    {
        RunSta(() =>
        {
            var selected = new List<DiffRowViewModel>();
            var canvas = new DiffOverviewCanvas
            {
                Width = 40,
                Height = 100,
                SelectRowCommand = new RecordingCommand(
                    parameter => selected.Add(
                        Assert.IsType<DiffRowViewModel>(parameter)))
            };
            canvas.Measure(new Size(40, 100));
            canvas.Arrange(new Rect(0, 0, 40, 100));
            canvas.Rows = BuildRows(10);

            Assert.True(canvas.SelectBucketAt(5));
            Assert.True(canvas.SelectBucketAt(95));
            Assert.True(canvas.SelectBucketAt(95));

            Assert.Equal(2, selected.Count);
            Assert.Equal("row:0", selected[0].Path);
            Assert.Equal("row:9", selected[1].Path);
        });
    }

    [Fact]
    public void ShortDocumentKeepsNaturalTickSpacingAndHoverExposesRealContent()
    {
        RunSta(() =>
        {
            var canvas = new DiffOverviewCanvas { Width = 64, Height = 500 };
            canvas.Measure(new Size(64, 500));
            canvas.Arrange(new Rect(0, 0, 64, 500));
            canvas.Rows = BuildRows(8);
            OverviewPreviewEventArgs? preview = null;
            canvas.PreviewChanged += (_, value) => preview = value;

            Assert.InRange(canvas.ContentHeight, 48, 64);
            canvas.UpdateHover(35);
            Assert.NotNull(preview?.Row);
            Assert.Contains("base", preview!.BasePreview, StringComparison.Ordinal);
            Assert.Contains("mine", preview.MinePreview, StringComparison.Ordinal);
            Assert.InRange(preview.Position, 1, 8);
            canvas.UpdateHover(400);
            Assert.Null(preview?.Row);
        });
    }

    [Fact]
    public void AggregatedBucketPreviewPointsAtTheRepresentedDifference()
    {
        var rows = BuildRows(1_000);
        rows[543] = BuildRow(543, DiffKind.Modified) with { OldValue = "real before", NewValue = "real after" };
        var bucket = Assert.Single(DiffOverviewCanvas.BuildBuckets(rows, 1));
        Assert.Equal(543, bucket.RepresentativeIndex);
        Assert.Contains("real before", DiffOverviewCanvas.BuildPreview(rows, bucket.RepresentativeIndex, true), StringComparison.Ordinal);
        Assert.Contains("real after", DiffOverviewCanvas.BuildPreview(rows, bucket.RepresentativeIndex, false), StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewIsBoundedAndPreservesSurrogatePairs()
    {
        var rows = new[] { BuildRow(0, DiffKind.Modified) with { NewValue = new string('a', 179) + "😀" + new string('b', 1000) } };
        var preview = DiffOverviewCanvas.BuildPreview(rows, 0, false);
        Assert.True(preview.Length <= 181);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
        Assert.DoesNotContain(preview, char.IsSurrogate);
    }

    [Fact]
    public void ExpandingContextDoesNotCenterUsingAnObsoleteBucketIndex()
    {
        RunSta(() =>
        {
            var canvas = new DiffOverviewCanvas { Width = 64, Height = 200 };
            canvas.Measure(new Size(64, 200));
            canvas.Arrange(new Rect(0, 0, 64, 200));
            canvas.Rows = BuildRows(200);
            canvas.SelectRowCommand = new RecordingCommand(_ => canvas.Rows = BuildRows(2));
            var centeringRequests = 0;
            canvas.NavigationRequested += (_, _) => centeringRequests++;

            Assert.True(canvas.SelectBucketAt(180));
            Assert.Equal(0, centeringRequests);
            Assert.Equal(2, canvas.Rows.Count);
        });
    }

    private static DiffRowViewModel[] BuildRows(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => BuildRow(index, DiffKind.Unchanged))
            .ToArray();
    }

    private static DiffRowViewModel BuildRow(int index, DiffKind kind)
    {
        return new DiffRowViewModel(
            index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"row:{index}",
            kind,
            index,
            "base",
            index,
            "mine",
            null,
            0);
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

    private sealed class RecordingCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }
}
