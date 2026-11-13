using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Resources;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed partial class DiffWindowViewModel
{
    private static readonly CompositeFormat FindPositionFormat = CompositeFormat.Parse(Strings.FindStatusFormat);
    private static readonly CompositeFormat LocationFoundMessageFormat = CompositeFormat.Parse(Strings.LocationFoundFormat);
    private static readonly CompositeFormat FoldLabelFormat = CompositeFormat.Parse(Strings.CollapsedContextFormat);
    private DiffDisplayMode _displayMode;
    private string _findText = string.Empty;
    private bool _findMatchCase;
    private bool _findDifferencesOnly;
    private bool _isFindOpen;
    private bool _isGoToOpen;
    private string _goToText = string.Empty;
    private string _locationStatusText = string.Empty;
    private bool _findDirty = true;
    private bool _isFinding;
    private long _findVersion;
    private CancellationTokenSource? _findCancellation;
    private int _findIndex = -1;
    private readonly List<SearchHit> _findMatches = [];
    private IReadOnlyList<TextDiffRowViewModel>? _interactionTextSource;
    private IReadOnlyList<DiffRowViewModel>? _interactionOverviewSource;
    private IReadOnlyList<WorksheetTabViewModel>? _interactionWorksheets;
    private ContextFoldProjection<TextDiffRowViewModel>? _textProjection;
    private ContextFoldProjection<WordDisplayRowViewModel>? _wordProjection;
    private ReviewInteractionIndex? _interactionIndex;
    private Dictionary<string, TextDiffRowViewModel> _textLookup = new(StringComparer.Ordinal);
    private Dictionary<string, int> _textPositions = new(StringComparer.Ordinal);
    private Dictionary<string, int> _wordPositions = new(StringComparer.Ordinal);
    private Dictionary<int, TextDiffRowViewModel> _oldLineLookup = [];
    private Dictionary<int, TextDiffRowViewModel> _newLineLookup = [];
    private Dictionary<string, DiffRowViewModel> _allOverviewLookup = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IRelayCommand> _foldCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WordDisplayRowViewModel> _wordFoldRows = new(StringComparer.Ordinal);

    public DiffDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (SetProperty(ref _displayMode, value))
            {
                OnPropertyChanged(nameof(ShowUnchanged));
                ApplyFilter();
            }
        }
    }

    public string FindText
    {
        get => _findText;
        set { if (SetProperty(ref _findText, value ?? string.Empty)) InvalidateFind(); }
    }

    public bool FindMatchCase
    {
        get => _findMatchCase;
        set { if (SetProperty(ref _findMatchCase, value)) InvalidateFind(); }
    }

    public bool FindDifferencesOnly
    {
        get => _findDifferencesOnly;
        set { if (SetProperty(ref _findDifferencesOnly, value)) InvalidateFind(); }
    }

    public bool IsFindOpen
    {
        get => _isFindOpen;
        set
        {
            if (SetProperty(ref _isFindOpen, value) && !value) InvalidateFind();
        }
    }

    public bool IsGoToOpen
    {
        get => _isGoToOpen;
        set => SetProperty(ref _isGoToOpen, value);
    }

    public string GoToText
    {
        get => _goToText;
        set => SetProperty(ref _goToText, value ?? string.Empty);
    }

    public int FindMatchCount => _findMatches.Count;
    public int FindMatchIndex => _findIndex + 1;
    public string FindStatusText => _isFinding ? Strings.FindSearching : _findDirty || FindText.Length == 0 ? Strings.FindReady
        : FindMatchCount == 0 ? Strings.FindNoMatches
        : string.Format(CultureInfo.CurrentCulture, FindPositionFormat, FindMatchIndex, FindMatchCount);
    public string LocationStatusText
    {
        get => _locationStatusText;
        private set => SetProperty(ref _locationStatusText, value);
    }

    public IRelayCommand OpenFindCommand { get; private set; } = null!;
    public IRelayCommand CloseFindCommand { get; private set; } = null!;
    public IRelayCommand FindNextCommand { get; private set; } = null!;
    public IRelayCommand FindPreviousCommand { get; private set; } = null!;
    public IRelayCommand OpenGoToCommand { get; private set; } = null!;
    public IRelayCommand CloseGoToCommand { get; private set; } = null!;
    public IRelayCommand GoToCommand { get; private set; } = null!;

    private void InitializeInteraction()
    {
        OpenFindCommand = new RelayCommand(() => { IsGoToOpen = false; IsFindOpen = true; });
        CloseFindCommand = new RelayCommand(() => IsFindOpen = false);
        OpenGoToCommand = new RelayCommand(() => { IsFindOpen = false; IsGoToOpen = true; });
        CloseGoToCommand = new RelayCommand(() => IsGoToOpen = false);
        FindNextCommand = new AsyncRelayCommand(() => MoveFindAsync(1));
        FindPreviousCommand = new AsyncRelayCommand(() => MoveFindAsync(-1));
        GoToCommand = new RelayCommand(() => GoTo(GoToText));
    }

    public void FindNext() => FindNextCommand.Execute(null);
    public void FindPrevious() => FindPreviousCommand.Execute(null);

    public WordDisplayRowViewModel? ResolveWordDisplayRow(TextDiffRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!IsWordView)
        {
            return null;
        }

        if (row.IsCollapsed)
        {
            return _wordFoldRows.GetValueOrDefault(row.StableId);
        }

        return _wordPositions.TryGetValue(row.StableId, out var index)
            && _wordProjection?.IsVisible(index, DisplayMode) == true
            ? _allWordDisplayRows[index] : null;
    }

    public bool GoTo(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (IsEditableText && !EditorResultsCurrent) return false;
        EnsureInteractionSources();
        var value = target.Trim();
        string? sheetName = null;
        var separator = value.LastIndexOf('!');
        if (separator >= 0)
        {
            sheetName = value[..separator].Trim().Trim('\'').Replace("''", "'", StringComparison.Ordinal);
            value = value[(separator + 1)..].Trim();
        }

        var columnLength = 0;
        while (columnLength < value.Length && char.IsAsciiLetter(value[columnLength])) columnLength++;
        if (!int.TryParse(value.AsSpan(columnLength), NumberStyles.None, CultureInfo.InvariantCulture, out var rowNumber)
            || rowNumber < 0 || (!IsSpreadsheetView && (columnLength != 0 || sheetName is not null)))
        {
            LocationStatusText = Strings.LocationInvalid;
            return false;
        }

        if (IsSpreadsheetView)
        {
            var candidates = sheetName is not null
                ? WorksheetTabs.Where(sheet => string.Equals(sheet.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                : SelectedWorksheet is { } current
                    ? new[] { current }.Concat(WorksheetTabs.Where(sheet => !ReferenceEquals(sheet, current)))
                    : WorksheetTabs;
            var column = value[..columnLength];
            foreach (var sheet in candidates)
            {
                var columnIndex = 0;
                if (!sheet.TryGetRowAt(rowNumber, out var row) || row is null
                    || (columnLength > 0 && !sheet.TryGetColumnIndex(column, out columnIndex))) continue;
                SelectedWorksheet = sheet;
                sheet.SelectCell(row, columnIndex);
                LocationStatusText = string.Format(CultureInfo.CurrentCulture, LocationFoundMessageFormat,
                    $"{sheet.Name}!{sheet.Columns[columnIndex].Key}{rowNumber}");
                return true;
            }
        }
        else if (_newLineLookup.TryGetValue(rowNumber, out var row) || _oldLineLookup.TryGetValue(rowNumber, out row))
        {
            SelectTextTarget(row);
            LocationStatusText = string.Format(CultureInfo.CurrentCulture, LocationFoundMessageFormat, target);
            return true;
        }

        LocationStatusText = Strings.LocationNotFound;
        return false;
    }

    private void InvalidateFind()
    {
        _findVersion++;
        ClearEditorFindFeedback();
        _findCancellation?.Cancel();
        _findDirty = true;
        _findMatches.Clear();
        _findIndex = -1;
        NotifyFindPosition();
    }

    private void NotifyFindPosition()
    {
        OnPropertyChanged(nameof(FindMatchCount));
        OnPropertyChanged(nameof(FindMatchIndex));
        OnPropertyChanged(nameof(FindStatusText));
    }

    private async Task MoveFindAsync(int direction)
    {
        if (IsEditableText && !EditorResultsCurrent) return;
        EnsureInteractionSources();
        if (_findDirty)
        {
            var query = FindText;
            var matchCase = FindMatchCase;
            var differencesOnly = FindDifferencesOnly;
            var version = _findVersion;
            var sheets = WorksheetTabs;
            var rows = _allTextRows;
            var spreadsheet = IsSpreadsheetView;
            _findCancellation?.Cancel();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
            _findCancellation = cancellation;
            _isFinding = true;
            NotifyFindPosition();
            try
            {
                using var measurement = Differ.Core.Diagnostics.ComparisonMetrics.Measure("search", spreadsheet ? "Excel" : "Text", rows.Count);
                var large = spreadsheet ? sheets.Sum(sheet => (long)sheet.AllRows.Count * sheet.Columns.Count) > 2000 : rows.Count > 2000;
                var hits = large
                    ? await _loadScheduler.RunAsync(() => IsActive, token => Task.Run(() =>
                        CollectFindMatches(query, matchCase, differencesOnly, spreadsheet, sheets, rows, token), token), cancellation.Token)
                    : CollectFindMatches(query, matchCase, differencesOnly, spreadsheet, sheets, rows, cancellation.Token);
                if (_isDisposed || cancellation.IsCancellationRequested || version != _findVersion) return;
                _findMatches.Clear();
                _findMatches.AddRange(hits);
                _findDirty = false;
                _findIndex = -1;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            finally
            {
                if (ReferenceEquals(_findCancellation, cancellation))
                {
                    _findCancellation = null;
                    _isFinding = false;
                    NotifyFindPosition();
                }
            }
        }
        if (_findMatches.Count == 0) { NotifyFindPosition(); return; }
        _findIndex = _findIndex < 0 ? direction > 0 ? 0 : _findMatches.Count - 1
            : (_findIndex + direction + _findMatches.Count) % _findMatches.Count;
        var hit = _findMatches[_findIndex];
        if (hit.Worksheet is { } sheet && hit.SpreadsheetRow is { } row)
        {
            SelectedWorksheet = sheet;
            sheet.SelectCell(row, hit.ColumnIndex);
        }
        else if (hit.TextRow is { } textRow)
        {
            SelectTextTarget(textRow);
            RevealEditorFindTarget(textRow);
        }

        NotifyFindPosition();
    }

    private void ClearEditorFindFeedback()
    {
        if (_editorSession is { } editor)
            SendEditorCommand(new { type = "clearFind", session = editor.Id.ToString("N"), findVersion = _findVersion });
    }

    private void RevealEditorFindTarget(TextDiffRowViewModel row)
    {
        if (_editorSession is not { } editor || !EditorResultsCurrent || FindText.Length == 0) return;
        var comparison = FindMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Keep the existing logical-row match count, preferring the current side's first occurrence.
        var offset = row.NewIndex is not null ? row.NewText.IndexOf(FindText, comparison) : -1;
        var mine = offset >= 0;
        var line = mine ? row.NewIndex : row.OldIndex;
        if (!mine) offset = row.OldText.IndexOf(FindText, comparison);
        if (offset < 0 || line is null) return;
        // Re-send even for the same selected row. Both .NET offsets and Monaco columns use UTF-16.
        SendEditorCommand(new
        {
            type = "revealFind",
            session = editor.Id.ToString("N"),
            version = editor.Version,
            findVersion = _findVersion,
            mine,
            line = line.Value,
            column = offset + 1,
            length = FindText.Length
        });
    }

    private static List<SearchHit> CollectFindMatches(string query, bool matchCase, bool differencesOnly,
        bool spreadsheet, IReadOnlyList<WorksheetTabViewModel> sheets, IReadOnlyList<TextDiffRowViewModel> rows,
        CancellationToken token)
    {
        var hits = new List<SearchHit>();
        if (query.Length == 0) return hits;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (spreadsheet)
        {
            foreach (var sheet in sheets)
            {
                foreach (var row in sheet.AllRows)
                {
                    token.ThrowIfCancellationRequested();
                    for (var column = 0; column < row.BaseCells.Count; column++)
                    {
                        var before = row.BaseCells[column];
                        var after = row.MineCells[column];
                        if (differencesOnly && !before.HasDifference && !after.HasDifference) continue;
                        if (before.DetailsText.Contains(query, comparison) || after.DetailsText.Contains(query, comparison))
                            hits.Add(new SearchHit(null, sheet, row, column));
                    }
                }
            }
        }
        else
        {
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (differencesOnly && !row.HasDiffMarker) continue;
                if (row.OldText.Contains(query, comparison) || row.NewText.Contains(query, comparison))
                    hits.Add(new SearchHit(row, null, null, -1));
            }
        }
        return hits;
    }
    private void EnsureInteractionSources()
    {
        if (ReferenceEquals(_interactionTextSource, _allTextRows)
            && ReferenceEquals(_interactionOverviewSource, _allRows)
            && ReferenceEquals(_interactionWorksheets, WorksheetTabs)) return;
        // Normal loading adopts a prepared index. This empty/preload path also
        // keeps explicit display-mode changes safe before parsing has finished.
        AdoptInteractionIndex(ReviewInteractionIndex.Create(
            IsSpreadsheetView ? FileKind.Excel : IsWordView ? FileKind.Word : FileKind.Text,
            _allRows, _allTextRows, _allWordDisplayRows, _allChangedRows, _allChangedTextRows,
            CancellationToken.None));
    }

    private void AdoptInteractionIndex(ReviewInteractionIndex index)
    {
        _interactionIndex = index;
        _interactionTextSource = _allTextRows;
        _interactionOverviewSource = _allRows;
        _interactionWorksheets = WorksheetTabs;
        _textLookup = index.TextLookup;
        _textPositions = index.TextPositions;
        _oldLineLookup = index.OldLineLookup;
        _newLineLookup = index.NewLineLookup;
        _allOverviewLookup = index.OverviewLookup;
        _textProjection = index.TextProjection;
        _wordProjection = index.WordProjection;
        _wordPositions = index.WordPositions;
        _changedRows = _allChangedRows;
        _changedTextRows = _allChangedTextRows;
        _changedRowIndexes = index.ChangedRowIndexes;
        _changedTextRowIndexes = index.ChangedTextRowIndexes;
        InvalidateFind();
    }

    private void ApplyTextProjection()
    {
        _foldCommands.Clear();
        _wordFoldRows.Clear();
        if (IsWordView)
        {
            _wordDisplayRows = DisplayMode == DiffDisplayMode.DifferencesOnly
                ? _changedWordDisplayRows : _wordProjection!.Build(DisplayMode, CreateCollapsedWordRow);
            if (DisplayMode == DiffDisplayMode.All)
            {
                TextRows = _allTextRows;
                DiffRows = _interactionIndex!.WordOverviewRows;
            }
            else if (DisplayMode == DiffDisplayMode.DifferencesOnly)
            {
                TextRows = _interactionIndex!.ChangedWordTextRows;
                DiffRows = _interactionIndex.ChangedWordOverviewRows;
            }
            else
            {
                TextRows = _wordDisplayRows.SelectMany(row => row.ContentRows).ToArray();
                DiffRows = _wordDisplayRows.Select(row => _interactionIndex!.WordOverviewLookup.GetValueOrDefault(row.StableId)
                    ?? ReviewInteractionIndex.CreateWordOverview(row)).ToArray();
            }
        }
        else if (IsTextView)
        {
            TextRows = _textProjection!.Build(DisplayMode, CreateCollapsedTextRow);
            DiffRows = DisplayMode switch
            {
                DiffDisplayMode.All => _allRows,
                DiffDisplayMode.DifferencesOnly => _allChangedRows,
                _ => TextRows.Select(row => _allOverviewLookup.GetValueOrDefault(row.StableId)
                    ?? new DiffRowViewModel(row.StableId, row.Path, row.Kind, row.OldIndex, row.OldText,
                        row.NewIndex, row.NewText, null, row.Depth)).ToArray()
            };
            _wordDisplayRows = [];
        }
        else
        {
            TextRows = [];
            _wordDisplayRows = [];
            DiffRows = DisplayMode == DiffDisplayMode.DifferencesOnly ? _allChangedRows : _allRows;
        }

        OnPropertyChanged(nameof(WordDisplayRows));
    }
    private TextDiffRowViewModel CreateCollapsedTextRow(int start, int count)
    {
        var source = _allTextRows;
        var projection = _textProjection!;
        var command = new RelayCommand(() =>
        {
            if (_isDisposed || !ReferenceEquals(source, _allTextRows)) return;
            projection.Reveal(start);
            ApplyFilter();
            SelectTextTarget(_allTextRows[start]);
        });
        return CreatePlaceholder($"fold:text:{start}:{count}", count, command);
    }

    private WordDisplayRowViewModel CreateCollapsedWordRow(int start, int count)
    {
        var source = _allWordDisplayRows;
        var projection = _wordProjection!;
        var command = new RelayCommand(() =>
        {
            if (_isDisposed || !ReferenceEquals(source, _allWordDisplayRows)) return;
            projection.Reveal(start);
            ApplyFilter();
            SelectTextTarget(_allWordDisplayRows[start].AnchorRow);
        });
        var row = WordDisplayRowViewModel.CreateCollapsed(CreatePlaceholder($"fold:word:{start}:{count}", count, command));
        _wordFoldRows[row.StableId] = row;
        return row;
    }

    private TextDiffRowViewModel CreatePlaceholder(string stableId, int count, IRelayCommand command)
    {
        var label = string.Format(CultureInfo.CurrentCulture, FoldLabelFormat, count);
        _foldCommands[stableId] = command;
        return new TextDiffRowViewModel(stableId, DiffKind.Unchanged, null, null, label, label,
            [new InlineDiffSegment(label, false)], [new InlineDiffSegment(label, false)])
        { IsCollapsed = true, HiddenCount = count, ExpandCommand = command };
    }

    private void SelectTextTarget(TextDiffRowViewModel row)
    {
        if (DisplayMode == DiffDisplayMode.DifferencesOnly && !row.HasDiffMarker)
        {
            DisplayMode = DiffDisplayMode.All;
        }
        else if (DisplayMode == DiffDisplayMode.Context)
        {
            var expanded = IsWordView
                ? _wordPositions.TryGetValue(row.StableId, out var wordPosition) && _wordProjection!.Reveal(wordPosition)
                : _textPositions.TryGetValue(row.StableId, out var textPosition) && _textProjection!.Reveal(textPosition);
            if (expanded) ApplyFilter();
        }

        SelectedTextRow = row;
        if (IsWordView && _wordPositions.TryGetValue(row.StableId, out var position))
        {
            var anchor = _allWordDisplayRows[position].AnchorRow;
            SelectedDiffRow = _allOverviewLookup.GetValueOrDefault(anchor.StableId);
        }
        else
        {
            SelectedDiffRow = _allOverviewLookup.GetValueOrDefault(row.StableId);
        }
    }

    private sealed record SearchHit(TextDiffRowViewModel? TextRow, WorksheetTabViewModel? Worksheet,
        SpreadsheetRowViewModel? SpreadsheetRow, int ColumnIndex);
}


