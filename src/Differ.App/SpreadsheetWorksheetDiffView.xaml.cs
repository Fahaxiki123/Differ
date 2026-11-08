using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.App.ViewModels;

namespace Differ.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WPF owns the view lifetime; Unloaded deterministically releases viewport and layout event subscriptions, and Loaded creates fresh bindings.")]
public partial class SpreadsheetWorksheetDiffView : UserControl, IComparisonPaneFocusTarget
{
    private ComparisonViewportBridge? _overviewBridge;
    private ReadingLayoutBinding? _readingLayout;
    private WorksheetTabViewModel? _viewModel;
    private ScrollViewer? _baseScrollViewer;
    private ScrollViewer? _mineScrollViewer;
    private DispatcherOperation? _selectionOperation;
    private DispatcherOperation? _scrollOperation;
    private ScrollViewer? _pendingScrollTarget;
    private double _pendingVerticalOffset;
    private double _pendingHorizontalOffset;
    private bool _pendingVerticalScroll;
    private bool _pendingHorizontalScroll;
    private bool _isSynchronizingScroll;
    private bool _isSynchronizingSelection;
    private DataGrid? _baseHeaderGrid;
    private DataGrid? _mineHeaderGrid;
    private ScrollViewer? _baseHeaderScroll;
    private ScrollViewer? _mineHeaderScroll;

    public SpreadsheetWorksheetDiffView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void FocusBasePane()
    {
        Keyboard.Focus(BaseSpreadsheetGrid);
    }

    public void FocusMinePane()
    {
        Keyboard.Focus(MineSpreadsheetGrid);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _readingLayout?.Dispose();
        _readingLayout = ReadingLayoutBinding.Attach(this, 2, true, DetailSplitter, DetailsToggle, DetailsContent);
        AttachViewModel(DataContext as WorksheetTabViewModel);
        ConfigureColumns();
        Dispatcher.BeginInvoke(
            AttachScrollViewers,
            DispatcherPriority.Loaded);
        QueueSelectionSynchronization();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _readingLayout?.Dispose();
        _readingLayout = null;
        AttachViewModel(null);
        DetachScrollViewers();
        if (_selectionOperation?.Status == DispatcherOperationStatus.Pending)
        {
            _selectionOperation.Abort();
        }

        _selectionOperation = null;
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
        AttachViewModel(e.NewValue as WorksheetTabViewModel);
        if (IsLoaded)
        {
            ConfigureColumns();
            QueueSelectionSynchronization();
        }
    }

    private void AttachViewModel(WorksheetTabViewModel? viewModel)
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
        if (string.Equals(
                e.PropertyName,
                nameof(WorksheetTabViewModel.SelectedRow),
                StringComparison.Ordinal)
            || string.Equals(
                e.PropertyName,
                nameof(WorksheetTabViewModel.SelectedCell),
                StringComparison.Ordinal)
            || string.Equals(
                e.PropertyName,
                nameof(WorksheetTabViewModel.Rows),
                StringComparison.Ordinal))
        {
            QueueSelectionSynchronization();
        }
    }

    private void ConfigureColumns()
    {
        if (_viewModel is null)
        {
            return;
        }

        ConfigureColumns(
            BaseSpreadsheetGrid,
            nameof(SpreadsheetRowViewModel.BaseCells));
        ConfigureColumns(
            MineSpreadsheetGrid,
            nameof(SpreadsheetRowViewModel.MineCells));
        ConfigureFrozenHeaders();
    }

    private void ConfigureFrozenHeaders()
    {
        var hasHeaders = _viewModel!.FrozenHeaderRows.Count > 0;
        BaseSpreadsheetGrid.HeadersVisibility = MineSpreadsheetGrid.HeadersVisibility =
            hasHeaders ? DataGridHeadersVisibility.None : DataGridHeadersVisibility.Column;
        _baseHeaderGrid = hasHeaders ? CreateHeaderGrid(true) : null;
        _mineHeaderGrid = hasHeaders ? CreateHeaderGrid(false) : null;
        BaseHeaderHost.Content = _baseHeaderGrid;
        MineHeaderHost.Content = _mineHeaderGrid;
    }

    private DataGrid CreateHeaderGrid(bool baseSide)
    {
        var grid = new DataGrid
        {
            Name = baseSide ? "BaseFrozenHeaderGrid" : "MineFrozenHeaderGrid",
            AutoGenerateColumns = false,
            CanUserResizeColumns = false,
            CanUserResizeRows = false,
            CanUserSortColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = true,
            ClipboardCopyMode = DataGridClipboardCopyMode.None,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.Cell,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            FrozenColumnCount = _viewModel!.FrozenColumnCount,
            ItemsSource = _viewModel.FrozenHeaderRows,
            EnableColumnVirtualization = true,
            EnableRowVirtualization = true,
            CellStyle = (Style)Resources["SpreadsheetCellStyle"],
            RowStyle = (Style)Resources["SpreadsheetRowStyle"],
            ColumnHeaderStyle = (Style)Resources["SpreadsheetColumnHeaderStyle"],
            ContextMenu = (ContextMenu)Resources["SpreadsheetCellContextMenu"]
        };
        ScrollViewer.SetCanContentScroll(grid, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Width = 50,
            Header = string.Empty,
            CellTemplate = (DataTemplate)Resources[baseSide ? "BaseRowNumberTemplate" : "MineRowNumberTemplate"]
        });
        ConfigureColumns(grid, baseSide ? nameof(SpreadsheetRowViewModel.BaseCells) : nameof(SpreadsheetRowViewModel.MineCells));
        grid.SelectedCellsChanged += SpreadsheetGrid_SelectedCellsChanged;
        grid.ContextMenuOpening += SpreadsheetGrid_ContextMenuOpening;
        grid.PreviewKeyDown += SpreadsheetGrid_PreviewKeyDown;
        grid.Loaded += SpreadsheetGrid_Loaded;
        return grid;
    }

    private void ConfigureColumns(DataGrid grid, string cellsPropertyName)
    {
        while (grid.Columns.Count > 1)
        {
            grid.Columns.RemoveAt(grid.Columns.Count - 1);
        }

        var headerTemplate = (DataTemplate)Resources[
            "SpreadsheetColumnHeaderTemplate"];
        for (var index = 0; index < _viewModel!.Columns.Count; index++)
        {
            var column = _viewModel.Columns[index];
            grid.Columns.Add(new SpreadsheetValueColumn(cellsPropertyName, index)
            {
                Width = column.Width,
                Header = column with
                {
                    DisplayHeader = cellsPropertyName == nameof(SpreadsheetRowViewModel.BaseCells)
                        ? column.BaseColumn ?? "—" : column.MineColumn ?? "—"
                },
                HeaderTemplate = headerTemplate
            });
        }
    }

    private void SpreadsheetGrid_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            AttachScrollViewers,
            DispatcherPriority.Loaded);
    }

    private void AttachScrollViewers()
    {
        DetachScrollViewers();
        _baseScrollViewer = FindVisualChild<ScrollViewer>(
            BaseSpreadsheetGrid);
        _mineScrollViewer = FindVisualChild<ScrollViewer>(
            MineSpreadsheetGrid);
        _baseHeaderScroll = _baseHeaderGrid is null ? null : FindVisualChild<ScrollViewer>(_baseHeaderGrid);
        _mineHeaderScroll = _mineHeaderGrid is null ? null : FindVisualChild<ScrollViewer>(_mineHeaderGrid);
        if (_baseHeaderScroll is not null) _baseHeaderScroll.ScrollChanged += OnHeaderScrollChanged;
        if (_mineHeaderScroll is not null) _mineHeaderScroll.ScrollChanged += OnHeaderScrollChanged;
        if (_baseScrollViewer is not null)
        {
            _baseScrollViewer.ScrollChanged += OnBaseScrollChanged;
            _overviewBridge = new ComparisonViewportBridge(this, _baseScrollViewer, BaseSpreadsheetGrid,
                _viewModel?.FrozenHeaderRows.Count ?? 0);
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
        if (_baseHeaderScroll is not null) _baseHeaderScroll.ScrollChanged -= OnHeaderScrollChanged;
        if (_mineHeaderScroll is not null) _mineHeaderScroll.ScrollChanged -= OnHeaderScrollChanged;
        _baseHeaderScroll = null;
        _mineHeaderScroll = null;
    }

    private void OnBaseScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateHeaderScrollbarGutter();
        SynchronizeScroll(_mineScrollViewer, e);
    }

    private void OnMineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateHeaderScrollbarGutter();
        SynchronizeScroll(_baseScrollViewer, e);
    }

    private void UpdateHeaderScrollbarGutter()
    {
        BaseHeaderHost.Margin = new Thickness(0, 0,
            _baseScrollViewer?.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0, 0);
        MineHeaderHost.Margin = new Thickness(0, 0,
            _mineScrollViewer?.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0, 0);
    }

    private void OnHeaderScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > double.Epsilon)
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
            if (applyHorizontal)
            {
                _baseScrollViewer?.ScrollToHorizontalOffset(horizontalOffset);
                _mineScrollViewer?.ScrollToHorizontalOffset(horizontalOffset);
                _baseHeaderScroll?.ScrollToHorizontalOffset(horizontalOffset);
                _mineHeaderScroll?.ScrollToHorizontalOffset(horizontalOffset);
            }
        }
        finally
        {
            _isSynchronizingScroll = false;
        }
    }

    private void SpreadsheetGrid_SelectedCellsChanged(
        object sender,
        SelectedCellsChangedEventArgs e)
    {
        if (_isSynchronizingSelection
            || _viewModel is null
            || sender is not DataGrid grid
            || e.AddedCells.Count == 0)
        {
            return;
        }

        var selectedCell = e.AddedCells[0];
        if (selectedCell.Item is not SpreadsheetRowViewModel row)
        {
            return;
        }

        var columnIndex = selectedCell.Column.DisplayIndex - 1;
        if (columnIndex < 0)
        {
            return;
        }

        _viewModel.SelectCell(row, columnIndex);
        QueueSelectionSynchronization();
    }

    private void QueueSelectionSynchronization()
    {
        if (!IsLoaded
            || _selectionOperation?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        _selectionOperation = Dispatcher.BeginInvoke(
            SynchronizeSelection,
            DispatcherPriority.DataBind);
    }

    private void SynchronizeSelection()
    {
        _selectionOperation = null;
        if (_viewModel?.SelectedRow is not { } row
            || _viewModel.SelectedCell is not { } cell)
        {
            _isSynchronizingSelection = true;
            try
            {
                BaseSpreadsheetGrid.SelectedCells.Clear();
                MineSpreadsheetGrid.SelectedCells.Clear();
                _baseHeaderGrid?.SelectedCells.Clear();
                _mineHeaderGrid?.SelectedCells.Clear();
            }
            finally
            {
                _isSynchronizingSelection = false;
            }

            return;
        }

        try
        {
            _isSynchronizingSelection = true;
            var header = _viewModel.IsFrozenHeader(row);
            if (header)
            {
                if (BaseSpreadsheetGrid.SelectedCells.Count > 0) BaseSpreadsheetGrid.SelectedCells.Clear();
                if (MineSpreadsheetGrid.SelectedCells.Count > 0) MineSpreadsheetGrid.SelectedCells.Clear();
            }
            else
            {
                if (_baseHeaderGrid?.SelectedCells.Count > 0) _baseHeaderGrid.SelectedCells.Clear();
                if (_mineHeaderGrid?.SelectedCells.Count > 0) _mineHeaderGrid.SelectedCells.Clear();
            }
            SelectCell(header ? _baseHeaderGrid! : BaseSpreadsheetGrid, row, cell.ColumnIndex);
            SelectCell(header ? _mineHeaderGrid! : MineSpreadsheetGrid, row, cell.ColumnIndex);
        }
        finally
        {
            _isSynchronizingSelection = false;
        }
    }

    private static void SelectCell(
        DataGrid grid,
        SpreadsheetRowViewModel row,
        int columnIndex)
    {
        var dataGridColumnIndex = columnIndex + 1;
        if (dataGridColumnIndex < 1
            || dataGridColumnIndex >= grid.Columns.Count)
        {
            return;
        }

        var column = grid.Columns[dataGridColumnIndex];
        var cell = new DataGridCellInfo(row, column);
        if (!grid.SelectedCells.Contains(cell))
        {
            grid.SelectedCells.Clear();
            grid.SelectedCells.Add(cell);
            grid.CurrentCell = cell;
        }

        if (!IsCellInViewport(grid, row, dataGridColumnIndex)) grid.ScrollIntoView(row, column);
    }

    private static bool IsCellInViewport(DataGrid grid, SpreadsheetRowViewModel row, int columnIndex)
    {
        if (grid.ItemContainerGenerator.ContainerFromItem(row) is not DataGridRow container
            || FindVisualChild<DataGridCellsPresenter>(container)?.ItemContainerGenerator.ContainerFromIndex(columnIndex) is not DataGridCell cell
            || !cell.IsVisible || cell.ActualWidth <= 0 || cell.ActualHeight <= 0) return false;
        var bounds = cell.TransformToAncestor(grid).TransformBounds(new Rect(cell.RenderSize));
        var scroll = FindVisualChild<ScrollViewer>(grid);
        var right = grid.ActualWidth - (scroll?.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0);
        var bottom = grid.ActualHeight - (scroll?.ComputedHorizontalScrollBarVisibility == Visibility.Visible ? SystemParameters.HorizontalScrollBarHeight : 0);
        return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= right && bounds.Bottom <= bottom;
    }

    private void SpreadsheetGrid_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        // Right-click does not move the DataGrid cell selection, so promote the
        // cell under the cursor first. Selection is mirrored across both grids
        // by SelectCell, which keeps the detail panes on the same cell.
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<DataGridCell>(source) is { } cell
            && cell.DataContext is SpreadsheetRowViewModel row
            && _viewModel is not null)
        {
            var columnIndex = cell.Column.DisplayIndex - 1;
            if (columnIndex >= 0)
            {
                _viewModel.SelectCell(row, columnIndex);
                QueueSelectionSynchronization();
            }
        }

        if (_viewModel?.SelectedCell is null)
        {
            e.Handled = true;
        }
    }

    private void SpreadsheetGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Custom display columns have no plain-text ClipboardContentBinding;
        // the explicit copy commands provide the actual cell contents.
        if (e.Key is not (Key.C or Key.Insert)
            || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (_viewModel?.SelectedCell is not { } cell)
        {
            return;
        }

        DiffClipboard.CopyText(ReferenceEquals(sender, MineSpreadsheetGrid) || ReferenceEquals(sender, _mineHeaderGrid)
            ? cell.MineText
            : cell.BaseText);
        e.Handled = true;
    }

    private void CopyBaseSide_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedCell is { } cell)
        {
            DiffClipboard.CopyText(cell.BaseText);
        }
    }

    private void CopyMineSide_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedCell is { } cell)
        {
            DiffClipboard.CopyText(cell.MineText);
        }
    }

    private void CopyBothSides_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedCell is { } cell)
        {
            DiffClipboard.CopyBothSides(cell.BaseText, cell.MineText);
        }
    }

    private void CopyBaseRow_Click(object sender, RoutedEventArgs e)
    {
        CopyRow(useBaseSide: true);
    }

    private void CopyMineRow_Click(object sender, RoutedEventArgs e)
    {
        CopyRow(useBaseSide: false);
    }

    private void CopyRow(bool useBaseSide)
    {
        if (_viewModel?.SelectedRow is { } row)
        {
            DiffClipboard.CopyText(
                DiffClipboard.FormatSpreadsheetSide(row, useBaseSide));
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
