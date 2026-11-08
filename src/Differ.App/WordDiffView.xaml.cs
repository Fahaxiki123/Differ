using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.App.ViewModels;

namespace Differ.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WPF owns the view lifetime; Unloaded deterministically releases the viewport event subscriptions, and Loaded creates a fresh binding.")]
public partial class WordDiffView : UserControl, IComparisonPaneFocusTarget
{
    private ComparisonViewportBridge? _overviewBridge;
    private bool _isSynchronizingSelection;
    private readonly DispatcherTimer _selectionFlashTimer;
    private DiffWindowViewModel? _viewModel;
    private WordDisplayRowViewModel? _copyTargetRow;
    private WordParagraphHost? _copyParagraph;

    public WordDiffView()
    {
        InitializeComponent();
        _selectionFlashTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(620)
        };
        _selectionFlashTimer.Tick += SelectionFlashTimer_Tick;
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void FocusBasePane()
    {
        FocusPane(WordDiffSide.Base);
    }

    public void FocusMinePane()
    {
        FocusPane(WordDiffSide.Mine);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as DiffWindowViewModel);
        Dispatcher.BeginInvoke(
            () =>
            {
                _overviewBridge?.Dispose();
                if (ComparisonViewportBridge.FindScrollViewer(WordRows) is { } scroll)
                {
                    _overviewBridge = new ComparisonViewportBridge(this, scroll, WordRows);
                }
                ScrollSelectedRowIntoView(flash: false);
            },
            DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _overviewBridge?.Dispose();
        _overviewBridge = null;
        _selectionFlashTimer.Stop();
        _copyTargetRow = null;
        _copyParagraph = null;
        AttachViewModel(null);
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
        if (string.Equals(
            e.PropertyName,
            nameof(DiffWindowViewModel.SelectedTextRow),
            StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(
                () => ScrollSelectedRowIntoView(flash: true),
                DispatcherPriority.DataBind);
        }
    }

    private void WordRows_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }
        if (e.AddedItems.Count > 0
            && e.AddedItems[0] is WordDisplayRowViewModel displayRow
            && _viewModel is not null)
        {
            // Focusing a group's page/expand button selects its ListBoxItem.
            // Keep an existing selection inside that group rather than reset
            // its revealed window to the first paragraph on every click.
            if (displayRow.IsGroup && _viewModel.SelectedTextRow is { } current && displayRow.Contains(current))
            {
                _selectionFlashTimer.Stop();
                _selectionFlashTimer.Start();
                return;
            }
            _viewModel.SelectedTextRow = displayRow.AnchorRow;
            ScrollSelectedRowIntoView(flash: true);
        }
    }

    private void ScrollSelectedRowIntoView(bool flash)
    {
        if (_viewModel?.SelectedTextRow is { } selectedRow)
        {
            var displayRow = _viewModel.ResolveWordDisplayRow(selectedRow);
            if (displayRow is not null)
            {
                var query = _viewModel.FindText;
                var comparison = _viewModel.FindMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var baseOnlyHit = query.Length > 0 && selectedRow.OldText.Contains(query, comparison)
                    && !selectedRow.NewText.Contains(query, comparison);
                displayRow.Group?.Reveal(selectedRow, baseOnlyHit);
                if (flash)
                {
                    _isSynchronizingSelection = true;
                    try
                    {
                        WordRows.SelectedItem = displayRow;
                    }
                    finally
                    {
                        _isSynchronizingSelection = false;
                    }
                    _selectionFlashTimer.Stop();
                    _selectionFlashTimer.Start();
                }

                WordRows.ScrollIntoView(displayRow);
            }
        }
    }

    private void SelectionFlashTimer_Tick(object? sender, EventArgs e)
    {
        _selectionFlashTimer.Stop();
        WordRows.SelectedItem = null;
    }

    private void WordRows_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        _copyParagraph = e.OriginalSource is DependencyObject paragraphSource
            ? FindAncestor<WordParagraphHost>(paragraphSource) : null;
        var menu = (ContextMenu)Resources["WordRowContextMenu"];
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (Equals(item.Tag, "select-characters")) item.IsEnabled = _copyParagraph is not null;
        // The selection flash clears SelectedItem after 620ms, so the row under
        // the cursor is captured here and used for the whole menu lifetime
        // instead of being re-read when a menu item is clicked.
        _copyTargetRow = e.OriginalSource is DependencyObject source
            ? FindAncestor<ListBoxItem>(source)?.DataContext
                as WordDisplayRowViewModel
            : null;
        _copyTargetRow ??= ResolveSelectedDisplayRow();
        if (_copyTargetRow is null)
        {
            e.Handled = true;
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.SelectedTextRow = _copyTargetRow.AnchorRow;
        }
    }

    private void WordRows_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<RichTextBox>(source) is { Selection.IsEmpty: false }) return;
        if (e.Key is not (Key.C or Key.Insert)
            || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (ResolveSelectedDisplayRow() is not { } displayRow)
        {
            return;
        }

        // A Word row shows both sides side by side with no per-side focus, so
        // Ctrl+C takes both. Single-side copies stay on the context menu.
        DiffClipboard.CopyBothSides(
            DiffClipboard.FormatWordSide(displayRow, useBaseSide: true),
            DiffClipboard.FormatWordSide(displayRow, useBaseSide: false));
        e.Handled = true;
    }

    private void CopyBaseSide_Click(object sender, RoutedEventArgs e)
    {
        CopySide(useBaseSide: true);
    }

    private void SelectParagraphCharacters_Click(object sender, RoutedEventArgs e) => _copyParagraph?.EnableCharacterSelection();

    private void CopyMineSide_Click(object sender, RoutedEventArgs e)
    {
        CopySide(useBaseSide: false);
    }

    private void CopyBothSides_Click(object sender, RoutedEventArgs e)
    {
        if (_copyTargetRow is { } displayRow)
        {
            DiffClipboard.CopyBothSides(
                DiffClipboard.FormatWordSide(displayRow, useBaseSide: true),
                DiffClipboard.FormatWordSide(displayRow, useBaseSide: false));
        }
    }

    private void CopySide(bool useBaseSide)
    {
        if (_copyTargetRow is { } displayRow)
        {
            DiffClipboard.CopyText(
                DiffClipboard.FormatWordSide(displayRow, useBaseSide));
        }
    }

    private WordDisplayRowViewModel? ResolveSelectedDisplayRow()
    {
        if (WordRows.SelectedItem is WordDisplayRowViewModel selectedItem)
        {
            return selectedItem;
        }

        // SelectedItem may already have been cleared by the flash timer, so
        // fall back to the shared row selection the rest of the shell tracks.
        return _viewModel?.SelectedTextRow is { } selectedRow
            ? _viewModel.ResolveWordDisplayRow(selectedRow)
            : null;
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

            // Hit testing over a WordInlineDiffTextBlock can surface a Run,
            // which is a TextElement rather than a Visual, so fall back to the
            // logical tree to bridge back into the visual hierarchy.
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void FocusPane(WordDiffSide side)
    {
        var displayRow = ResolveSelectedDisplayRow()
            ?? WordRows.Items.OfType<WordDisplayRowViewModel>().FirstOrDefault();
        if (displayRow is null)
        {
            Keyboard.Focus(WordRows);
            return;
        }

        WordRows.ScrollIntoView(displayRow);
        WordRows.UpdateLayout();
        if (WordRows.ItemContainerGenerator.ContainerFromItem(displayRow) is ListBoxItem paragraphContainer
            && FindVisualChild<WordParagraphPresenter>(paragraphContainer, candidate => candidate.Side == side) is { } paragraph)
        {
            Keyboard.Focus(paragraph);
            return;
        }
        if (WordRows.ItemContainerGenerator.ContainerFromItem(displayRow)
                is ListBoxItem container
            && FindVisualChild<WordInlineDiffTextBlock>(
                container,
                candidate => candidate.Side == side) is { } textBlock)
        {
            textBlock.Focusable = true;
            Keyboard.Focus(textBlock);
            return;
        }

        Keyboard.Focus(WordRows);
    }

    private static T? FindVisualChild<T>(
        DependencyObject parent,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var nested = FindVisualChild(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
