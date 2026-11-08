using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
            window.Left = -20_000;
            window.Top = -20_000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            window.FontSize = 13;
            window.UseLayoutRounding = true;

            var tabs = Assert.IsType<ListBox>(spreadsheet is not null
                ? spreadsheet.FindName("WorksheetTabs")
                : window.FindName("FileTabs"));
            var root = Assert.IsType<Grid>(spreadsheet is not null ? spreadsheet.Content : window.Content);
            var tabRow = root.RowDefinitions[worksheet ? 2 : 1];
            var compactHeight = worksheet ? 28d : 30d;
            // Use the production templates without starting file parsing or
            // replacing their selection bindings with incompatible view models.
            BindingOperations.ClearBinding(tabs, ItemsControl.ItemsSourceProperty);
            BindingOperations.ClearBinding(tabs, System.Windows.Controls.Primitives.Selector.SelectedItemProperty);
            var items = Enumerable.Range(1, 10)
                .Select(index => new TabLabel($"提交前审阅_中文长名称_工作表及文件标签_{index:D2}"))
                .ToArray();
            tabs.ItemsSource = items;
            tabs.SelectedIndex = 0;

            try
            {
                window.Show();
                FlushLayout(window);
                var scroll = Assert.Single(FindVisuals<ScrollViewer>(tabs));
                Assert.Equal(Visibility.Visible, scroll.ComputedHorizontalScrollBarVisibility);
                Assert.True(tabRow.ActualHeight > compactHeight);
                AssertTabFitsViewport(tabs, items[0], scroll);

                tabs.SelectedIndex = items.Length - 1;
                tabs.ScrollIntoView(items[^1]);
                FlushLayout(window);
                Assert.True(scroll.HorizontalOffset > 0);
                Assert.Same(items[^1], tabs.SelectedItem);
                AssertTabFitsViewport(tabs, items[^1], scroll);

                var overflowHeight = tabRow.ActualHeight;
                themes.Apply(AppTheme.Dark);
                FlushLayout(window);
                Assert.Equal(overflowHeight, tabRow.ActualHeight);
                AssertTabFitsViewport(tabs, items[^1], scroll);

                tabs.ItemsSource = items.Take(2).ToArray();
                tabs.SelectedIndex = 0;
                FlushLayout(window);
                Assert.Equal(Visibility.Collapsed, scroll.ComputedHorizontalScrollBarVisibility);
                var tolerance = 1 / VisualTreeHelper.GetDpi(window).DpiScaleY;
                Assert.InRange(tabRow.ActualHeight, compactHeight - tolerance, compactHeight + tolerance);
                AssertTabFitsViewport(tabs, items[0], scroll);
            }
            finally
            {
                window.Close();
                FlushLayout(window);
            }
        });
    }

    private static void AssertTabFitsViewport(ListBox tabs, object item, ScrollViewer scroll)
    {
        var container = Assert.IsType<ListBoxItem>(tabs.ItemContainerGenerator.ContainerFromItem(item));
        var viewport = Assert.Single(FindVisuals<ScrollContentPresenter>(scroll));
        var bounds = container.TransformToAncestor(viewport).TransformBounds(new Rect(container.RenderSize));
        var tolerance = 1 / VisualTreeHelper.GetDpi(tabs).DpiScaleY;
        Assert.True(bounds.Top >= -tolerance && bounds.Bottom <= viewport.ActualHeight + tolerance,
            $"Tab is vertically clipped: bounds={bounds}, viewport={viewport.RenderSize}.");
        Assert.True(bounds.Left >= -tolerance && bounds.Right <= viewport.ActualWidth + tolerance,
            $"Selected tab cannot be fully scrolled into view: bounds={bounds}, viewport={viewport.RenderSize}.");
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
        public string FileName => DisplayName + ".xlsx";
        public string FileKindGlyph { get; } = "X";
        public bool HasTabBadge { get; } = true;
        public string TabBadgeText { get; } = "12";
    }
}
