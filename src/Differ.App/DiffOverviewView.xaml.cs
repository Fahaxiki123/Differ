using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Differ.App.Controls;
using Differ.App.Resources;
using Differ.App.ViewModels;

namespace Differ.App;

public partial class DiffOverviewView : UserControl
{
    private static readonly CompositeFormat PositionFormat = CompositeFormat.Parse(Strings.OverviewPositionFormat);
    public DiffOverviewView()
    {
        InitializeComponent();
        Overview.PreviewChanged += OnPreviewChanged;
        Overview.ScrollRequested += (_, e) => (DataContext as DiffWindowViewModel)?.ScrollFromOverview(e.WheelDelta);
        Overview.NavigationRequested += (_, e) => (DataContext as DiffWindowViewModel)?.CenterFromOverview(e.Position);
        Unloaded += (_, _) => PreviewPopup.IsOpen = false;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                PreviewPopup.IsOpen = false;
            }
        };
        DataContextChanged += (_, _) => PreviewPopup.IsOpen = false;
    }

    private void OnPreviewChanged(object? sender, OverviewPreviewEventArgs e)
    {
        if (e.Row is null || !IsVisible || DataContext is DiffWindowViewModel { CanInteractWithOverview: false })
        {
            PreviewPopup.IsOpen = false;
            return;
        }

        PreviewPath.Text = e.Row.Path;
        PreviewKind.Text = e.Row.KindLabel;
        PreviewKind.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush");
        PreviewPosition.Text = string.Format(CultureInfo.CurrentCulture, PositionFormat, e.Position, e.Total);
        BasePreview.Text = string.IsNullOrWhiteSpace(e.BasePreview) ? Strings.OverviewEmpty : e.BasePreview;
        MinePreview.Text = string.IsNullOrWhiteSpace(e.MinePreview) ? Strings.OverviewEmpty : e.MinePreview;
        PreviewPopup.VerticalOffset = Math.Clamp(e.PointerY - 86, 0, Math.Max(0, ActualHeight - 230));
        PreviewPopup.IsOpen = true;
    }
}
