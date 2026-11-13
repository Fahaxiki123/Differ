using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class TabStripOverflowLayoutTests
{
    [Theory]
    [InlineData(false, 720)]
    [InlineData(false, 980)]
    [InlineData(false, 1500)]
    [InlineData(true, 980)]
    [InlineData(true, 1500)]
    public void OverflowKeepsWholeTabsVisibleAndCompactHeightReturns(bool worksheet, double width)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var preferences = new UserPreferencesService();
            using var scheduler = new ComparisonLoadScheduler();
            using var workspace = new ComparisonWorkspaceViewModel(new DiffCoordinator([]), scheduler,
                new ExternalDiffSessionStore(Path.Combine(Path.GetTempPath(), "Differ.App.Tests", Guid.NewGuid().ToString("N"))),
                NullLoggerFactory.Instance, preferences);
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Controls.xaml"));
            using var themes = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            themes.InitializeFromSystem();

            var spreadsheet = worksheet ? new SpreadsheetDiffView() : null;
            if (spreadsheet is not null) spreadsheet.DataContext = new { HasWorksheetTabs = true };
            Window window = spreadsheet is not null
                ? new Window { Content = spreadsheet }
                : new DiffWindow(workspace, themes);
            window.Resources.MergedDictionaries.Add(resources);
            window.Width = width;
            window.Height = 680;
            window.Left = -8_000;
            window.Top = -8_000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (worksheet)
            {
                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
            }
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            window.FontSize = 13;
            window.UseLayoutRounding = true;

            var tabs = Assert.IsType<ListBox>(spreadsheet is not null
                ? spreadsheet.FindName("WorksheetTabs")
                : window.FindName("FileTabs"));
            var tabBar = Assert.IsType<Border>(spreadsheet is not null
                ? spreadsheet.FindName("WorksheetFooter")
                : window.FindName("FileTabBar"));
            var compactHeight = worksheet ? 31d : 38d;
            // Use the production templates without starting file parsing or
            // replacing their selection bindings with incompatible view models.
            BindingOperations.ClearBinding(tabs, ItemsControl.ItemsSourceProperty);
            BindingOperations.ClearBinding(tabs, System.Windows.Controls.Primitives.Selector.SelectedItemProperty);
            var items = Enumerable.Range(1, 10)
                .Select(index => new TabLabel(worksheet
                    ? $"提交前审阅_中文长名称_工作表及文件标签_{index:D2}"
                    : $"提交前审阅_一个足够长需要截断的中文文件名称_多个标签同时打开_{index:D2}"))
                .ToArray();
            tabs.ItemsSource = items;
            tabs.SelectedIndex = 0;

            try
            {
                window.Show();
                FlushLayout(window);
                var scroll = Assert.Single(FindVisuals<ScrollViewer>(tabs));
                var tolerance = 1 / VisualTreeHelper.GetDpi(window).DpiScaleY;
                Assert.True(scroll.ScrollableWidth > 0);
                Assert.Equal(worksheet ? Visibility.Visible : Visibility.Collapsed, scroll.ComputedHorizontalScrollBarVisibility);
                var firstTab = AssertTabFitsViewport(tabs, items[0], scroll);
                if (worksheet)
                {
                    Assert.True(tabBar.ActualHeight > compactHeight);
                }
                else
                {
                    Assert.InRange(tabBar.ActualHeight, compactHeight - tolerance, compactHeight + tolerance);
                    AssertOverflowLabel(window, visible: true);
                    AssertCaptionHitTargets(window, firstTab);
                    AssertToolbarFits(window, tolerance);
                }

                tabs.SelectedIndex = items.Length - 1;
                FlushLayout(window);
                Assert.True(scroll.HorizontalOffset > 0);
                Assert.Same(items[^1], tabs.SelectedItem);
                AssertTabFitsViewport(tabs, items[^1], scroll);

                var overflowHeight = tabBar.ActualHeight;
                themes.Apply(AppTheme.Dark);
                FlushLayout(window);
                Assert.Equal(overflowHeight, tabBar.ActualHeight);
                AssertTabFitsViewport(tabs, items[^1], scroll);
                if (!worksheet) AssertToolbarFits(window, tolerance);

                tabs.ItemsSource = items.Take(worksheet || width == 720 ? 1 : 2).ToArray();
                tabs.SelectedIndex = 0;
                FlushLayout(window);
                Assert.Equal(0, scroll.ScrollableWidth, tolerance);
                Assert.Equal(Visibility.Collapsed, scroll.ComputedHorizontalScrollBarVisibility);
                Assert.InRange(tabBar.ActualHeight, compactHeight - tolerance, compactHeight + tolerance);
                AssertTabFitsViewport(tabs, items[0], scroll);
                if (!worksheet) AssertOverflowLabel(window, visible: false);
            }
            finally
            {
                window.Close();
                FlushLayout(window);
            }
        });
    }

    private static ListBoxItem AssertTabFitsViewport(ListBox tabs, object item, ScrollViewer scroll)
    {
        var container = Assert.IsType<ListBoxItem>(tabs.ItemContainerGenerator.ContainerFromItem(item));
        var viewport = Assert.Single(FindVisuals<ScrollContentPresenter>(scroll));
        var bounds = container.TransformToAncestor(viewport).TransformBounds(new Rect(container.RenderSize));
        var tolerance = 1 / VisualTreeHelper.GetDpi(tabs).DpiScaleY;
        Assert.True(bounds.Top >= -tolerance && bounds.Bottom <= viewport.ActualHeight + tolerance,
            $"Tab is vertically clipped: bounds={bounds}, viewport={viewport.RenderSize}.");
        Assert.True(bounds.Left >= -tolerance && bounds.Right <= viewport.ActualWidth + tolerance,
            $"Selected tab cannot be fully scrolled into view: bounds={bounds}, viewport={viewport.RenderSize}.");
        if (tabs.Clip is RectangleGeometry clip)
        {
            var clipBounds = container.TransformToAncestor(tabs).TransformBounds(new Rect(container.RenderSize));
            Assert.True(clipBounds.Left >= clip.Rect.Left - tolerance && clipBounds.Right <= clip.Rect.Right + tolerance,
                $"Selected tab is clipped by the title bar: bounds={clipBounds}, clip={clip.Rect}.");
        }

        return container;
    }

    private static void AssertCaptionHitTargets(Window window, ListBoxItem tab)
    {
        const int client = 1;
        const int caption = 2;
        const int minimize = 8;
        const int maximize = 9;
        const int closeWindow = 20;
        var dragArea = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("CaptionDragArea"));
        var filter = Assert.IsType<Button>(window.FindName("FilterTabsButton"));
        var minimizeButton = Assert.IsType<Button>(window.FindName("MinimizeCaptionButton"));
        var maximizeButton = Assert.IsType<Button>(window.FindName("MaximizeRestoreCaptionButton"));
        var closeButton = Assert.IsType<Button>(window.FindName("CloseCaptionButton"));
        var close = Assert.Single(FindVisuals<Button>(tab));
        Assert.True(dragArea.ActualWidth > 0);
        Assert.Equal(client, CaptionHitTest(window, tab));
        Assert.Equal(client, CaptionHitTest(window, close));
        Assert.Equal(client, CaptionHitTest(window, filter));
        Assert.Equal(caption, CaptionHitTest(window, dragArea));
        Assert.Equal(minimize, CaptionHitTest(window, minimizeButton));
        Assert.Equal(maximize, CaptionHitTest(window, maximizeButton));
        Assert.Equal(closeWindow, CaptionHitTest(window, closeButton));
    }

    private static void AssertOverflowLabel(Window window, bool visible)
    {
        var label = Assert.IsType<TextBlock>(window.FindName("OverflowLabel"));
        Assert.Equal(visible, label.IsVisible);
        if (visible) Assert.NotEmpty(label.Text);
    }

    private static int CaptionHitTest(Window window, FrameworkElement target)
    {
        const uint nonClientHitTest = 0x0084;
        var screenPoint = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
        var coordinates = ((int)Math.Round(screenPoint.Y) << 16) | ((int)Math.Round(screenPoint.X) & 0xffff);
        return (int)SendMessageW(new WindowInteropHelper(window).Handle, nonClientHitTest, 0, coordinates);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    private static void AssertToolbarFits(Window window, double tolerance)
    {
        var toolbar = Assert.IsType<Border>(window.FindName("ComparisonToolbar"));
        var navigation = ToolbarBounds("DifferenceNavigationGroup");
        var whitespace = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("IgnoreWhitespaceButton"));
        var left = ToolbarBounds(whitespace.IsVisible ? "IgnoreWhitespaceButton" : "DisplayContextButton");
        var right = ToolbarBounds("FindButton");
        Assert.InRange(navigation.Left + navigation.Width / 2 - toolbar.ActualWidth / 2, -tolerance, tolerance);
        Assert.True(left.Right <= navigation.Left + tolerance);
        Assert.True(navigation.Right <= right.Left + tolerance);
        Assert.True(ToolbarBounds("MoreActionsButton").Right <= toolbar.ActualWidth + tolerance);

        Rect ToolbarBounds(string name)
        {
            var item = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
            return item.TransformToAncestor(toolbar).TransformBounds(new Rect(item.RenderSize));
        }
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

    private sealed record TabLabel(string DisplayName)
    {
        public string Name => DisplayName;
        public string FileName => DisplayName + ".xlsx";
        public string FileKindGlyph { get; } = "X";
        public bool HasDifferences { get; } = true;
        public int DifferenceCount { get; } = 12;
        public bool HasTabBadge { get; } = true;
        public string TabBadgeText { get; } = "12";
        public bool HasChromeTabBadge => HasTabBadge;
        public string ChromeTabBadge => TabBadgeText;
    }
}
