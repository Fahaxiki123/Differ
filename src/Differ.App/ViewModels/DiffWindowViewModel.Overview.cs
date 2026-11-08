namespace Differ.App.ViewModels;

public sealed partial class DiffWindowViewModel
{
    private double _overviewViewportStart;
    private double _overviewViewportEnd = 1;
    private IReadOnlyList<DiffRowViewModel> _editorOverviewRows = [];

    // A retained paint snapshot is never used as an authoritative interaction source.
    public IReadOnlyList<DiffRowViewModel> OverviewRows => IsEditableText ? _editorOverviewRows : DiffRows;
    public bool CanInteractWithOverview => !IsEditableText || EditorResultsCurrent;
    public double OverviewViewportStart => _overviewViewportStart;
    public double OverviewViewportEnd => _overviewViewportEnd;
    public event EventHandler<OverviewScrollEventArgs>? OverviewScrollRequested;

    public void SetOverviewViewport(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end))
        {
            return;
        }

        start = Math.Clamp(start, 0, 1);
        end = Math.Clamp(end, start, 1);
        SetProperty(ref _overviewViewportStart, start, nameof(OverviewViewportStart));
        SetProperty(ref _overviewViewportEnd, end, nameof(OverviewViewportEnd));
    }

    public void ScrollFromOverview(int wheelDelta)
    {
        if (CanInteractWithOverview) OverviewScrollRequested?.Invoke(this, new OverviewScrollEventArgs(wheelDelta));
    }

    public void CenterFromOverview(double position)
    {
        if (CanInteractWithOverview) OverviewScrollRequested?.Invoke(this, new OverviewScrollEventArgs(0, Math.Clamp(position, 0, 1)));
    }

    private void UpdateOverviewRows()
    {
        if (IsEditableText && EditorResultsCurrent) _editorOverviewRows = DiffRows;
        OnPropertyChanged(nameof(OverviewRows));
    }

    private void ClearEditorOverview()
    {
        _editorOverviewRows = [];
        OnPropertyChanged(nameof(OverviewRows));
    }
}

public sealed class OverviewScrollEventArgs(int wheelDelta, double? targetFraction = null) : EventArgs
{
    public int WheelDelta { get; } = wheelDelta;
    public double? TargetFraction { get; } = targetFraction;
}
