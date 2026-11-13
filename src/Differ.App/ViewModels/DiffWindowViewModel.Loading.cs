using System.Diagnostics;
using System.Globalization;
using System.Text;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.Core.External;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed partial class DiffWindowViewModel
{
    private CancellationTokenSource? _loadCancellation;
    private int _loadVersion;
    private static readonly CompositeFormat SpreadsheetSummaryDetailsFormat =
        CompositeFormat.Parse(ChromeStrings.SpreadsheetSummaryDetailsFormat);

    public Task LoadAsync() => _isLoaded || _isDisposed ? Task.CompletedTask : ReloadAsync();
    public Task ReloadAsync() => LoadCoreAsync(refreshMine: false, CancellationToken.None);
    public Task RefreshMineAsync(CancellationToken cancellationToken = default) => LoadCoreAsync(refreshMine: true, cancellationToken);

    private async Task LoadCoreAsync(bool refreshMine, CancellationToken cancellationToken, DiffOptions? optionsOverride = null)
    {
        if (_isDisposed) return;
        if (refreshMine && IsEditableText && ConfirmDiscardEditorAsync is { } confirm && !await confirm()) return;
        if (refreshMine && IsEditorDirty && ConfirmDiscardEditorAsync is null) return;
        if (refreshMine && _sessionStore is null) throw new InvalidOperationException("Session refresh services are not configured.");
        _isLoaded = true;
        _loadCancellation?.Cancel();
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token, cancellationToken);
        _loadCancellation = loadCancellation;
        var loadToken = loadCancellation.Token;
        var loadVersion = ++_loadVersion;
        var options = optionsOverride ?? Options.ToDiffOptions();
        var originalSession = _session;
        ExternalDiffSession? candidate = null;
        var committed = false;
        ErrorText = string.Empty;
        IsBusy = true;
        StatusText = Strings.ComparisonLoading;
        try
        {
            if (!refreshMine && !string.IsNullOrWhiteSpace(originalSession.PreparationError))
            {
                ErrorText = originalSession.PreparationError;
                StatusText = ErrorText;
                return;
            }
            if (refreshMine)
            {
                candidate = await Task.Run(() => _sessionStore!.RefreshMineAsync(originalSession, loadToken), loadToken);
                loadToken.ThrowIfCancellationRequested();
                _pendingRefreshSession = candidate;
            }
            var comparisonSession = candidate ?? originalSession;
            var validationError = await Task.Run(() => ValidateFile(comparisonSession.BaseSnapshotPath, Strings.BaseSide)
                ?? ValidateFile(comparisonSession.MineSnapshotPath, Strings.MineSide), loadToken);
            if (validationError is not null)
            {
                if (!refreshMine || CurrentDocument is null) ErrorText = validationError;
                StatusText = validationError;
                return;
            }
            if (await TryLoadEditorAsync(refreshMine, comparisonSession, options, loadToken))
            {
                loadToken.ThrowIfCancellationRequested();
                if (loadVersion != _loadVersion || _isDisposed) return;
                if (candidate is not null)
                {
                    _session = candidate;
                    OnPropertyChanged(nameof(Session));
                    OnPropertyChanged(nameof(ComparisonKey));
                    OnPropertyChanged(nameof(SnapshotTimeText));
                    ReleaseSession(originalSession);
                }
                committed = true;
                return;
            }
            var loaded = await _loadScheduler.RunAsync(
                () => IsActive,
                async token =>
                {
                    var comparisonStopwatch = Stopwatch.StartNew();
                    var document = await _diffCoordinator.CompareAsync(new DiffRequest
                    {
                        RelativePath = comparisonSession.Request.FileTypePath,
                        BaseFilePath = comparisonSession.BaseSnapshotPath,
                        CurrentFilePath = comparisonSession.MineSnapshotPath,
                        BaseRevision = comparisonSession.Request.BaseRevision,
                        Options = options
                    }, token).ConfigureAwait(false);
                    PerformanceLog.Write("comparison", document.FileKind.ToString(), comparisonStopwatch.Elapsed);
                    token.ThrowIfCancellationRequested();
                    var presentationStopwatch = Stopwatch.StartNew();
                    var presentation = ComparisonPresentationBuilder.Build(document, token, options);
                    PerformanceLog.Write("presentation-build", document.FileKind.ToString(), presentationStopwatch.Elapsed);
                    return (Document: document, Presentation: presentation);
                }, loadToken);
            loadToken.ThrowIfCancellationRequested();
            if (loadVersion != _loadVersion || _isDisposed) return;
            ClearEditorSession();
            ApplyPresentation(loaded.Presentation);
            CurrentDocument = loaded.Document;
            _lastAppliedOptions = options;
            UpdatePendingOptions();
            if (candidate is not null) _session = candidate;
            committed = true;
            if (candidate is not null)
            {
                OnPropertyChanged(nameof(Session));
                OnPropertyChanged(nameof(ComparisonKey));
                OnPropertyChanged(nameof(SnapshotTimeText));
                ReleaseSession(originalSession);
            }
        }
        catch (OperationCanceledException) when (loadToken.IsCancellationRequested)
        {
            // A newer operation or a closing tab keeps the last committed result.
            if (loadVersion == _loadVersion && !_isDisposed)
                StatusText = HasPendingOptions ? Strings.PendingComparisonOptions : SummaryText;
        }
        catch (Exception exception)
        {
            if (loadVersion != _loadVersion || _isDisposed) return;
            LogComparisonFailure(_logger, FileName, exception);
            var message = string.Format(CultureInfo.CurrentCulture,
                refreshMine ? Strings.RefreshFailedFormat : Strings.ComparisonFailedFormat, exception.Message);
            if (!refreshMine || CurrentDocument is null) ErrorText = message;
            StatusText = message;
        }
        finally
        {
            if (candidate is not null)
            {
                if (ReferenceEquals(_pendingRefreshSession, candidate)) _pendingRefreshSession = null;
                if (!committed) ReleaseSession(candidate);
            }
            if (loadVersion == _loadVersion)
            {
                if (!_isDisposed) IsBusy = false;
            }
            if (ReferenceEquals(_loadCancellation, loadCancellation)) _loadCancellation = null;
            loadCancellation.Dispose();
        }
    }

    private void ApplyPresentation(ComparisonPresentation presentation)
    {
        using var metrics = Differ.Core.Diagnostics.ComparisonMetrics.Measure("ui-apply", presentation.FileKind.ToString(), presentation.AllRows.Count);
        _allRows = presentation.AllRows;
        _allChangedRows = presentation.ChangedRows;
        _allTextRows = presentation.AllTextRows;
        _allChangedTextRows = presentation.ChangedTextRows;
        _allWordDisplayRows = presentation.AllWordRows;
        _changedWordDisplayRows = presentation.ChangedWordRows;
        SetViewKind(presentation.FileKind);
        DetachWorksheetTabs(WorksheetTabs);
        WorksheetTabs = presentation.WorksheetTabs;
        AttachWorksheetTabs(WorksheetTabs);
        _selectedWorksheet = WorksheetTabs.FirstOrDefault(tab => string.Equals(
            tab.Name, presentation.FirstChangedSection, StringComparison.Ordinal)) ?? (WorksheetTabs.Count > 0 ? WorksheetTabs[0] : null);
        OnPropertyChanged(nameof(SelectedWorksheet));
        Warnings = presentation.Warnings;
        FileKindText = LocalizeFileKind(presentation.FileKind);
        DifferenceCount = presentation.FileKind == FileKind.Word ? presentation.ChangedWordRows.Count : presentation.Summary.Changed;
        (string Label, int Count)[] summaryItems =
        [
            (Strings.DiffAdded, presentation.Summary.Added),
            (Strings.DiffDeleted, presentation.Summary.Deleted),
            (Strings.DiffModified, presentation.Summary.Modified),
            (Strings.DiffMoved, presentation.Summary.Moved),
            (Strings.DiffConflict, presentation.Summary.Conflicts),
            (Strings.DiffUnsupported, presentation.Summary.Unsupported)
        ];
        SummaryText = string.Join(" · ", summaryItems
            .Where(item => item.Count != 0)
            .Select(item => $"{item.Label} {item.Count.ToString(CultureInfo.CurrentCulture)}"));
        OfficeSummaryDetail = presentation.FileKind == FileKind.Word ? SummaryText : string.Empty;
        if (presentation.FileKind == FileKind.Word)
            SummaryText = $"{DifferenceCount} 个变更区域 · 段落/表格单元格细项：{SummaryText}";
        if (presentation.FileKind == FileKind.Excel)
        {
            var structureCount = presentation.ChangedRows.Count(row => !row.Metadata.ContainsKey("column"));
            var formattingCount = presentation.ChangedRows.Count(row => row.Metadata.TryGetValue("change.kind", out var kind)
                && kind == nameof(SpreadsheetCellChange.Formatting));
            var cellCount = presentation.ChangedRows.Length - structureCount - formattingCount;
            SummaryText = string.Format(CultureInfo.CurrentCulture, Strings.SpreadsheetSummaryUnitsFormat,
                DifferenceCount, structureCount, cellCount, formattingCount);
            OfficeSummaryDetail = DifferenceCount == 0 ? string.Empty : string.Format(CultureInfo.CurrentCulture,
                SpreadsheetSummaryDetailsFormat, structureCount, cellCount, formattingCount);
        }
        StatusText = IsApplyingEditorProjection && _editorModeError.Length != 0 ? _editorModeError
            : presentation.Summary.Changed == 0 ? Strings.ComparisonIdentical
            : presentation.FileKind == FileKind.Word ? string.Format(CultureInfo.CurrentCulture, Strings.ComparisonCompleteRegionsFormat, DifferenceCount)
            : presentation.FileKind == FileKind.Excel ? string.Format(CultureInfo.CurrentCulture, Strings.ComparisonCompleteEventsFormat, DifferenceCount)
            : string.Format(CultureInfo.CurrentCulture, ComparisonCompleteMessageFormat, presentation.Summary.Changed);
        AdoptInteractionIndex(presentation.InteractionIndex);
        ApplyFilter();
    }
}
