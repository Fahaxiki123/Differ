using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Services;
using Differ.App.ViewModels;

namespace Differ.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WPF owns the view lifetime; Unloaded deterministically releases viewport and layout event subscriptions, and Loaded creates fresh bindings.")]
public partial class TextDiffView : UserControl, IComparisonPaneFocusTarget
{
    private ComparisonViewportBridge? _overviewBridge;
    private ReadingLayoutBinding? _readingLayout;
    private DiffWindowViewModel? _viewModel;
    private ScrollViewer? _baseScrollViewer;
    private ScrollViewer? _mineScrollViewer;
    private DispatcherOperation? _scrollOperation;
    private ScrollViewer? _pendingScrollTarget;
    private double _pendingVerticalOffset;
    private double _pendingHorizontalOffset;
    private bool _pendingVerticalScroll;
    private bool _pendingHorizontalScroll;
    private bool _isSynchronizingScroll;

    public TextDiffView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void FocusBasePane()
    {
        Keyboard.Focus(BaseTextGrid);
    }

    public void FocusMinePane()
    {
        Keyboard.Focus(MineTextGrid);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _readingLayout?.Dispose();
        _readingLayout = ReadingLayoutBinding.Attach(this, 3, false, DetailSplitter, DetailsToggle, DetailsContent);
        AttachViewModel(DataContext as DiffWindowViewModel);
        Dispatcher.BeginInvoke(AttachScrollViewers, DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _readingLayout?.Dispose();
        _readingLayout = null;
        AttachViewModel(null);
        DetachScrollViewers();
        if (_scrollOperation?.Status == DispatcherOperationStatus.Pending)
        {
            _scrollOperation.Abort();
        }

        _scrollOperation = null;
        _pendingScrollTarget = null;
        _pendingVerticalScroll = false;
        _pendingHorizontalScroll = false;
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel(e.NewValue as DiffWindowViewModel);
    }

    private void AttachViewModel(DiffWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!string.Equals(
            e.PropertyName,
            nameof(DiffWindowViewModel.SelectedTextRow),
            StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.BeginInvoke(
            ScrollSelectedRowIntoView,
            DispatcherPriority.DataBind);
    }

    private void TextGrid_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(AttachScrollViewers, DispatcherPriority.Loaded);
    }

    private void AttachScrollViewers()
    {
        DetachScrollViewers();
        _baseScrollViewer = FindVisualChild<ScrollViewer>(BaseTextGrid);
        _mineScrollViewer = FindVisualChild<ScrollViewer>(MineTextGrid);
        if (_baseScrollViewer is not null)
        {
            _baseScrollViewer.ScrollChanged += OnBaseScrollChanged;
            _overviewBridge = new ComparisonViewportBridge(this, _baseScrollViewer, BaseTextGrid);
        }

        if (_mineScrollViewer is not null)
        {
            _mineScrollViewer.ScrollChanged += OnMineScrollChanged;
        }
    }

    private void DetachScrollViewers()
    {
        _overviewBridge?.Dispose();
        _overviewBridge = null;
        if (_baseScrollViewer is not null)
        {
            _baseScrollViewer.ScrollChanged -= OnBaseScrollChanged;
        }

        if (_mineScrollViewer is not null)
        {
            _mineScrollViewer.ScrollChanged -= OnMineScrollChanged;
        }

        _baseScrollViewer = null;
        _mineScrollViewer = null;
    }

    private void OnBaseScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        SynchronizeScroll(_mineScrollViewer, e);
    }

    private void OnMineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        SynchronizeScroll(_baseScrollViewer, e);
    }

    private void SynchronizeScroll(
        ScrollViewer? target,
        ScrollChangedEventArgs e)
    {
        if (_isSynchronizingScroll || target is null)
        {
            return;
        }

        if (Math.Abs(e.VerticalChange) <= double.Epsilon
            && Math.Abs(e.HorizontalChange) <= double.Epsilon)
        {
            return;
        }

        _pendingScrollTarget = target;
        if (Math.Abs(e.VerticalChange) > double.Epsilon)
        {
            _pendingVerticalScroll = true;
            _pendingVerticalOffset = e.VerticalOffset;
        }

        if (Math.Abs(e.HorizontalChange) > double.Epsilon)
        {
            _pendingHorizontalScroll = true;
            _pendingHorizontalOffset = e.HorizontalOffset;
        }

        if (_scrollOperation?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        // ScrollChanged may arrive once per mouse-wheel tick or once per
        // scrollbar pixel. Apply the latest pair only once at Render priority
        // so both grids are laid out together instead of thrashing each other.
        _scrollOperation = Dispatcher.BeginInvoke(
            ApplyPendingScroll,
            DispatcherPriority.Render);
    }

    private void ApplyPendingScroll()
    {
        _scrollOperation = null;
        var target = _pendingScrollTarget;
        var applyVertical = _pendingVerticalScroll;
        var applyHorizontal = _pendingHorizontalScroll;
        var verticalOffset = _pendingVerticalOffset;
        var horizontalOffset = _pendingHorizontalOffset;
        _pendingScrollTarget = null;
        _pendingVerticalScroll = false;
        _pendingHorizontalScroll = false;

        if (target is null)
        {
            return;
        }

        try
        {
            _isSynchronizingScroll = true;
            if (applyVertical
                && Math.Abs(target.VerticalOffset - verticalOffset) > 0.1)
            {
                target.ScrollToVerticalOffset(verticalOffset);
            }

            if (applyHorizontal
                && Math.Abs(target.HorizontalOffset - horizontalOffset) > 0.1)
            {
                target.ScrollToHorizontalOffset(horizontalOffset);
            }
        }
        finally
        {
            _isSynchronizingScroll = false;
        }
    }

    private void TextGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ScrollSelectedRowIntoView();
    }

    private void ScrollSelectedRowIntoView()
    {
        if (_viewModel?.SelectedTextRow is not { } selectedRow)
        {
            return;
        }

        BaseTextGrid.ScrollIntoView(selectedRow);
        MineTextGrid.ScrollIntoView(selectedRow);
    }

    private void TextGrid_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        // WPF does not move selection on right-click, so the menu would
        // otherwise act on whatever was selected before. Promote the row under
        // the cursor first; both grids share SelectedTextRow, so setting it on
        // either one keeps the panes and the other grid in step.
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<DataGridRow>(source) is { } row
            && row.Item is TextDiffRowViewModel textRow
            && _viewModel is not null)
        {
            _viewModel.SelectedTextRow = textRow;
        }

        if (_viewModel?.SelectedTextRow is null)
        {
            e.Handled = true;
        }
    }

    private void TextGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ClipboardCopyMode is None because every column is a template column
        // with no ClipboardContentBinding, which would copy empty strings.
        // Handle Ctrl+C here so the focused side is copied instead.
        if (e.Key is not (Key.C or Key.Insert)
            || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (_viewModel?.SelectedTextRow is not { } selectedRow)
        {
            return;
        }

        DiffClipboard.CopyText(ReferenceEquals(sender, MineTextGrid)
            ? selectedRow.NewText
            : selectedRow.OldText);
        e.Handled = true;
    }

    private void CopyBaseSide_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedTextRow is { } selectedRow)
        {
            DiffClipboard.CopyText(selectedRow.OldText);
        }
    }

    private void CopyMineSide_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedTextRow is { } selectedRow)
        {
            DiffClipboard.CopyText(selectedRow.NewText);
        }
    }

    private void CopyBothSides_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedTextRow is { } selectedRow)
        {
            DiffClipboard.CopyBothSides(
                selectedRow.OldText,
                selectedRow.NewText);
        }
    }

    private static T? FindAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            // Hit testing over an InlineDiffTextBlock can surface a Run, which
            // is a TextElement rather than a Visual, so fall back to the
            // logical tree to bridge back into the visual hierarchy.
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
