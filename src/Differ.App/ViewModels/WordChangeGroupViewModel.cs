using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Resources;
using Differ.Core.Algorithms;

namespace Differ.App.ViewModels;

public sealed class WordChangeGroupViewModel : ObservableObject
{
    private static readonly CompositeFormat SummaryFormat = CompositeFormat.Parse(WordReviewStrings.GroupSummary);
    private static readonly CompositeFormat WindowFormat = CompositeFormat.Parse(WordReviewStrings.GroupWindow);
    private static readonly CompositeFormat PreviewFormat = CompositeFormat.Parse(WordReviewStrings.GroupPreview);
    private readonly TextDiffRowViewModel[] _baseRows;
    private readonly TextDiffRowViewModel[] _mineRows;
    private bool _isExpanded;
    private int _windowStart;

    internal WordChangeGroupViewModel(IReadOnlyList<TextDiffRowViewModel> rows)
    {
        _baseRows = rows.Where(row => row.OldIndex is not null).ToArray();
        _mineRows = rows.Where(row => row.NewIndex is not null).ToArray();
        IsSplitMerge = rows[0].IsSplitMergeGroup;
        _isExpanded = Math.Max(_baseRows.Length, _mineRows.Length) <= DiffPerformanceOptions.MaximumWordGroupPreviewParagraphs;
        ToggleCommand = new RelayCommand(() => { _isExpanded = !_isExpanded; _windowStart = 0; Refresh(); });
        PreviousCommand = new RelayCommand(() => { _windowStart = Math.Max(0, _windowStart - WindowSize); Refresh(); }, () => IsExpanded && _windowStart > 0);
        NextCommand = new RelayCommand(() => { _windowStart += WindowSize; Refresh(); }, () => IsExpanded && _windowStart + WindowSize < Count);
        Refresh();
    }

    public bool IsSplitMerge { get; }
    public bool IsExpanded => _isExpanded;
    public int BaseCount => _baseRows.Length;
    public int MineCount => _mineRows.Length;
    public int Count => Math.Max(BaseCount, MineCount);
    public bool CanCollapse => Count > DiffPerformanceOptions.MaximumWordGroupPreviewParagraphs;
    public bool HasMultipleWindows => IsExpanded && Count > WindowSize;
    public bool BaseMissing => VisibleBaseRows.Count == 0;
    public bool MineMissing => VisibleMineRows.Count == 0;
    public string Summary => string.Format(CultureInfo.CurrentCulture, SummaryFormat,
        IsSplitMerge ? WordReviewStrings.GroupSplitMerge : WordReviewStrings.GroupUnresolved, BaseCount, MineCount);
    public string ToggleLabel => IsExpanded ? WordReviewStrings.Collapse : WordReviewStrings.Expand;
    public string WindowLabel => IsExpanded
        ? string.Format(CultureInfo.CurrentCulture, WindowFormat, _windowStart + 1, Math.Min(Count, _windowStart + WindowSize), Count)
        : string.Format(CultureInfo.CurrentCulture, PreviewFormat, WindowSize,
            Math.Max(0, BaseCount - WindowSize), Math.Max(0, MineCount - WindowSize));
    public IReadOnlyList<TextDiffRowViewModel> VisibleBaseRows { get; private set; } = [];
    public IReadOnlyList<TextDiffRowViewModel> VisibleMineRows { get; private set; } = [];
    public IRelayCommand ToggleCommand { get; }
    public IRelayCommand PreviousCommand { get; }
    public IRelayCommand NextCommand { get; }
    private int WindowSize => IsExpanded ? DiffPerformanceOptions.MaximumWordGroupWindowParagraphs : DiffPerformanceOptions.MaximumWordGroupPreviewParagraphs;

    public void Reveal(TextDiffRowViewModel target, bool useBaseSide = false)
    {
        var position = Array.IndexOf(useBaseSide ? _baseRows : _mineRows, target);
        if (position < 0) position = Array.IndexOf(useBaseSide ? _mineRows : _baseRows, target);
        if (position < 0) return;
        if (position == _windowStart) return;
        _isExpanded = true;
        // Merely including the hit at the end of a 24-paragraph window is not
        // enough: that window can be taller than the viewport. Put the actual
        // target first so BringIntoView on the group reveals its text.
        _windowStart = position;
        Refresh();
    }

    private void Refresh()
    {
        // Paging both sides together limits visual generation even for a group
        // with thousands of paragraphs. No independent side collapse creates
        // a false row correspondence; each window labels its original ordinal.
        VisibleBaseRows = _baseRows.Skip(_windowStart).Take(WindowSize).Select(ForGroup).ToArray();
        VisibleMineRows = _mineRows.Skip(_windowStart).Take(WindowSize).Select(ForGroup).ToArray();
        OnPropertyChanged(string.Empty);
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    private TextDiffRowViewModel ForGroup(TextDiffRowViewModel row) => row with
    {
        OldSegments = string.IsNullOrEmpty(row.OldText) ? [] : [new InlineDiffSegment(row.OldText, !IsSplitMerge)],
        NewSegments = string.IsNullOrEmpty(row.NewText) ? [] : [new InlineDiffSegment(row.NewText, !IsSplitMerge)],
        InlineComparison = null
    };
}
