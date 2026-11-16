using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.App.ViewModels;

namespace Differ.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The WPF Unloaded event releases the layout binding.")]
public partial class SpreadsheetDiffView : UserControl
{
    private ReadingLayoutBinding? _readingLayout;
    private DispatcherOperation? _tabScrollOperation;

    public SpreadsheetDiffView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _readingLayout?.Dispose();
        _readingLayout = ReadingLayoutBinding.Attach(this, 3, true, DetailSplitter, DetailsToggle, DetailsContent, autoCollapsedHeight: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _readingLayout?.Dispose();
        _readingLayout = null;
        _tabScrollOperation?.Abort();
        _tabScrollOperation = null;
    }

    private void WorksheetTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || _tabScrollOperation?.Status == DispatcherOperationStatus.Pending) return;
        _tabScrollOperation = Dispatcher.BeginInvoke(BringSelectedWorksheetTabIntoView, DispatcherPriority.Loaded);
    }

    private void BringSelectedWorksheetTabIntoView()
    {
        _tabScrollOperation = null;
        if (!IsLoaded || WorksheetTabs.SelectedItem is not { } selected
            || ComparisonViewportBridge.FindScrollViewer(WorksheetTabs) is not { } scroll
            || scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible)
            return;

        var viewport = scroll.Template.FindName("PART_ScrollContentPresenter", scroll) as FrameworkElement ?? scroll;
        if (WorksheetTabs.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem tab
            && tab.IsDescendantOf(viewport))
        {
            var bounds = tab.TransformToAncestor(viewport).TransformBounds(new Rect(tab.RenderSize));
            if (bounds.Left >= -0.5 && bounds.Right <= viewport.ActualWidth + 0.5) return;
        }

        WorksheetTabs.ScrollIntoView(selected);
    }

    private void CopyBothSides_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiffWindowViewModel { SelectedWorksheet.SelectedCell: { } cell })
        {
            DiffClipboard.CopyBothSides(cell.BaseText, cell.MineText);
        }
    }
}
