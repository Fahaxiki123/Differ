using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Differ.App.Services;

namespace Differ.App.Tests;

public sealed class ReadingLayoutBindingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void InitialLayoutUsesFormatDefaultAndReopeningRestoresTotalHeight(bool spreadsheet, bool expanded)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var preferences = new UserPreferencesService();
            using var layout = new LayoutFixture(preferences, spreadsheet);
            var expectedExpandedHeight = spreadsheet ? 104 : 124;
            Assert.Equal(expanded, layout.Toggle.IsChecked);
            Assert.Equal(expanded ? expectedExpandedHeight : 28, layout.Details.Height.Value);
            Assert.Equal(expanded ? 6 : 0, layout.SplitterRow.Height.Value);
            Assert.Equal(expanded ? Visibility.Visible : Visibility.Collapsed, layout.Content.Visibility);

            layout.Toggle.IsChecked = true;
            Assert.Equal(expectedExpandedHeight, layout.Details.Height.Value);
            if (spreadsheet) preferences.SpreadsheetDetailHeight = 220;
            else preferences.TextDetailHeight = 220;
            Assert.Equal(220, layout.Details.Height.Value);

            layout.Toggle.IsChecked = false;
            Assert.Equal(28, layout.Details.Height.Value);
            Assert.Equal(0, layout.SplitterRow.Height.Value);
            Assert.Equal(Visibility.Collapsed, layout.Content.Visibility);
            Assert.Equal(220, spreadsheet ? preferences.SpreadsheetDetailHeight : preferences.TextDetailHeight);

            // Selection changes do not own this state. Reopening uses the last
            // reading height rather than the compact header height.
            layout.Content.DataContext = new object();
            Assert.False(layout.Toggle.IsChecked);
            layout.Toggle.IsChecked = true;
            Assert.Equal(220, layout.Details.Height.Value);
            Assert.Equal(Visibility.Visible, layout.Content.Visibility);
        });
    }

    [Fact]
    public void SameFormatSharesPreferencesAndDisposalReleasesAllSubscriptions()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var preferences = new UserPreferencesService();
            using var first = new LayoutFixture(preferences, spreadsheet: false);
            using var second = new LayoutFixture(preferences, spreadsheet: false);
            using var spreadsheet = new LayoutFixture(preferences, spreadsheet: true);
            first.Toggle.IsChecked = true;
            preferences.TextDetailHeight = 280;
            Assert.True(second.Toggle.IsChecked);
            Assert.Equal(280, second.Details.Height.Value);
            Assert.Equal(104, spreadsheet.Details.Height.Value);

            first.Binding.Dispose();
            preferences.TextDetailsExpanded = false;
            Assert.False(second.Toggle.IsChecked);
            Assert.True(first.Toggle.IsChecked);
            first.Toggle.IsChecked = false;
            first.Toggle.IsChecked = true;
            Assert.False(preferences.TextDetailsExpanded);
        });
    }

    [Fact]
    public void DragStoresTotalHeightAndCollapsedDragCannotReplaceIt()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var preferences = new UserPreferencesService();
            using var layout = new LayoutFixture(preferences, spreadsheet: true);
            var window = new Window
            {
                Content = layout.Grid,
                Width = 800,
                Height = 600,
                Left = -20_000,
                Top = -20_000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            try
            {
                window.Show();
                layout.Details.Height = new GridLength(248);
                window.UpdateLayout();
                layout.Splitter.RaiseEvent(new DragCompletedEventArgs(0, 20, false)
                {
                    RoutedEvent = Thumb.DragCompletedEvent
                });
                Assert.Equal(248, preferences.SpreadsheetDetailHeight, 4);
                layout.Toggle.IsChecked = false;
                window.UpdateLayout();
                layout.Splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false)
                {
                    RoutedEvent = Thumb.DragCompletedEvent
                });
                Assert.Equal(248, preferences.SpreadsheetDetailHeight, 4);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false, 72)]
    [InlineData(false, 124)]
    [InlineData(true, 72)]
    [InlineData(true, 124)]
    [InlineData(true, 180)]
    [InlineData(true, 20)]
    public void SavedHeightKeepsItsHistoricalTotalMeaning(bool spreadsheet, double savedHeight)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var preferences = new UserPreferencesService
            {
                TextDetailHeight = savedHeight,
                SpreadsheetDetailHeight = savedHeight,
                TextDetailsExpanded = true,
                SpreadsheetDetailsExpanded = true
            };
            using var layout = new LayoutFixture(preferences, spreadsheet);
            var expected = Math.Max(72, savedHeight);
            Assert.Equal(expected, layout.Details.Height.Value);
            Assert.Equal(72, layout.Details.MinHeight);
            layout.Toggle.IsChecked = false;
            Assert.Equal(28, layout.Details.Height.Value);
            layout.Toggle.IsChecked = true;
            Assert.Equal(expected, layout.Details.Height.Value);
        });
    }

    private sealed class LayoutFixture : IDisposable
    {
        public LayoutFixture(UserPreferencesService preferences, bool spreadsheet)
        {
            Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.RowDefinitions.Add(SplitterRow);
            Grid.RowDefinitions.Add(Details);
            Grid.SetRow(Splitter, 1);
            Grid.SetRow(Toggle, 2);
            Grid.SetRow(Content, 2);
            Grid.Children.Add(Splitter);
            Grid.Children.Add(Toggle);
            Grid.Children.Add(Content);
            Binding = new ReadingLayoutBinding(Details, SplitterRow, Splitter, Toggle, Content, preferences, spreadsheet);
        }

        public Grid Grid { get; } = new();
        public RowDefinition Details { get; } = new();
        public RowDefinition SplitterRow { get; } = new();
        public GridSplitter Splitter { get; } = new();
        public ToggleButton Toggle { get; } = new();
        public Border Content { get; } = new();
        public ReadingLayoutBinding Binding { get; }
        public void Dispose() => Binding.Dispose();
    }
}
