using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Microsoft.Win32;

namespace Differ.App;

public partial class DiffWindow : Window
{
    private static readonly CompositeFormat ExportFailedMessageFormat = CompositeFormat.Parse(Strings.ExportFailedFormat);
    private readonly ComparisonWorkspaceViewModel _workspace;
    private readonly IThemeService _themeService;
    private readonly bool _persistWindowLayout;
    private bool _closeAfterSave;
    private bool _savingWindowLayout;

    public DiffWindow(
        ComparisonWorkspaceViewModel workspace,
        IThemeService themeService,
        bool persistWindowLayout = false)
    {
        _workspace = workspace;
        _themeService = themeService;
        _persistWindowLayout = persistWindowLayout;
        InitializeComponent();
        DataContext = workspace;
        _workspace.AllTabsClosed += OnAllTabsClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        Closing += OnClosing;
        Closed += OnClosed;
        if (_persistWindowLayout)
        {
            RestoreWindowLayout();
        }
    }

    private void DiffGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is DataGrid
            {
                DataContext: DiffWindowViewModel viewModel
            } grid
            && viewModel.SelectedDiffRow is not null)
        {
            grid.ScrollIntoView(viewModel.SelectedDiffRow);
        }
    }

    private void ComparisonOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OnAllTabsClosed(object? sender, EventArgs e)
    {
        Close();
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _workspace.SelectedTab is { } active
            && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            if (active.IsFindOpen && IsInsidePanel(e.OriginalSource as DependencyObject, "FindPanel"))
            {
                ExecuteSelectedCommand(Keyboard.Modifiers == ModifierKeys.Shift
                    ? active.FindPreviousCommand : active.FindNextCommand);
                e.Handled = true;
                return;
            }

            if (active.IsGoToOpen && IsInsidePanel(e.OriginalSource as DependencyObject, "GoToPanel"))
            {
                ExecuteSelectedCommand(active.GoToCommand);
                e.Handled = true;
                return;
            }
        }

        var shortcut = ComparisonShortcutResolver.Resolve(
            e.Key,
            e.SystemKey,
            Keyboard.Modifiers);
        switch (shortcut)
        {
            case ComparisonShortcut.Undo:
            case ComparisonShortcut.Redo:
                if (_workspace.SelectedTab is { IsEditableText: true } editable
                    && !IsTextInput(e.OriginalSource as DependencyObject)
                    && FindVisiblePaneTarget(ComparisonContentHost) is CodeEditorView activeEditor
                    && !activeEditor.HasBrowserKeyboardFocus)
                {
                    e.Handled = true;
                    ExecuteSelectedCommand(shortcut == ComparisonShortcut.Undo
                        ? editable.UndoEditorCommand : editable.RedoEditorCommand);
                }
                return;
            case ComparisonShortcut.Save:
            case ComparisonShortcut.SaveAs:
                if (_workspace.SelectedTab?.IsEditableText == true
                    && FindVisiblePaneTarget(ComparisonContentHost) is CodeEditorView editor
                    && !editor.HasBrowserKeyboardFocus)
                {
                    e.Handled = true;
                    await editor.SaveFromShellAsync(shortcut == ComparisonShortcut.SaveAs);
                }
                // Native WebView focus retains its own Monaco commands; handling here as well would save twice.
                return;
            case ComparisonShortcut.PreviousDifference:
                e.Handled = true;
                ExecuteSelectedCommand(
                    _workspace.SelectedTab?.PreviousDifferenceCommand);
                return;
            case ComparisonShortcut.NextDifference:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.SelectedTab?.NextDifferenceCommand);
                return;
            case ComparisonShortcut.ToggleTheme:
                e.Handled = true;
                _themeService.Toggle();
                return;
            case ComparisonShortcut.ToggleUnchanged:
                if (_workspace.SelectedTab is { } selectedTab)
                {
                    selectedTab.ShowUnchanged = !selectedTab.ShowUnchanged;
                }

                e.Handled = true;
                return;
            case ComparisonShortcut.FocusBase:
                e.Handled = FocusComparisonPane(useMineSide: false);
                return;
            case ComparisonShortcut.FocusMine:
                e.Handled = FocusComparisonPane(useMineSide: true);
                return;
            case ComparisonShortcut.CloseTab:
                e.Handled = true;
                _workspace.CloseSelectedTab();
                return;
            case ComparisonShortcut.CloseWindow:
                e.Handled = true;
                if (!CloseLocalPanel())
                {
                    Close();
                }

                return;
            case ComparisonShortcut.Find:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.SelectedTab?.OpenFindCommand);
                FocusLocalInput("FindInput");
                return;
            case ComparisonShortcut.GoTo:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.SelectedTab?.OpenGoToCommand);
                FocusLocalInput("GoToInput");
                return;
            case ComparisonShortcut.Refresh:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.RefreshSelectedCommand);
                return;
            case ComparisonShortcut.NextTab:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.NextTabCommand);
                return;
            case ComparisonShortcut.PreviousTab:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.PreviousTabCommand);
                return;
        }
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _themeService.Toggle();
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            ChangeZoom(e.Delta > 0 ? 0.1 : -0.1);
            e.Handled = true;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(0.1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-0.1);

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => _workspace.Preferences.ReadingScale = 1;

    private void ChangeZoom(double change) => _workspace.Preferences.ReadingScale =
        Math.Round(_workspace.Preferences.ReadingScale + change, 2);

    private void FilterTabs_Click(object sender, RoutedEventArgs e)
    {
        TabFilterPopup.IsOpen = !TabFilterPopup.IsOpen;
        if (TabFilterPopup.IsOpen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                TabFilterInput.Focus();
                TabFilterInput.SelectAll();
            }));
        }
    }

    private void FileTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && sender is ListBox { SelectedItem: { } selected } tabs)
        {
            tabs.ScrollIntoView(selected);
        }
    }

    private void FilterInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter)
        {
            TabFilterPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void LocalInput_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } input)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (input.IsVisible)
                {
                    input.Focus();
                    input.SelectAll();
                }
            }));
        }
    }

    private void FocusLocalInput(string name)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var input = FindVisibleInput(ComparisonContentHost, name);
            input?.Focus();
            input?.SelectAll();
        }));
    }

    private static TextBox? FindVisibleInput(DependencyObject parent, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox { IsVisible: true } input && input.Name == name)
            {
                return input;
            }

            var nested = FindVisibleInput(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private bool CloseLocalPanel()
    {
        if (TabFilterPopup.IsOpen)
        {
            TabFilterPopup.IsOpen = false;
            return true;
        }

        var active = _workspace.SelectedTab;
        if (active?.IsFindOpen == true)
        {
            ExecuteSelectedCommand(active.CloseFindCommand);
            return true;
        }

        if (active?.IsGoToOpen == true)
        {
            ExecuteSelectedCommand(active.CloseGoToCommand);
            return true;
        }

        return false;
    }

    private static bool IsInsidePanel(DependencyObject? element, string name)
    {
        while (element is not null)
        {
            if (element is FrameworkElement frameworkElement && frameworkElement.Name == name)
            {
                return true;
            }

            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private static bool IsTextInput(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)
            {
                return true;
            }

            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private async void ExportHtml_Click(object sender, RoutedEventArgs e) => await ExportAsync(html: true);

    private async void ExportSelectedText_Click(object sender, RoutedEventArgs e) => await ExportAsync(html: false);

    private async Task ExportAsync(bool html)
    {
        var tab = _workspace.SelectedTab;
        if (tab?.CanExport != true)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = html ? Strings.ExportHtml : Strings.ExportSelectedText,
            Filter = html ? Strings.ExportHtmlFilter : Strings.ExportTextFilter,
            DefaultExt = html ? ".html" : ".txt",
            FileName = Strings.ExportReportTitle + (html ? ".html" : ".txt"),
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (html)
            {
                await tab.ExportHtmlAsync(dialog.FileName, CancellationToken.None);
            }
            else
            {
                await tab.ExportSelectedTextAsync(dialog.FileName, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(this, string.Format(CultureInfo.CurrentCulture, ExportFailedMessageFormat, exception.Message),
                Strings.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestoreWindowLayout()
    {
        var preferences = _workspace.Preferences;
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var desired = new Rect(double.IsFinite(preferences.WindowLeft) ? preferences.WindowLeft : SystemParameters.WorkArea.Left + 30,
            double.IsFinite(preferences.WindowTop) ? preferences.WindowTop : SystemParameters.WorkArea.Top + 30,
            preferences.WindowWidth, preferences.WindowHeight);
        var bounds = ClampWindowBounds(desired, desktop, new Size(MinWidth, MinHeight));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (preferences.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    internal static Rect ClampWindowBounds(Rect requested, Rect desktop, Size minimum)
    {
        var width = Math.Min(desktop.Width, Math.Max(minimum.Width, requested.Width));
        var height = Math.Min(desktop.Height, Math.Max(minimum.Height, requested.Height));
        var left = double.IsFinite(requested.Left) ? requested.Left : desktop.Left;
        var top = double.IsFinite(requested.Top) ? requested.Top : desktop.Top;
        return new Rect(Math.Clamp(left, desktop.Left, desktop.Right - width),
            Math.Clamp(top, desktop.Top, desktop.Bottom - height), width, height);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_persistWindowLayout && _workspace.Tabs.All(tab => !tab.IsEditableText)) return;
        if (_closeAfterSave)
        {
            return;
        }

        e.Cancel = true;
        if (_savingWindowLayout)
        {
            return;
        }

        _savingWindowLayout = true;
        if (!await _workspace.ConfirmCloseAllAsync())
        {
            _savingWindowLayout = false;
            return;
        }
        if (!_persistWindowLayout)
        {
            _closeAfterSave = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
            return;
        }
        var preferences = _workspace.Preferences;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            preferences.WindowLeft = bounds.Left;
            preferences.WindowTop = bounds.Top;
            preferences.WindowWidth = bounds.Width;
            preferences.WindowHeight = bounds.Height;
        }

        preferences.IsMaximized = WindowState == WindowState.Maximized;
        try
        {
            await preferences.SaveAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning(Strings.PreferencesSaveFailed);
        }
        finally
        {
            _closeAfterSave = true;
            // Even a cached/synchronous save must leave the first Closing
            // callback before asking WPF to close the window again.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
    }

    private bool FocusComparisonPane(bool useMineSide)
    {
        var target = FindVisiblePaneTarget(ComparisonContentHost);
        if (target is null)
        {
            return false;
        }

        if (useMineSide)
        {
            target.FocusMinePane();
        }
        else
        {
            target.FocusBasePane();
        }

        return true;
    }

    private static void ExecuteSelectedCommand(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private static IComparisonPaneFocusTarget? FindVisiblePaneTarget(
        DependencyObject parent)
    {
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement { IsVisible: true }
                && child is IComparisonPaneFocusTarget target)
            {
                return target;
            }

            var nested = FindVisiblePaneTarget(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _workspace.AllTabsClosed -= OnAllTabsClosed;
        _workspace.Dispose();
    }
}
