using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class SpreadsheetSelectionVisualTests
{
    private static readonly string[] FrozenHeaderHostNames = ["BaseHeaderHost", "MineHeaderHost"];

    [Fact]
    public void ConfirmedHeadersAndKeyColumnsStayFrozenWhileBodyScrollsAndSelectionRemainsShared()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var model = BuildFrozenWorksheet();
            var rows = model.Rows;
            var view = new SpreadsheetWorksheetDiffView { DataContext = model };
            var resources = LoadApplicationResources();
            using var theme = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            theme.InitializeFromSystem();
            var window = new Window
            {
                Width = 1000,
                Height = 680,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = view
            };
            window.Resources.MergedDictionaries.Add(resources);
            try
            {
                window.Show(); FlushLayout(window);
                var body = Assert.IsType<DataGrid>(view.FindName("BaseSpreadsheetGrid"));
                var mineBody = Assert.IsType<DataGrid>(view.FindName("MineSpreadsheetGrid"));
                var header = Assert.IsType<DataGrid>(Assert.IsType<ContentControl>(view.FindName("BaseHeaderHost")).Content);
                var mineHeader = Assert.IsType<DataGrid>(Assert.IsType<ContentControl>(view.FindName("MineHeaderHost")).Content);
                Assert.Equal(6, header.Items.Count); Assert.Equal(294, body.Items.Count);
                Assert.Equal(3, body.FrozenColumnCount); Assert.Equal(3, header.FrozenColumnCount);
                Assert.Equal(DataGridHeadersVisibility.None, body.HeadersVisibility);
                var headerY = header.TransformToAncestor(window).Transform(new Point()).Y;
                var scroll = FindVisuals<ScrollViewer>(body).First();
                ScrollBar.ScrollToVerticalOffsetCommand.Execute(30d, scroll);
                ScrollBar.ScrollToVerticalOffsetCommand.Execute(150d, scroll);
                ScrollBar.ScrollToHorizontalOffsetCommand.Execute(140d, scroll);
                ScrollBar.ScrollToHorizontalOffsetCommand.Execute(280d, scroll);
                FlushLayout(window); FlushLayout(window);
                Assert.Equal(150, scroll.VerticalOffset, 1);
                Assert.Equal(280, scroll.HorizontalOffset, 1);
                Assert.Equal(headerY, header.TransformToAncestor(window).Transform(new Point()).Y);
                Assert.Equal(scroll.HorizontalOffset, FindVisuals<ScrollViewer>(header).First().HorizontalOffset, 1);
                Assert.Equal(scroll.HorizontalOffset, FindVisuals<ScrollViewer>(mineHeader).First().HorizontalOffset, 1);
                Assert.Equal(scroll.VerticalOffset, FindVisuals<ScrollViewer>(mineBody).First().VerticalOffset, 1);
                model.SelectCell(rows[3], 3); FlushLayout(window);
                Assert.Equal("D4", model.SelectedCell!.BaseAddress);
                Assert.Same(rows[3], Assert.Single(header.SelectedCells).Item);
                Assert.Same(rows[3], Assert.Single(mineHeader.SelectedCells).Item);
                Assert.Empty(body.SelectedCells);
                model.SelectCell(rows[200], 3); FlushLayout(window);
                Assert.Same(rows[200], Assert.Single(body.SelectedCells).Item);
                Assert.Empty(header.SelectedCells);
                model.ApplyFilter(DiffDisplayMode.Context); FlushLayout(window);
                Assert.Equal(6, model.FrozenHeaderRows.Count);
                Assert.DoesNotContain(model.ScrollableRows, model.IsFrozenHeader);
            }
            finally { window.Close(); FlushLayout(window); }
        });
    }

    [Fact]
    public void ThemeChangeRepaintsGridlinesWithoutRecreatingWorksheetContainers()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var model = BuildFrozenWorksheet();
            var view = new SpreadsheetWorksheetDiffView { DataContext = model };
            var resources = LoadApplicationResources();
            using var theme = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            theme.InitializeFromSystem();
            var window = CreateDragTestWindow(view, resources);
            try
            {
                window.Show();
                FlushLayout(window);
                var selectedRow = model.Rows[150];
                const int selectedColumn = 7;
                model.SelectCell(selectedRow, selectedColumn);
                FlushLayout(window);
                FlushLayout(window);

                var body = Assert.IsType<DataGrid>(view.FindName("BaseSpreadsheetGrid"));
                var mine = Assert.IsType<DataGrid>(view.FindName("MineSpreadsheetGrid"));
                var selectedCell = GetCell(body, selectedRow, selectedColumn + 1);
                var separatorColour = Assert.IsType<SolidColorBrush>(selectedCell.BorderBrush).Color;
                var scroll = BodyScroll(view, baseSide: true);
                var verticalOffset = scroll.ContentVerticalOffset;
                var horizontalOffset = scroll.ContentHorizontalOffset;
                Assert.True(verticalOffset > 0);
                Assert.True(horizontalOffset > 0);
                var grids = new[] { body, mine }.Concat(FrozenHeaderHostNames.Select(name =>
                    Assert.IsType<DataGrid>(Assert.IsType<ContentControl>(view.FindName(name)).Content))).ToArray();
                var rows = grids.Select(grid => FindVisuals<DataGridRow>(grid).First()).ToArray();
                var items = rows.Select(row => row.Item).ToArray();

                theme.Apply(AppTheme.Dark);
                FlushLayout(window);

                for (var index = 0; index < grids.Length; index++)
                    Assert.Same(rows[index], grids[index].ItemContainerGenerator.ContainerFromItem(items[index]));
                Assert.Same(selectedCell, GetCell(body, selectedRow, selectedColumn + 1));
                Assert.NotEqual(separatorColour, Assert.IsType<SolidColorBrush>(selectedCell.BorderBrush).Color);
                Assert.Same(selectedRow, Assert.Single(body.SelectedCells).Item);
                Assert.Same(selectedRow, Assert.Single(mine.SelectedCells).Item);
                Assert.Equal(verticalOffset, scroll.ContentVerticalOffset);
                Assert.Equal(horizontalOffset, scroll.ContentHorizontalOffset);
            }
            finally
            {
                window.Close();
                FlushLayout(window);
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeHorizontalScrollbarDragPaintsBothPanesBeforeReleaseAndCommitsFinalOffset(bool baseSide)
    {
        ThemeTestSupport.RunSta(() =>
        {
            const Orientation orientation = Orientation.Horizontal;
            var view = new SpreadsheetWorksheetDiffView { DataContext = BuildFrozenWorksheet(mineRowOffset: 7) };
            var resources = LoadApplicationResources();
            using var theme = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            theme.InitializeFromSystem();
            var window = CreateDragTestWindow(view, resources);
            try
            {
                window.Show();
                FlushLayout(window);
                var surface = Assert.IsType<SpreadsheetDragSurface>(view.FindName("DragSurface"));
                var baseScroll = BodyScroll(view, baseSide: true);
                var mineScroll = BodyScroll(view, baseSide: false);
                var source = baseSide ? baseScroll : mineScroll;
                var follower = baseSide ? mineScroll : baseScroll;
                var thumb = FindScrollThumb(source, orientation);
                var initialOffset = ScrollOffset(source, orientation, content: true);

                StartDrag(thumb);
                MoveThumb(thumb, orientation, 45);
                FlushLayout(window);
                var firstOffset = ScrollOffset(source, orientation, content: false);
                Assert.True(firstOffset > initialOffset);
                AssertDraggingAt(surface, source, follower, orientation, initialOffset);

                MoveThumb(thumb, orientation, 60);
                FlushLayout(window);
                var finalOffset = ScrollOffset(source, orientation, content: false);
                Assert.True(finalOffset > firstOffset);
                AssertDraggingAt(surface, source, follower, orientation, initialOffset);

                CompleteDrag(thumb, orientation, 105, canceled: false);
                FlushLayout(window);
                FlushLayout(window);
                Assert.False(surface.IsActive);
                AssertOffsetsMatch(finalOffset, ScrollOffset(source, orientation, content: true));
                AssertOffsetsMatch(finalOffset, ScrollOffset(follower, orientation, content: true));
                Assert.False(source.IsDeferredScrollingEnabled);
                Assert.False(follower.IsDeferredScrollingEnabled);

                foreach (var hostName in FrozenHeaderHostNames)
                {
                    var host = Assert.IsType<ContentControl>(view.FindName(hostName));
                    var header = Assert.IsType<DataGrid>(host.Content);
                    Assert.Equal(3, header.FrozenColumnCount);
                    AssertOffsetsMatch(finalOffset, FindVisuals<ScrollViewer>(header).First().ContentHorizontalOffset);
                }
            }
            finally
            {
                window.Close();
                FlushLayout(window);
            }
        });
    }

    [Fact]
    public void UnifiedVerticalRailPaintsBothBodiesDuringDragAndCommitsTheDisplayedRows()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var view = new SpreadsheetWorksheetDiffView { DataContext = BuildFrozenWorksheet(mineRowOffset: 7) };
            var resources = LoadApplicationResources();
            using var theme = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            theme.InitializeFromSystem();
            var window = CreateDragTestWindow(view, resources);
            try
            {
                window.Show();
                FlushLayout(window);
                var surface = Assert.IsType<SpreadsheetDragSurface>(view.FindName("DragSurface"));
                var rail = Assert.IsType<SpreadsheetScrollRail>(view.FindName("VerticalRail"));
                var baseScroll = BodyScroll(view, baseSide: true);
                var mineScroll = BodyScroll(view, baseSide: false);
                var headers = FrozenHeaderHostNames.Select(name =>
                    Assert.IsType<ContentControl>(view.FindName(name))).ToArray();
                var headerPositions = headers.Select(header => header.TranslatePoint(new Point(), window).Y).ToArray();
                Assert.DoesNotContain(new[] { baseScroll, mineScroll }.SelectMany(FindVisuals<ScrollBar>),
                    bar => bar.Orientation == Orientation.Vertical && bar.IsVisible);

                var initialOffset = baseScroll.ContentVerticalOffset;
                var band = rail.ViewportBand;
                Assert.False(band.IsEmpty);
                var start = band.Top + band.Height / 2;
                Assert.True(rail.BeginViewportDrag(start));
                rail.DragViewportTo(start + 45);
                FlushLayout(window);
                Assert.True(surface.IsActive);
                Assert.True(surface.RenderedStartIndex > initialOffset);
                var firstOffset = surface.RenderedStartIndex;
                var firstFrame = CapturePanePixels(view, surface);
                var firstBandTop = rail.ViewportBand.Top;

                rail.DragViewportTo(start + 105);
                FlushLayout(window);
                Assert.True(surface.IsActive);
                Assert.True(surface.RenderedStartIndex > firstOffset);
                Assert.True(rail.ViewportBand.Top > firstBandTop);
                AssertOffsetsMatch(initialOffset, baseScroll.ContentVerticalOffset);
                AssertOffsetsMatch(initialOffset, mineScroll.ContentVerticalOffset);
                var finalOffset = surface.RenderedStartIndex;
                var secondFrame = CapturePanePixels(view, surface);
                Assert.False(firstFrame[0].AsSpan().SequenceEqual(secondFrame[0]), "BASE body did not repaint while held.");
                Assert.False(firstFrame[1].AsSpan().SequenceEqual(secondFrame[1]), "MINE body did not repaint while held.");
                Assert.Equal(headerPositions, headers.Select(header => header.TranslatePoint(new Point(), window).Y));

                rail.EndViewportDrag();
                FlushLayout(window);
                FlushLayout(window);
                Assert.False(surface.IsActive);
                AssertOffsetsMatch(finalOffset, baseScroll.ContentVerticalOffset);
                AssertOffsetsMatch(finalOffset, mineScroll.ContentVerticalOffset);
                Assert.Equal(headerPositions, headers.Select(header => header.TranslatePoint(new Point(), window).Y));

                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.MouseWheelEvent
                };
                rail.RaiseEvent(wheel);
                FlushLayout(window);
                Assert.True(wheel.Handled);
                if (SystemParameters.WheelScrollLines != 0)
                {
                    Assert.True(baseScroll.ContentVerticalOffset > finalOffset);
                }
                AssertOffsetsMatch(baseScroll.ContentVerticalOffset, mineScroll.ContentVerticalOffset);
            }
            finally
            {
                window.Close();
                FlushLayout(window);
            }
        });
    }

    [Fact]
    public void HidingAndDetachingWorksheetClearAnActiveScrollbarDrag()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var view = new SpreadsheetWorksheetDiffView { DataContext = BuildFrozenWorksheet() };
            var resources = LoadApplicationResources();
            using var theme = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            theme.InitializeFromSystem();
            var window = CreateDragTestWindow(view, resources);
            try
            {
                window.Show();
                FlushLayout(window);
                var surface = Assert.IsType<SpreadsheetDragSurface>(view.FindName("DragSurface"));
                var baseScroll = BodyScroll(view, baseSide: true);
                var mineScroll = BodyScroll(view, baseSide: false);
                var rail = Assert.IsType<SpreadsheetScrollRail>(view.FindName("VerticalRail"));

                var start = rail.ViewportBand.Top + rail.ViewportBand.Height / 2;
                Assert.True(rail.BeginViewportDrag(start));
                rail.DragViewportTo(start + 35);
                FlushLayout(window);
                Assert.True(surface.IsActive);
                view.Visibility = Visibility.Hidden;
                FlushLayout(window);
                Assert.False(surface.IsActive);
                Assert.False(baseScroll.IsDeferredScrollingEnabled);
                Assert.False(mineScroll.IsDeferredScrollingEnabled);

                view.Visibility = Visibility.Visible;
                FlushLayout(window);
                start = rail.ViewportBand.Top + rail.ViewportBand.Height / 2;
                Assert.True(rail.BeginViewportDrag(start));
                rail.DragViewportTo(start + 40);
                FlushLayout(window);
                Assert.True(surface.IsActive);
                window.Content = null;
                FlushLayout(window);
                Assert.False(surface.IsActive);
                Assert.False(baseScroll.IsDeferredScrollingEnabled);
                Assert.False(mineScroll.IsDeferredScrollingEnabled);

                window.Content = view;
                FlushLayout(window);
                Assert.False(surface.IsActive);
                baseScroll = BodyScroll(view, baseSide: true);
                mineScroll = BodyScroll(view, baseSide: false);
                ScrollBar.ScrollToVerticalOffsetCommand.Execute(30d, baseScroll);
                FlushLayout(window);
                AssertOffsetsMatch(30, baseScroll.ContentVerticalOffset);
                AssertOffsetsMatch(30, mineScroll.ContentVerticalOffset);
            }
            finally
            {
                window.Close();
                FlushLayout(window);
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void SelectionKeepsFourSidesDiffContentAndLayoutAcrossNativeSelectionAndRecycling(AppTheme theme)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var model = BuildWorksheet();
            model.SelectCell(model.Rows[0], 0);
            var view = new SpreadsheetWorksheetDiffView { DataContext = model };
            var resources = LoadApplicationResources();
            using var themes = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(theme));
            themes.InitializeFromSystem();
            var window = new Window
            {
                Width = 980,
                Height = 680,
                Left = -20_000,
                Top = -20_000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
                Content = view
            };
            window.Resources.MergedDictionaries.Add(resources);
            try
            {
                window.Show();
                FlushLayout(window);
                var grids = new[]
                {
                    Assert.IsType<DataGrid>(view.FindName("BaseSpreadsheetGrid")),
                    Assert.IsType<DataGrid>(view.FindName("MineSpreadsheetGrid"))
                };
                var changedCells = grids.Select(grid => GetCell(grid, model.Rows[0], 2)).ToArray();
                Assert.All(changedCells, cell =>
                {
                    Assert.IsType<SpreadsheetValueColumn>(cell.Column);
                    var presenter = Assert.Single(FindVisuals<SpreadsheetCellPresenter>(cell));
                    Assert.Empty(FindVisuals<ContentPresenter>(presenter));
                });
                var before = changedCells.Select(Snapshot).ToArray();
                model.SelectCell(model.Rows[0], 1);
                FlushLayout(window);

                for (var index = 0; index < grids.Length; index++)
                {
                    var cell = changedCells[index];
                    AssertSelectedCell(grids[index], model.Rows[0], 2);
                    Assert.Equal(before[index], Snapshot(cell));
                    var chrome = FindVisuals<Border>(cell).Single(border => border.Name == "CellChrome");
                    var sideKind = index == 0 ? "Deleted" : "Added";
                    Assert.Same(cell.FindResource("Diff.Cell." + sideKind), chrome.Background);
                    var text = Assert.Single(FindVisuals<InlineDiffTextBlock>(cell));
                    var changedRun = Assert.Single(text.Inlines.OfType<Run>(), run => run.FontWeight == FontWeights.SemiBold);
                    Assert.Same(cell.FindResource("Diff.Inline." + sideKind + ".Brush"), changedRun.Foreground);
                    Assert.Null(changedRun.Background);
                    foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
                    {
                        AssertRenderedFourSides(cell, scale);
                    }
                }

                // Exercise the native cell selection path without activating
                // the offscreen window or stealing desktop keyboard focus.
                var nextColumnCell = GetCell(grids[1], model.Rows[0], 3);
                Assert.True(nextColumnCell.Focusable);
                Assert.True(KeyboardNavigation.GetIsTabStop(nextColumnCell));
                var peer = new DataGridCellItemAutomationPeer(model.Rows[0], grids[1].Columns[3]);
                var provider = Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
                provider.Select();
                FlushLayout(window);
                Assert.Equal(2, model.SelectedCell?.ColumnIndex);
                Assert.All(grids, grid => AssertSelectedCell(grid, model.Rows[0], 3));
                Assert.All(changedCells, cell => Assert.Null(Frame(cell).SelectionBrush));

                // Changing the other side uses the same native selection event
                // as a mouse click and must mirror the complete frame.
                var next = new DataGridCellInfo(model.Rows[1], grids[0].Columns[1]);
                grids[0].SelectedCells.Clear();
                grids[0].SelectedCells.Add(next);
                grids[0].CurrentCell = next;
                FlushLayout(window);
                Assert.Equal("A2", model.SelectedCell?.MineAddress);
                Assert.All(grids, grid => AssertSelectedCell(grid, model.Rows[1], 1));

                model.SelectCell(model.Rows[200], 1);
                FlushLayout(window);
                Assert.All(grids, grid => AssertSelectedCell(grid, model.Rows[200], 2));
                Assert.All(grids, grid =>
                {
                    Assert.True(FindVisuals<DataGridRow>(grid).Count() < model.Rows.Count);
                    Assert.All(FindVisuals<DataGridCell>(grid), cell =>
                        Assert.Equal(cell.IsSelected, Frame(cell).SelectionBrush is not null));
                });
                model.SelectCell(model.Rows[0], 1);
                FlushLayout(window);
                Assert.All(grids, grid => AssertSelectedCell(grid, model.Rows[0], 2));
            }
            finally
            {
                window.Close();
                FlushLayout(window);
            }
        });
    }

    private static void AssertSelectedCell(DataGrid grid, SpreadsheetRowViewModel row, int column)
    {
        var selected = Assert.Single(grid.SelectedCells);
        Assert.Same(row, selected.Item);
        Assert.Same(grid.Columns[column], selected.Column);
        var cell = GetCell(grid, row, column);
        var frame = Frame(cell);
        Assert.True(cell.IsSelected);
        Assert.Equal(2, SpreadsheetCellFrame.SelectionThickness);
        Assert.Same(cell.FindResource("Dui.Focus"), frame.SelectionBrush);
        Assert.IsType<ContentPresenter>(frame.Child);
        var hit = VisualTreeHelper.HitTest(frame, new Point(1, frame.ActualHeight / 2));
        Assert.False(hit?.VisualHit is DrawingVisual, "The selection outline cannot take input from cell content.");
    }

    private static (Size Cell, Rect Text, Color Background) Snapshot(DataGridCell cell)
    {
        var text = Assert.Single(FindVisuals<InlineDiffTextBlock>(cell));
        var chrome = FindVisuals<Border>(cell).Single(border => border.Name == "CellChrome");
        return (cell.RenderSize, text.TransformToAncestor(cell).TransformBounds(new Rect(text.RenderSize)),
            Assert.IsType<SolidColorBrush>(chrome.Background).Color);
    }

    private static SpreadsheetCellFrame Frame(DataGridCell cell) => Assert.IsType<SpreadsheetCellFrame>(
        cell.Template.FindName("CellFrame", cell));

    private static void AssertRenderedFourSides(DataGridCell cell, double scale)
    {
        var width = (int)Math.Ceiling(cell.ActualWidth * scale);
        var height = (int)Math.Ceiling(cell.ActualHeight * scale);
        // Render the real viewport before cropping: a DataGridCell carries
        // ancestor clipping and cannot be rendered as a detached bitmap root.
        var root = Assert.IsAssignableFrom<FrameworkElement>(Window.GetWindow(cell).Content);
        var bounds = cell.TransformToAncestor(root).TransformBounds(new Rect(cell.RenderSize));
        var viewport = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
            (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        viewport.Render(root);
        var bitmap = new CroppedBitmap(viewport, new Int32Rect((int)Math.Round(bounds.Left * scale),
            (int)Math.Round(bounds.Top * scale), width, height));
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var colour = Assert.IsType<SolidColorBrush>(Frame(cell).SelectionBrush).Color;
        var background = Assert.IsType<SolidColorBrush>(FindVisuals<SpreadsheetCellPresenter>(cell).Single().Background).Color;
        var diagnostics = Environment.GetEnvironmentVariable("DIFFER_SELECTION_DEBUG");
        if (!string.IsNullOrWhiteSpace(diagnostics))
        {
            Directory.CreateDirectory(diagnostics);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(diagnostics, $"cell-{cell.FindResource("Theme.Name")}-{scale}.png"));
            encoder.Save(stream);
        }
        bool IsOutline(int x, int y)
        {
            var offset = (y * width + x) * 4;
            var rendered = Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            var contrast = ColourDistance(colour, background);
            // Rendering a live per-monitor-DPI tree at another output DPI can
            // blend its snapped edge. Require a visible selection-colour
            // contribution, not exact RGB identity after raster resampling.
            return ColourDistance(rendered, colour) < contrast * 0.8
                && ColourDistance(rendered, background) > contrast * 0.3
                && pixels[offset + 3] >= 250;
        }
        var band = (int)Math.Ceiling(3 * scale);
        Assert.True(Enumerable.Range(0, band).Any(x => IsOutline(x, height / 2)),
            $"Missing left outline at {scale}: cell={cell.RenderSize}, frame={Frame(cell).RenderSize}, colour={colour}, pixels={string.Join(',', pixels.Skip(height / 2 * width * 4).Take(32))}");
        Assert.Contains(Enumerable.Range(width - band, band), x => IsOutline(x, height / 2));
        Assert.Contains(Enumerable.Range(0, band), y => IsOutline(width / 2, y));
        Assert.Contains(Enumerable.Range(height - band, band), y => IsOutline(width / 2, y));
    }

    private static double ColourDistance(Color first, Color second) => Math.Sqrt(
        Math.Pow(first.R - second.R, 2) + Math.Pow(first.G - second.G, 2) + Math.Pow(first.B - second.B, 2));

    private static DataGridCell GetCell(DataGrid grid, SpreadsheetRowViewModel row, int column)
    {
        var container = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(row));
        return Assert.Single(FindVisuals<DataGridCell>(container), cell => cell.Column.DisplayIndex == column);
    }

    private static Window CreateDragTestWindow(SpreadsheetWorksheetDiffView view, ResourceDictionary resources)
    {
        var window = new Window
        {
            Width = 1000,
            Height = 680,
            Left = -20_000,
            Top = -20_000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = view
        };
        window.Resources.MergedDictionaries.Add(resources);
        return window;
    }

    private static ScrollViewer BodyScroll(SpreadsheetWorksheetDiffView view, bool baseSide) =>
        FindVisuals<ScrollViewer>(Assert.IsType<DataGrid>(view.FindName(baseSide ? "BaseSpreadsheetGrid" : "MineSpreadsheetGrid"))).First();

    private static Thumb FindScrollThumb(ScrollViewer viewer, Orientation orientation)
    {
        var bar = Assert.Single(FindVisuals<ScrollBar>(viewer), value => value.Orientation == orientation && value.IsVisible);
        var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
        return Assert.IsType<Thumb>(track.Thumb);
    }

    private static void StartDrag(Thumb thumb) =>
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });

    private static void MoveThumb(Thumb thumb, Orientation orientation, double distance) =>
        thumb.RaiseEvent(new DragDeltaEventArgs(orientation == Orientation.Horizontal ? distance : 0,
            orientation == Orientation.Vertical ? distance : 0)
        {
            RoutedEvent = Thumb.DragDeltaEvent
        });

    private static void CompleteDrag(Thumb thumb, Orientation orientation, double distance, bool canceled) =>
        thumb.RaiseEvent(new DragCompletedEventArgs(orientation == Orientation.Horizontal ? distance : 0,
            orientation == Orientation.Vertical ? distance : 0, canceled)
        {
            RoutedEvent = Thumb.DragCompletedEvent
        });

    private static double ScrollOffset(ScrollViewer viewer, Orientation orientation, bool content)
    {
        if (content)
        {
            return orientation == Orientation.Vertical ? viewer.ContentVerticalOffset : viewer.ContentHorizontalOffset;
        }

        // The native thumb binds to this DP. The CLR offset getter returns the parked grid's cached offset.
        return (double)viewer.GetValue(orientation == Orientation.Vertical
            ? ScrollViewer.VerticalOffsetProperty : ScrollViewer.HorizontalOffsetProperty);
    }

    private static void AssertOffsetsMatch(double expected, double actual) =>
        Assert.InRange(actual, expected - 1, expected + 1);

    private static void AssertDraggingAt(SpreadsheetDragSurface surface, ScrollViewer source, ScrollViewer follower,
        Orientation orientation, double initialOffset)
    {
        Assert.True(surface.IsActive);
        var displayedOffset = orientation == Orientation.Vertical ? surface.RenderedStartIndex : surface.RenderedHorizontalOffset;
        var sourceOffset = ScrollOffset(source, orientation, content: false);
        AssertOffsetsMatch(sourceOffset, displayedOffset);
        AssertOffsetsMatch(sourceOffset, ScrollOffset(follower, orientation, content: false));
        AssertOffsetsMatch(initialOffset, ScrollOffset(source, orientation, content: true));
        AssertOffsetsMatch(initialOffset, ScrollOffset(follower, orientation, content: true));
    }

    private static byte[][] CapturePanePixels(SpreadsheetWorksheetDiffView view, SpreadsheetDragSurface surface)
    {
        var viewport = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        viewport.Render(view);
        var bounds = surface.TransformToAncestor(view).TransformBounds(new Rect(surface.RenderSize));
        var y = (int)(bounds.Top + bounds.Height * 0.45);
        var height = Math.Min(120, viewport.PixelHeight - y);
        var width = Math.Min(180, (int)(bounds.Width / 4));
        return [Capture((int)(bounds.Left + bounds.Width / 8)), Capture((int)(bounds.Left + bounds.Width * 5 / 8))];

        byte[] Capture(int x)
        {
            var body = new CroppedBitmap(viewport, new Int32Rect(x, y, width, height));
            var pixels = new byte[width * height * 4];
            body.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }
    }

    private static WorksheetTabViewModel BuildFrozenWorksheet(int mineRowOffset = 0)
    {
        var columns = Enumerable.Range(0, 12).Select(index => new SpreadsheetColumnViewModel(
            "logical" + index, 100, index == 3)
        { BaseColumn = ((char)('A' + index)).ToString(), MineColumn = ((char)('A' + index)).ToString() }).ToArray();
        var rows = Enumerable.Range(1, 300).Select(number =>
        {
            var baseCells = BuildCells(number);
            var mineNumber = number > 6 ? number + mineRowOffset : number;
            var mineCells = mineNumber == number ? baseCells : BuildCells(mineNumber);
            return new SpreadsheetRowViewModel("row" + number, number, mineNumber, DiffKind.Unchanged, baseCells, mineCells);
        }).ToArray();
        return new WorksheetTabViewModel("配置", 0, new SpreadsheetSectionViewModel("配置", columns, rows, 2, 6), []);

        static SpreadsheetCellViewModel[] BuildCells(int number) => Enumerable.Range(0, 12).Select(index => new SpreadsheetCellViewModel(
            $"{(char)('A' + index)}{number}", $"数据 {number}:{index}", $"数据 {number}:{index}", "details",
            DiffKind.Unchanged, [new InlineDiffSegment($"数据 {number}:{index}", false)],
            [new InlineDiffSegment($"数据 {number}:{index}", false)])).ToArray();
    }

    private static WorksheetTabViewModel BuildWorksheet()
    {
        var columns = new[]
        {
            new SpreadsheetColumnViewModel("A", 100, false),
            new SpreadsheetColumnViewModel("B", 140, true),
            new SpreadsheetColumnViewModel("C", 100, false)
        };
        var rows = Enumerable.Range(1, 240).Select(rowIndex =>
        {
            SpreadsheetCellViewModel Cell(int columnIndex, bool mine)
            {
                var changed = rowIndex == 1 && columnIndex == 1;
                var kind = changed ? DiffKind.Modified : DiffKind.Unchanged;
                var address = columns[columnIndex].Key + rowIndex.ToString(CultureInfo.InvariantCulture);
                var value = changed ? mine ? "240" : "120" : rowIndex.ToString(CultureInfo.InvariantCulture);
                var segments = new[] { new InlineDiffSegment("value ", false), new InlineDiffSegment(value, changed) };
                return new SpreadsheetCellViewModel(address, "value " + value, "value " + value,
                    address, kind, segments, segments)
                {
                    IsContentReplacement = changed
                };
            }
            return new SpreadsheetRowViewModel("row:" + rowIndex, rowIndex, rowIndex,
                rowIndex == 1 ? DiffKind.Modified : DiffKind.Unchanged,
                Enumerable.Range(0, 3).Select(column => Cell(column, false)).ToArray(),
                Enumerable.Range(0, 3).Select(column => Cell(column, true)).ToArray());
        }).ToArray();
        return new WorksheetTabViewModel("Selection", 1,
            new SpreadsheetSectionViewModel("Selection", columns, rows), []);
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "DEVELOPMENT_SPEC.md")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory.FullName, "src", "Differ.App", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = document.Root!.Element(presentation + "Application.Resources")!
            .Element(presentation + "ResourceDictionary")!;
        var context = new ParserContext
        {
            BaseUri = new Uri("pack://application:,,,/Differ.App;component/App.xaml", UriKind.Absolute)
        };
        context.XmlnsDictionary.Add(string.Empty, presentation.NamespaceName);
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        return Assert.IsType<ResourceDictionary>(XamlReader.Parse(resources.ToString(), context));
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisuals<T>(child)) yield return descendant;
        }
    }

    private static void FlushLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
}
