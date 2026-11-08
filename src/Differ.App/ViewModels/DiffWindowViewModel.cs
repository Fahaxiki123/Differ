using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.Core.External;
using Differ.Core.Models;
using Differ.Core.Services;
using Microsoft.Extensions.Logging;

namespace Differ.App.ViewModels;

public sealed partial class DiffWindowViewModel : ObservableObject, IDisposable
{
    private static readonly CompositeFormat DifferencePositionMessageFormat =
        CompositeFormat.Parse(Strings.DifferencePositionFormat);
    private static readonly CompositeFormat ComparisonCompleteMessageFormat =
        CompositeFormat.Parse(Strings.ComparisonCompleteFormat);
    private static readonly CompositeFormat ComparisonFailedMessageFormat =
        CompositeFormat.Parse(Strings.ComparisonFailedFormat);
    private static readonly CompositeFormat InputFileMissingMessageFormat =
        CompositeFormat.Parse(Strings.InputFileMissingFormat);
    private static readonly CompositeFormat InputPathIsDirectoryMessageFormat =
        CompositeFormat.Parse(Strings.InputPathIsDirectoryFormat);
    private static readonly CompositeFormat RevisionMessageFormat =
        CompositeFormat.Parse(Strings.RevisionFormat);
    private static readonly CompositeFormat WarningCountMessageFormat =
        CompositeFormat.Parse(Strings.WarningCountFormat);

    private static readonly Action<ILogger, string, Exception?> LogComparisonFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2001, nameof(LogComparisonFailure)),
            "External file comparison failed for {FileName}");

    private readonly DiffCoordinator _diffCoordinator;
    private readonly ComparisonLoadScheduler _loadScheduler;
    private ExternalDiffSession _session;
    private readonly ILogger<DiffWindowViewModel> _logger;
    private readonly Action<DiffWindowViewModel> _closeRequested;
    private readonly CancellationTokenSource _cancellation = new();
    private IReadOnlyList<DiffRowViewModel> _allRows = [];
    private DiffRowViewModel[] _allChangedRows = [];
    private IReadOnlyList<TextDiffRowViewModel> _allTextRows = [];
    private TextDiffRowViewModel[] _allChangedTextRows = [];
    private IReadOnlyList<DiffRowViewModel> _diffRows = [];
    private IReadOnlyList<TextDiffRowViewModel> _textRows = [];
    private IReadOnlyList<WordDisplayRowViewModel> _wordDisplayRows = [];
    private IReadOnlyList<WordDisplayRowViewModel> _allWordDisplayRows = [];
    private IReadOnlyList<WordDisplayRowViewModel> _changedWordDisplayRows = [];
    private IReadOnlyList<SpreadsheetRowViewModel> _spreadsheetRows = [];
    private DiffRowViewModel[] _changedRows = [];
    private TextDiffRowViewModel[] _changedTextRows = [];
    private SpreadsheetRowViewModel[] _changedSpreadsheetRows = [];
    private Dictionary<string, int> _changedRowIndexes =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private Dictionary<string, int> _changedTextRowIndexes =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, int> _changedSpreadsheetRowIndexes =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private IReadOnlyList<SpreadsheetColumnViewModel> _spreadsheetColumns = [];
    private DiffRowViewModel? _selectedDiffRow;
    private TextDiffRowViewModel? _selectedTextRow;
    private SpreadsheetRowViewModel? _selectedSpreadsheetRow;
    private SpreadsheetCellSelectionViewModel? _selectedSpreadsheetCell;
    private WorksheetTabViewModel? _selectedWorksheet;
    private string _statusText = Strings.ComparisonLoading;
    private string _summaryText = string.Empty;
    private string _fileKindText = string.Empty;
    private string _errorText = string.Empty;
    private int _differenceCount;
    private bool _isBusy;
    private bool _isSpreadsheetView;
    private bool _isTextView;
    private bool _isWordView;
    private bool _isLoaded;
    private bool _isDisposed;
    private bool _isActive;
    private IReadOnlyList<WorksheetTabViewModel> _worksheetTabs = [];
    private IReadOnlyList<string> _warnings = [];

    public DiffWindowViewModel(
        DiffCoordinator diffCoordinator,
        ComparisonLoadScheduler loadScheduler,
        ExternalDiffSession session,
        ILogger<DiffWindowViewModel> logger,
        Action<DiffWindowViewModel> closeRequested)
    {
        _diffCoordinator = diffCoordinator;
        _loadScheduler = loadScheduler;
        _session = session;
        _logger = logger;
        _closeRequested = closeRequested;

        var request = session.Request;
        FileName = GetFileName(request);
        WindowTitle = $"{FileName} — {Strings.ExternalDiffAppTitle}";
        BaseTitle = GetDisplayName(
            request.BaseDisplayName,
            request.BaseFilePath,
            Strings.BaseSide);
        MineTitle = GetDisplayName(
            request.MineDisplayName,
            request.MineFilePath,
            Strings.MineSide);
        BasePath = request.BaseFilePath;
        MinePath = request.MineFilePath;
        BaseRevisionText = FormatRevision(request.BaseRevision);
        MineRevisionText = FormatRevision(request.MineRevision);
        var extension = Path.GetExtension(request.FileTypePath);
        FileKindGlyph = GetFileKindGlyph(extension);
        _isSpreadsheetView = IsSpreadsheetExtension(extension);
        _isWordView = IsWordExtension(extension);
        _isTextView = !_isSpreadsheetView && !_isWordView;
        Options.SetFormat(_isSpreadsheetView ? FileKind.Excel : _isWordView ? FileKind.Word : FileKind.Text, extension);
        ApplyOptionsCommand = new AsyncRelayCommand(ReloadAsync);

        PreviousDifferenceCommand = new RelayCommand(
            () => MoveDifference(-1),
            CanMoveDifference);
        NextDifferenceCommand = new RelayCommand(
            () => MoveDifference(1),
            CanMoveDifference);
        SelectOverviewRowCommand = new RelayCommand<DiffRowViewModel>(
            row =>
            {
                if (row is not null)
                {
                    SelectOverviewRow(row);
                }
            });
        CloseTabCommand = new RelayCommand(() => _closeRequested(this));
        InitializeInteraction();
        InitializeSessionActions();
    }

    public IReadOnlyList<DiffRowViewModel> DiffRows
    {
        get => _diffRows;
        private set
        {
            if (SetProperty(ref _diffRows, value)) UpdateOverviewRows();
        }
    }

    public IReadOnlyList<TextDiffRowViewModel> TextRows
    {
        get => _textRows;
        private set => SetProperty(ref _textRows, value);
    }

    public IReadOnlyList<WordDisplayRowViewModel> WordDisplayRows =>
        _wordDisplayRows;

    public IReadOnlyList<SpreadsheetRowViewModel> SpreadsheetRows
    {
        get => _spreadsheetRows;
        private set => SetProperty(ref _spreadsheetRows, value);
    }

    public IReadOnlyList<WorksheetTabViewModel> WorksheetTabs
    {
        get => _worksheetTabs;
        private set
        {
            if (SetProperty(ref _worksheetTabs, value))
            {
                OnPropertyChanged(nameof(HasWorksheetTabs));
            }
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set
        {
            if (SetProperty(ref _warnings, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
                OnPropertyChanged(nameof(WarningSummaryText));
                OnPropertyChanged(nameof(WarningToolTipText));
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    public IRelayCommand PreviousDifferenceCommand { get; }

    public IRelayCommand NextDifferenceCommand { get; }

    public IRelayCommand<DiffRowViewModel> SelectOverviewRowCommand { get; }

    public IRelayCommand CloseTabCommand { get; }

    public ComparisonOptionsViewModel Options { get; } = new();

    public IAsyncRelayCommand ApplyOptionsCommand { get; }

    public ExternalDiffSession Session => _session;

    public string ComparisonKey => _session.ComparisonKey;

    public string WindowTitle { get; }

    public string FileName { get; }

    public string FileKindGlyph { get; }

    public string BaseTitle { get; }

    public string MineTitle { get; }

    public string BasePath { get; }

    public string MinePath { get; }

    public string BaseRevisionText { get; }

    public string MineRevisionText { get; }

    public int DifferenceCount
    {
        get => _differenceCount;
        private set
        {
            if (SetProperty(ref _differenceCount, value))
            {
                OnPropertyChanged(nameof(HasDifferences));
                OnPropertyChanged(nameof(HasTabBadge));
                OnPropertyChanged(nameof(TabBadgeText));
            }
        }
    }

    public bool HasDifferences => DifferenceCount > 0;

    public bool HasTabBadge => HasError || HasDifferences || IsEditorDirty;

    public string TabBadgeText => HasError
        ? "!"
        : (IsEditorDirty ? "● " : string.Empty) + DifferenceCount.ToString(CultureInfo.CurrentCulture);

    public bool HasWorksheetTabs => WorksheetTabs.Count > 0;

    public bool IsSpreadsheetView
    {
        get => _isSpreadsheetView;
        private set
        {
            if (SetProperty(ref _isSpreadsheetView, value))
            {
                OnPropertyChanged(nameof(IsStandardView));
            }
        }
    }

    public bool IsTextView
    {
        get => _isTextView;
        private set
        {
            if (SetProperty(ref _isTextView, value))
            {
                OnPropertyChanged(nameof(IsStandardView));
            }
        }
    }

    public bool IsWordView
    {
        get => _isWordView;
        private set
        {
            if (SetProperty(ref _isWordView, value))
            {
                OnPropertyChanged(nameof(IsStandardView));
            }
        }
    }

    public bool IsStandardView => !IsSpreadsheetView && !IsTextView && !IsWordView;

    public IReadOnlyList<SpreadsheetColumnViewModel> SpreadsheetColumns =>
        _spreadsheetColumns;

    public WorksheetTabViewModel? SelectedWorksheet
    {
        get => _selectedWorksheet;
        set
        {
            if (SetProperty(ref _selectedWorksheet, value))
            {
                ApplySpreadsheetFilter();
                NotifyNavigationChanged();
            }
        }
    }

    public DiffRowViewModel? SelectedDiffRow
    {
        get => _selectedDiffRow;
        set
        {
            if (SetProperty(ref _selectedDiffRow, value))
            {
                OnPropertyChanged(nameof(DifferencePositionText));
            }
        }
    }

    public TextDiffRowViewModel? SelectedTextRow
    {
        get => _selectedTextRow;
        set
        {
            if (SetProperty(ref _selectedTextRow, value))
            {
                OnPropertyChanged(nameof(DifferencePositionText));
            }
        }
    }

    public SpreadsheetRowViewModel? SelectedSpreadsheetRow
    {
        get => _selectedSpreadsheetRow;
        set
        {
            if (SetProperty(ref _selectedSpreadsheetRow, value))
            {
                if (SelectedWorksheet is { } worksheet
                    && !ReferenceEquals(worksheet.SelectedRow, value))
                {
                    worksheet.SelectedRow = value;
                }

                SelectedSpreadsheetCell = SelectedWorksheet?.SelectedCell;
                OnPropertyChanged(nameof(DifferencePositionText));
            }
        }
    }

    public SpreadsheetCellSelectionViewModel? SelectedSpreadsheetCell
    {
        get => _selectedSpreadsheetCell;
        private set => SetProperty(ref _selectedSpreadsheetCell, value);
    }

    public void SelectOverviewRow(DiffRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!CanInteractWithOverview) return;
        if (IsSpreadsheetView && SelectedWorksheet is { } worksheet)
        {
            SelectedDiffRow = row;
            worksheet.SelectOverviewRow(row);
            return;
        }

        if (_foldCommands.TryGetValue(row.StableId, out var expand))
        {
            expand.Execute(null);
            return;
        }

        if (_textLookup.TryGetValue(row.StableId, out var selectedTextRow))
        {
            SelectTextTarget(selectedTextRow);
        }
        else
        {
            SelectedDiffRow = row;
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string FileKindText
    {
        get => _fileKindText;
        private set => SetProperty(ref _fileKindText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(HasTabBadge));
                OnPropertyChanged(nameof(TabBadgeText));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningSummaryText => string.Format(
        CultureInfo.CurrentCulture,
        WarningCountMessageFormat,
        Warnings.Count);

    public string WarningToolTipText => string.Join(
        Environment.NewLine,
        Warnings);

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool ShowUnchanged
    {
        get => DisplayMode != DiffDisplayMode.DifferencesOnly;
        set => DisplayMode = value ? DiffDisplayMode.All : DiffDisplayMode.DifferencesOnly;
    }

    public string DifferencePositionText
    {
        get
        {
            if (IsEditableText)
                return !EditorResultsCurrent ? Strings.EditorUpdating
                    : _editorSession!.Hunks.Count == 0 ? Strings.NoNavigableDifference
                    : string.Format(CultureInfo.CurrentCulture, EditorPositionFormat,
                        _currentHunk < 0 ? CodeReviewStrings.UnselectedIndex : (_currentHunk + 1).ToString(CultureInfo.CurrentCulture), _editorSession.Hunks.Count);
            if (IsSpreadsheetView)
            {
                var worksheet = SelectedWorksheet;
                if (worksheet is null || worksheet.NavigableDifferenceCount == 0)
                {
                    return Strings.NoNavigableDifference;
                }

                return FormatDifferencePosition(
                    worksheet.SelectedDifferenceIndex,
                    worksheet.NavigableDifferenceCount);
            }

            if (IsTextView || IsWordView)
            {
                if (_changedTextRows.Length == 0)
                {
                    return Strings.NoNavigableDifference;
                }

                var selectedIndex = SelectedTextRow is { } selectedRow
                    && _changedTextRowIndexes.TryGetValue(selectedRow.StableId, out var index)
                    ? index : -1;
                return FormatDifferencePosition(
                    selectedIndex,
                    _changedTextRows.Length);
            }

            if (_changedRows.Length == 0)
            {
                return Strings.NoNavigableDifference;
            }

            var rowIndex = SelectedDiffRow is { } selectedDiffRow
                && _changedRowIndexes.TryGetValue(selectedDiffRow.StableId, out var genericSelectedIndex)
                ? genericSelectedIndex : -1;
            return FormatDifferencePosition(
                rowIndex,
                _changedRows.Length);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DisposeEditor();
        DetachWorksheetTabs(WorksheetTabs);
        CancelSessionActions();
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    public void SelectSpreadsheetCell(
        SpreadsheetRowViewModel? row,
        int columnIndex)
    {
        if (SelectedWorksheet is not { } worksheet)
        {
            if (IsSpreadsheetView)
            {
                DiffRows = [];
            }

            SelectedSpreadsheetCell = null;
            return;
        }

        worksheet.SelectCell(row, columnIndex);
        SelectedSpreadsheetRow = worksheet.SelectedRow;
        SelectedSpreadsheetCell = worksheet.SelectedCell;
    }

    private string? ValidateInput()
    {
        var baseError = ValidateFile(
            _session.BaseSnapshotPath,
            Strings.BaseSide);
        if (baseError is not null)
        {
            return baseError;
        }

        return ValidateFile(_session.MineSnapshotPath, Strings.MineSide);
    }

    private static string? ValidateFile(string path, string sideName)
    {
        if (Directory.Exists(path))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                InputPathIsDirectoryMessageFormat,
                sideName,
                path);
        }

        return File.Exists(path)
            ? null
            : string.Format(
                CultureInfo.CurrentCulture,
                InputFileMissingMessageFormat,
                sideName,
                path);
    }

    private void ApplyFilter()
    {
        EnsureInteractionSources();
        var selectedStableId = SelectedDiffRow?.StableId;
        var selectedTextStableId = SelectedTextRow?.StableId;
        ApplyTextProjection();

        SelectedDiffRow = selectedStableId is null
            ? DiffRows.FirstOrDefault(row => row.Kind != DiffKind.Unchanged)
            : DiffRows.FirstOrDefault(row => row.StableId == selectedStableId)
                ?? DiffRows.FirstOrDefault(row => row.Kind != DiffKind.Unchanged);

        SelectedTextRow = selectedTextStableId is null
            ? TextRows.FirstOrDefault(row => row.Kind != DiffKind.Unchanged)
            : TextRows.FirstOrDefault(
                row => row.StableId == selectedTextStableId)
                ?? TextRows.FirstOrDefault(
                    row => row.Kind != DiffKind.Unchanged);

        ApplySpreadsheetFilter();
        NotifyNavigationChanged();
    }

    private void ApplySpreadsheetFilter()
    {
        if (SelectedWorksheet is not { } worksheet)
        {
            if (_spreadsheetColumns.Count > 0)
            {
                _spreadsheetColumns = [];
                OnPropertyChanged(nameof(SpreadsheetColumns));
            }

            SpreadsheetRows = [];
            _changedSpreadsheetRows = [];
            _changedSpreadsheetRowIndexes =
                new Dictionary<string, int>(StringComparer.Ordinal);
            SelectedSpreadsheetRow = null;
            return;
        }

        worksheet.ApplyFilter(DisplayMode);
        DiffRows = worksheet.OverviewRows;
        SpreadsheetRows = worksheet.Rows;
        _changedSpreadsheetRows = worksheet.ChangedRows;
        _changedSpreadsheetRowIndexes = worksheet.ChangedRowIndexes;

        if (!HaveSameSpreadsheetColumns(_spreadsheetColumns, worksheet.Columns))
        {
            _spreadsheetColumns = worksheet.Columns;
            OnPropertyChanged(nameof(SpreadsheetColumns));
        }

        SelectedSpreadsheetRow = worksheet.SelectedRow;
        SelectedSpreadsheetCell = worksheet.SelectedCell;
    }

    private void NotifyNavigationChanged()
    {
        PreviousDifferenceCommand.NotifyCanExecuteChanged();
        NextDifferenceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DifferencePositionText));
    }

    private void AttachWorksheetTabs(
        IEnumerable<WorksheetTabViewModel> worksheets)
    {
        foreach (var worksheet in worksheets)
        {
            worksheet.PropertyChanged += OnWorksheetPropertyChanged;
        }
    }

    private void DetachWorksheetTabs(
        IEnumerable<WorksheetTabViewModel> worksheets)
    {
        foreach (var worksheet in worksheets)
        {
            worksheet.PropertyChanged -= OnWorksheetPropertyChanged;
        }
    }

    private void OnWorksheetPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (sender is not WorksheetTabViewModel worksheet
            || !ReferenceEquals(worksheet, SelectedWorksheet))
        {
            return;
        }

        if (string.Equals(
            e.PropertyName,
            nameof(WorksheetTabViewModel.Rows),
            StringComparison.Ordinal))
        {
            SpreadsheetRows = worksheet.Rows;
        }
        else if (string.Equals(
            e.PropertyName,
            nameof(WorksheetTabViewModel.OverviewRows),
            StringComparison.Ordinal))
        {
            DiffRows = worksheet.OverviewRows;
        }
        else if (string.Equals(
            e.PropertyName,
            nameof(WorksheetTabViewModel.SelectedRow),
            StringComparison.Ordinal))
        {
            SelectedSpreadsheetRow = worksheet.SelectedRow;
        }
        else if (string.Equals(
            e.PropertyName,
            nameof(WorksheetTabViewModel.SelectedCell),
            StringComparison.Ordinal))
        {
            SelectedSpreadsheetCell = worksheet.SelectedCell;
        }
        else if (e.PropertyName == nameof(WorksheetTabViewModel.SelectedDifferenceIndex))
        {
            OnPropertyChanged(nameof(DifferencePositionText));
        }
        else if (e.PropertyName == nameof(WorksheetTabViewModel.DisplayMode)
            && DisplayMode != worksheet.DisplayMode)
        {
            DisplayMode = worksheet.DisplayMode;
        }
    }

    private static bool HaveSameSpreadsheetColumns(
        IReadOnlyList<SpreadsheetColumnViewModel> left,
        IReadOnlyList<SpreadsheetColumnViewModel> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftColumn = left[index];
            var rightColumn = right[index];
            if (!string.Equals(
                    leftColumn.Key,
                    rightColumn.Key,
                    StringComparison.Ordinal)
                || leftColumn.Width != rightColumn.Width
                || leftColumn.HasDifferences != rightColumn.HasDifferences)
            {
                return false;
            }
        }

        return true;
    }

    private bool CanMoveDifference()
    {
        if (IsEditableText) return EditorResultsCurrent && _editorSession!.Hunks.Count > 0;
        if (IsSpreadsheetView)
        {
            return SelectedWorksheet?.NavigableDifferenceCount > 0;
        }

        return IsTextView || IsWordView
            ? GetChangedTextRows().Length > 0
            : GetChangedRows().Length > 0;
    }

    private void MoveDifference(int offset)
    {
        if (IsEditableText) { MoveEditorDifference(offset); return; }
        if (IsSpreadsheetView)
        {
            SelectedWorksheet?.MoveDifference(offset);
            return;
        }

        if (IsTextView || IsWordView)
        {
            var textRows = GetChangedTextRows();
            if (textRows.Length == 0)
            {
                return;
            }

            var currentTextIndex = SelectedTextRow is not null
                && _changedTextRowIndexes.TryGetValue(
                    SelectedTextRow.StableId,
                    out var selectedTextIndex)
                ? selectedTextIndex
                : -1;
            var nextTextIndex = currentTextIndex < 0
                ? 0
                : (currentTextIndex + offset + textRows.Length)
                    % textRows.Length;
            SelectTextTarget(textRows[nextTextIndex]);
            return;
        }

        var changedRows = GetChangedRows();
        if (changedRows.Length == 0)
        {
            return;
        }

        var currentIndex = SelectedDiffRow is not null
            && _changedRowIndexes.TryGetValue(
                SelectedDiffRow.StableId,
                out var selectedRowIndex)
            ? selectedRowIndex
            : -1;
        var nextIndex = currentIndex < 0
            ? 0
            : (currentIndex + offset + changedRows.Length) % changedRows.Length;
        SelectedDiffRow = changedRows[nextIndex];
    }

    private DiffRowViewModel[] GetChangedRows()
    {
        return _changedRows;
    }

    private SpreadsheetRowViewModel[] GetChangedSpreadsheetRows()
    {
        return _changedSpreadsheetRows;
    }

    private TextDiffRowViewModel[] GetChangedTextRows()
    {
        return _changedTextRows;
    }

    private void SetViewKind(FileKind kind)
    {
        IsSpreadsheetView = kind == FileKind.Excel;
        IsTextView = kind is FileKind.Text or FileKind.Binary;
        IsWordView = kind == FileKind.Word;
        if (kind != FileKind.Binary)
        {
            Options.SetFormat(kind, Path.GetExtension(_session.Request.FileTypePath));
        }
    }

    private static bool IsSpreadsheetExtension(string extension)
    {
        return extension.ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xls" or ".csv";
    }

    private static bool IsWordExtension(string extension)
    {
        return extension.ToLowerInvariant() is ".docx" or ".doc" or ".rtf";
    }

    private static string GetFileKindGlyph(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".docx" or ".doc" or ".rtf" => "W",
            ".xlsx" or ".xlsm" or ".xls" or ".csv" => "X",
            ".txt" or ".md" or ".json" or ".xml" => "T",
            _ => "≡"
        };
    }

    private static string FormatDifferencePosition(
        int selectedIndex,
        int count)
    {
        return string.Format(
            CultureInfo.CurrentCulture,
            DifferencePositionMessageFormat,
            selectedIndex < 0 ? "—" : (selectedIndex + 1).ToString(CultureInfo.CurrentCulture),
            count);
    }

    private static string GetFileName(ExternalDiffRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.OriginalFileName))
        {
            return request.OriginalFileName;
        }

        var fileName = Path.GetFileName(request.MineFilePath);
        return string.IsNullOrWhiteSpace(fileName)
            ? Strings.ProductName
            : fileName;
    }

    private static string GetDisplayName(
        string? requestedName,
        string path,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            return requestedName;
        }

        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName)
            ? fallback
            : fileName;
    }

    private static string FormatRevision(string? revision)
    {
        return string.IsNullOrWhiteSpace(revision)
            ? Strings.RevisionUnknown
            : string.Format(
                CultureInfo.CurrentCulture,
                RevisionMessageFormat,
                revision);
    }

    private static string LocalizeFileKind(FileKind kind)
    {
        return kind switch
        {
            FileKind.Text => Strings.FileKindText,
            FileKind.Word => Strings.FileKindWord,
            FileKind.Excel => Strings.FileKindExcel,
            FileKind.Binary => Strings.FileKindBinary,
            _ => Strings.FileKindUnsupported
        };
    }
}

