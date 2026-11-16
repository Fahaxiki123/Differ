using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.Core.External;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed partial class DiffWindowViewModel
{
    private static readonly CompositeFormat SnapshotTimestampFormat = CompositeFormat.Parse(Strings.SnapshotTimeFormat);
    private static readonly CompositeFormat RulesFailureFormat = CompositeFormat.Parse(Strings.RulesOperationFailedFormat);
    private static readonly CompositeFormat ExportCompleteFormat = CompositeFormat.Parse(Strings.ExportCompletedFormat);
    private static readonly CompositeFormat ExportRulesMessageFormat = CompositeFormat.Parse(Strings.ExportRulesFormat);
    private ExternalDiffSessionStore? _sessionStore;
    private Action<ExternalDiffSession>? _releaseSession;
    private UserPreferencesService? _preferences;
    private Func<IReadOnlyList<string>>? _protectedPaths;
    private ExternalDiffSession? _pendingRefreshSession;
    private DiffDocument? _currentDocument;
    private DiffOptions? _lastAppliedOptions;
    private AsyncRelayCommand _refreshMineCommand = null!;
    private AsyncRelayCommand _saveRulesCommand = null!;
    private AsyncRelayCommand _restoreRulesCommand = null!;
    private AsyncRelayCommand _toggleIgnoreWhitespaceCommand = null!;

    public IAsyncRelayCommand RefreshMineCommand => _refreshMineCommand;
    public IAsyncRelayCommand SaveRulesCommand => _saveRulesCommand;
    public IAsyncRelayCommand RestoreRulesCommand => _restoreRulesCommand;
    public IAsyncRelayCommand ToggleIgnoreWhitespaceCommand => _toggleIgnoreWhitespaceCommand;
    public bool IsIgnoringWhitespace => _lastAppliedOptions?.IgnoreWhitespace == true;
    public bool CanExport => CurrentDocument is not null && (!IsEditableText || EditorResultsCurrent);
    public bool HasPendingOptions => Options.HasPendingChanges;
    public string SnapshotTimeText => string.Format(CultureInfo.CurrentCulture,
        SnapshotTimestampFormat, _session.MineLastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

    public DiffDocument? CurrentDocument
    {
        get => _currentDocument;
        private set
        {
            if (SetProperty(ref _currentDocument, value)) OnPropertyChanged(nameof(CanExport));
        }
    }

    private void InitializeSessionActions()
    {
        Options.PropertyChanged += OnSessionOptionsChanged;
        _refreshMineCommand = new AsyncRelayCommand(RefreshMineAsync, () => _sessionStore is not null && !_isDisposed);
        _saveRulesCommand = new AsyncRelayCommand(SaveRulesAsync, () => _preferences is not null && !_isDisposed);
        _restoreRulesCommand = new AsyncRelayCommand(RestoreRulesAsync, () => _preferences is not null && !_isDisposed);
        _toggleIgnoreWhitespaceCommand = new AsyncRelayCommand(ToggleIgnoreWhitespaceAsync,
            () => Options.SupportsText && _lastAppliedOptions is not null && !IsBusy && !_isDisposed);
    }

    internal void ConfigureActions(ExternalDiffSessionStore store, Action<ExternalDiffSession> release,
        UserPreferencesService preferences, Func<IReadOnlyList<string>> protectedPaths)
    {
        _sessionStore = store;
        _releaseSession = release;
        _preferences = preferences;
        _protectedPaths = protectedPaths;
        _refreshMineCommand.NotifyCanExecuteChanged();
        _saveRulesCommand.NotifyCanExecuteChanged();
        _restoreRulesCommand.NotifyCanExecuteChanged();
    }

    private void CancelSessionActions()
    {
        Options.PropertyChanged -= OnSessionOptionsChanged;
        _refreshMineCommand.Cancel();
        _saveRulesCommand.Cancel();
        _restoreRulesCommand.Cancel();
        _toggleIgnoreWhitespaceCommand.Cancel();
        _loadCancellation?.Cancel();
        if (_pendingRefreshSession is { } pending)
        {
            _pendingRefreshSession = null;
            ReleaseSession(pending);
        }
    }

    private void OnSessionOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComparisonOptionsViewModel.SupportsText))
            _toggleIgnoreWhitespaceCommand.NotifyCanExecuteChanged();

        if (e.PropertyName is nameof(ComparisonOptionsViewModel.IgnoreWhitespace)
            or nameof(ComparisonOptionsViewModel.IgnoreCase) or nameof(ComparisonOptionsViewModel.IgnoreLineEndings)
            or nameof(ComparisonOptionsViewModel.CompareFormatting) or nameof(ComparisonOptionsViewModel.TextEncodingName)
            or nameof(ComparisonOptionsViewModel.KeyColumnsText) or nameof(ComparisonOptionsViewModel.SpreadsheetRules))
        {
            _loadCancellation?.Cancel();
            UpdatePendingOptions();
        }
    }

    private void UpdatePendingOptions()
    {
        var current = Options.ToDiffOptions();
        var applied = _lastAppliedOptions;
        Options.HasPendingChanges = CurrentDocument is not null && (applied is null
            || current.IgnoreWhitespace != applied.IgnoreWhitespace || current.IgnoreCase != applied.IgnoreCase
            || current.IgnoreLineEndings != applied.IgnoreLineEndings || current.CompareFormatting != applied.CompareFormatting
            || !string.Equals(current.TextEncodingName, applied.TextEncodingName, StringComparison.OrdinalIgnoreCase)
            || !current.KeyColumns.SequenceEqual(applied.KeyColumns, StringComparer.OrdinalIgnoreCase)
            || !current.SpreadsheetRules.SequenceEqual(applied.SpreadsheetRules));
        OnPropertyChanged(nameof(HasPendingOptions));
        OnPropertyChanged(nameof(IsIgnoringWhitespace));
    }

    private async Task ToggleIgnoreWhitespaceAsync(CancellationToken cancellationToken)
    {
        if (!Options.SupportsText || _lastAppliedOptions is not { } applied || IsBusy || _isDisposed) return;
        var toggled = applied with { IgnoreWhitespace = !applied.IgnoreWhitespace };
        // Keep other staged advanced rules separate from the rules used by the current comparison.
        Options.IgnoreWhitespace = toggled.IgnoreWhitespace;
        try
        {
            await LoadCoreAsync(refreshMine: false, cancellationToken, toggled);
        }
        finally
        {
            if (!_isDisposed) OnPropertyChanged(nameof(IsIgnoringWhitespace));
        }
    }

    private void ReleaseSession(ExternalDiffSession session)
    {
        if (_releaseSession is not null) _releaseSession(session);
        else _sessionStore?.TryDelete(session);
    }

    internal IEnumerable<string> GetProtectedPaths()
    {
        yield return _session.Request.BaseFilePath;
        yield return _session.Request.MineFilePath;
        yield return _session.BaseSnapshotPath;
        yield return _session.MineSnapshotPath;
        if (_pendingRefreshSession is { } pending)
        {
            yield return pending.BaseSnapshotPath;
            yield return pending.MineSnapshotPath;
        }
    }

    internal async Task InitializeRulesAndLoadAsync()
    {
        try
        {
            if (_preferences is not null)
            {
                var preset = await _preferences.LoadRulesAsync(Options.FormatKey, _cancellation.Token);
                if (preset is not null && !_isLoaded && !_isDisposed) Options.Apply(preset);
            }
            if (!_isDisposed) await LoadAsync();
        }
        catch (OperationCanceledException) when (_isDisposed) { }
    }

    private async Task SaveRulesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _preferences!.SaveRulesAsync(Options.FormatKey, Options.ToDiffOptions(), cancellationToken);
            if (!_isDisposed) StatusText = Strings.RulesSaved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_isDisposed) StatusText = string.Format(CultureInfo.CurrentCulture, RulesFailureFormat, exception.Message);
        }
    }

    private async Task RestoreRulesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _preferences!.RestoreRulesAsync(Options.FormatKey, cancellationToken);
            if (_isDisposed) return;
            Options.ResetDefaults();
            await ReloadAsync();
            if (!_isDisposed) StatusText = Strings.RulesRestored;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_isDisposed) StatusText = string.Format(CultureInfo.CurrentCulture, RulesFailureFormat, exception.Message);
        }
    }

    public async Task ExportHtmlAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!CanExport) throw new InvalidOperationException(Strings.EditorExportPending);
        var document = CurrentDocument ?? throw new InvalidOperationException(Strings.ExportNoSelection);
        var reportDocument = document with
        {
            Warnings = [string.Format(CultureInfo.CurrentCulture, ExportRulesMessageFormat,
                DescribeAppliedRules(_lastAppliedOptions ?? new DiffOptions())), .. document.Warnings]
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token, cancellationToken);
        await ComparisonExportService.ExportHtmlAsync(reportDocument, FileName, path,
            _protectedPaths?.Invoke() ?? GetProtectedPaths().ToArray(), linked.Token);
        if (!_isDisposed) StatusText = string.Format(CultureInfo.CurrentCulture, ExportCompleteFormat, Path.GetFileName(path));
    }

    private static string DescribeAppliedRules(DiffOptions applied)
    {
        var rules = new List<string>();
        if (applied.TextEncodingName is not null) rules.Add($"{Strings.TextEncoding}: {applied.TextEncodingName}");
        if (applied.IgnoreWhitespace) rules.Add(Strings.IgnoreWhitespace);
        if (applied.IgnoreCase) rules.Add(Strings.IgnoreCase);
        if (applied.IgnoreLineEndings) rules.Add(Strings.IgnoreLineEndings);
        if (applied.CompareFormatting) rules.Add(Strings.CompareFormatting);
        if (applied.KeyColumns.Count > 0) rules.Add($"{Strings.KeyColumnsLabel}: {string.Join(",", applied.KeyColumns)}");
        return rules.Count == 0 ? Strings.RulesDefault : string.Join(" · ", rules);
    }

    public async Task ExportSelectedTextAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!CanExport) throw new InvalidOperationException(Strings.EditorExportPending);
        string oldText;
        string newText;
        string location;
        if (IsSpreadsheetView && SelectedSpreadsheetRow?.IsCollapsed == true)
            throw new InvalidOperationException(Strings.ExportNoSelection);
        if (IsSpreadsheetView && SelectedSpreadsheetCell is { } cell && cell.Kind != DiffKind.Unchanged)
        {
            oldText = cell.BaseText;
            newText = cell.MineText;
            location = $"{SelectedWorksheet?.Name} {cell.BaseAddress} → {cell.MineAddress}";
        }
        else if (IsTextView || IsWordView)
        {
            var row = SelectedTextRow;
            if (row is null || row.IsCollapsed || row.Kind == DiffKind.Unchanged) throw new InvalidOperationException(Strings.ExportNoSelection);
            oldText = row.OldText;
            newText = row.NewText;
            location = row.Path;
        }
        else if (SelectedDiffRow is { } row && row.Kind != DiffKind.Unchanged)
        {
            oldText = row.OldValue;
            newText = row.NewValue;
            location = row.Path;
        }
        else throw new InvalidOperationException(Strings.ExportNoSelection);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token, cancellationToken);
        await ComparisonExportService.ExportTextAsync(location, oldText, newText, path,
            _protectedPaths?.Invoke() ?? GetProtectedPaths().ToArray(), linked.Token);
        if (!_isDisposed) StatusText = string.Format(CultureInfo.CurrentCulture, ExportCompleteFormat, Path.GetFileName(path));
    }
}
