using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Differ.App.Resources;
using Differ.App.ViewModels;

namespace Differ.App.Services;

internal sealed class ReadingLayoutBinding : IDisposable
{
    private readonly double _headerHeight;
    private readonly double _splitterHeight;
    private readonly RowDefinition _details;
    private readonly RowDefinition _splitterRow;
    private readonly GridSplitter _splitter;
    private readonly ToggleButton _toggle;
    private readonly FrameworkElement _content;
    private readonly UserPreferencesService _preferences;
    private readonly bool _spreadsheet;
    private readonly bool _autoCollapsedHeight;
    private bool _applying;

    internal ReadingLayoutBinding(RowDefinition details, RowDefinition splitterRow,
        GridSplitter splitter, ToggleButton toggle, FrameworkElement content,
        UserPreferencesService preferences, bool spreadsheet, bool autoCollapsedHeight = false)
    {
        _details = details;
        _splitterRow = splitterRow;
        _splitter = splitter;
        _toggle = toggle;
        _content = content;
        _preferences = preferences;
        _spreadsheet = spreadsheet;
        _autoCollapsedHeight = autoCollapsedHeight;
        _headerHeight = toggle.TryFindResource("Ui.DetailHeaderHeight") is double headerHeight ? headerHeight : 28;
        _splitterHeight = toggle.TryFindResource("Ui.DetailSplitterHeight") is double splitterHeight ? splitterHeight : 6;
        ApplyPreferences();
        _toggle.Checked += OnExpansionChanged;
        _toggle.Unchecked += OnExpansionChanged;
        _splitter.DragCompleted += OnDragCompleted;
        _preferences.PropertyChanged += OnPreferencesChanged;
    }

    public static ReadingLayoutBinding? Attach(UserControl view, int detailRowIndex, bool spreadsheet,
        GridSplitter splitter, ToggleButton toggle, FrameworkElement content, bool autoCollapsedHeight = false)
    {
        if (view.Content is not Grid grid || grid.RowDefinitions.Count <= detailRowIndex
            || Window.GetWindow(view)?.DataContext is not ComparisonWorkspaceViewModel workspace)
        {
            return null;
        }

        return new ReadingLayoutBinding(grid.RowDefinitions[detailRowIndex],
            grid.RowDefinitions[detailRowIndex - 1], splitter, toggle, content,
            workspace.Preferences, spreadsheet, autoCollapsedHeight);
    }

    public void Dispose()
    {
        _toggle.Checked -= OnExpansionChanged;
        _toggle.Unchecked -= OnExpansionChanged;
        _splitter.DragCompleted -= OnDragCompleted;
        _preferences.PropertyChanged -= OnPreferencesChanged;
    }

    private void OnExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        if (_spreadsheet)
        {
            _preferences.SpreadsheetDetailsExpanded = _toggle.IsChecked == true;
        }
        else
        {
            _preferences.TextDetailsExpanded = _toggle.IsChecked == true;
        }
    }

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        var expandedProperty = _spreadsheet ? nameof(UserPreferencesService.SpreadsheetDetailsExpanded)
            : nameof(UserPreferencesService.TextDetailsExpanded);
        var heightProperty = _spreadsheet ? nameof(UserPreferencesService.SpreadsheetDetailHeight)
            : nameof(UserPreferencesService.TextDetailHeight);
        if (e.PropertyName == expandedProperty || e.PropertyName == heightProperty) ApplyPreferences();
    }

    private void ApplyPreferences()
    {
        _applying = true;
        try
        {
            var expanded = _spreadsheet ? _preferences.SpreadsheetDetailsExpanded : _preferences.TextDetailsExpanded;
            var height = _spreadsheet ? _preferences.SpreadsheetDetailHeight : _preferences.TextDetailHeight;
            _toggle.IsChecked = expanded;
            _toggle.ToolTip = expanded ? Strings.CollapseContentDetails : Strings.ExpandFullContent;
            _content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            _splitter.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            _splitterRow.Height = new GridLength(expanded ? _splitterHeight : 0);
            _details.MinHeight = expanded ? 72 : _headerHeight;
            _details.Height = expanded ? new GridLength(height)
                : _autoCollapsedHeight ? GridLength.Auto
                : new GridLength(_headerHeight);
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_toggle.IsChecked != true || !_splitter.IsVisible) return;

        // Keep the saved expanded height intact when the compact header is shown.
        var height = _details.ActualHeight;
        if (_spreadsheet)
        {
            _preferences.SpreadsheetDetailHeight = height;
        }
        else
        {
            _preferences.TextDetailHeight = height;
        }
    }
}
