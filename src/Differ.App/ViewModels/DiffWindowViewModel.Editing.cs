using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.Core.Algorithms;
using Differ.Core.Models;
using Differ.Formats.Text;

namespace Differ.App.ViewModels;

public sealed partial class DiffWindowViewModel
{
    private static readonly CompositeFormat EditorStateFormat = CompositeFormat.Parse(Strings.EditorStateFormat);
    private static readonly CompositeFormat EditorHunkCountFormat = CompositeFormat.Parse(Strings.EditorHunkCountFormat);
    private static readonly CompositeFormat EditorSummaryFormat = CompositeFormat.Parse(Strings.EditorSummaryFormat);
    private static readonly CompositeFormat EditorPositionFormat = CompositeFormat.Parse(Strings.EditorPositionFormat);
    private static readonly CompositeFormat SourceFileHintFormat = CompositeFormat.Parse(CodeReviewStrings.SourceFileHint);
    private static readonly CompositeFormat SaveTargetHintFormat = CompositeFormat.Parse(CodeReviewStrings.SaveTargetHint);
    private EditableTextSession? _editorSession;
    private int _currentHunk = -1;
    private CancellationTokenSource? _editorProjectionCancellation;
    private bool _editorEnabled;
    private bool _editorPendingText;
    private string? _editorLoadedEncoding;
    private DiffOptions? _editorRequestedOptions;
    private readonly Dictionary<string, object> _editorInlineCache = new(StringComparer.Ordinal);
    private Dictionary<int, DiffNode> _editorOldLines = [];
    private Dictionary<int, DiffNode> _editorNewLines = [];
    private CancellationTokenSource? _editorInlineCancellation;
    private bool _isEditMode;
    private bool _isEditModeChanging;
    private bool _editorAcceptsInput;
    private bool _editorCanUndo;
    private bool _editorCanRedo;
    private bool _editorSaving;
    private string _editorLocationText = CodeReviewStrings.NotSelected;
    private string _editorLanguage = string.Empty;
    private string _editorFormatText = string.Empty;
    private string _editorModeError = string.Empty;
    private string _editorNotice = string.Empty;
    private int _editorModeGeneration;
    private AsyncRelayCommand? _toggleEditModeCommand;
    private AsyncRelayCommand? _saveEditorCommand;
    private AsyncRelayCommand? _saveEditorAsCommand;
    private RelayCommand? _undoEditorCommand;
    private RelayCommand? _redoEditorCommand;
    private RelayCommand? _adoptEditorCommand;
    internal bool IsApplyingEditorProjection { get; private set; }

    public EditableTextSession? EditorSession => _editorSession;
    public bool IsEditableText => _editorSession is not null;
    public bool IsEditorDirty => _editorPendingText || _editorSession?.IsDirty == true;
    public bool EditorResultsCurrent => !_editorPendingText && _editorSession?.ResultsCurrent == true;
    public bool IsEditMode => _isEditMode;
    public bool IsEditModeChanging => _isEditModeChanging;
    public bool CanEditorUndo => IsEditMode && !IsEditModeChanging && _editorModeError.Length == 0 && _editorCanUndo;
    public bool CanEditorRedo => IsEditMode && !IsEditModeChanging && _editorModeError.Length == 0 && _editorCanRedo;
    public bool CanEditorSave => IsEditableText && !_editorSaving && IsEditorDirty;
    public bool CanEditorSaveAs => IsEditableText && !_editorSaving && (IsEditMode || IsEditorDirty);
    public bool CanEditorAdopt => IsEditMode && !IsEditModeChanging && _editorModeError.Length == 0 && EditorResultsCurrent && _currentHunk >= 0;
    public string EditorSaveStatus => !IsEditableText ? string.Empty : _editorSaving ? CodeReviewStrings.Saving : _editorSession!.SaveTarget is null
        ? IsEditorDirty ? CodeReviewStrings.UnsavedNeedsSaveAs : CodeReviewStrings.NeedsSaveAs
        : IsEditorDirty ? CodeReviewStrings.Unsaved : CodeReviewStrings.Saved;
    public string EditorSaveActionLabel => _editorSession?.SaveTarget is null ? CodeReviewStrings.SaveAsEllipsis : Strings.EditorSave;
    public string EditorBaseIdentity => long.TryParse(_session.Request.BaseRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
        ? "BASE · r" + revision.ToString(CultureInfo.InvariantCulture) : "BASE";
    public bool HasAlternateEditorSaveTarget => _editorSession?.SaveTarget is { } target
        && !string.Equals(target, MinePath, StringComparison.OrdinalIgnoreCase);
    public string EditorSaveTargetName => HasAlternateEditorSaveTarget ? Path.GetFileName(_editorSession!.SaveTarget!) : string.Empty;
    public string EditorMinePathHint => string.Join(Environment.NewLine,
        string.Format(CultureInfo.CurrentCulture, SourceFileHintFormat, MinePath),
        EditorSaveTargetHint,
        HasAlternateEditorSaveTarget ? CodeReviewStrings.RefreshUsesSource : string.Empty).TrimEnd();
    public string EditorRefreshHint => !IsEditableText ? Strings.RefreshMine : string.Join(Environment.NewLine,
        Strings.RefreshMine,
        string.Format(CultureInfo.CurrentCulture, SourceFileHintFormat, MinePath),
        HasAlternateEditorSaveTarget ? EditorSaveTargetHint + Environment.NewLine + CodeReviewStrings.RefreshUsesSource : string.Empty).TrimEnd();
    public string EditorSaveHint => ChromeStrings.SaveShortcut + Environment.NewLine + EditorSaveTargetHint;
    private string EditorSaveTargetHint => string.Format(CultureInfo.CurrentCulture, SaveTargetHintFormat,
        _editorSession?.SaveTarget ?? CodeReviewStrings.SaveTargetUnspecified);
    public string EditorMineIdentity => HasAlternateEditorSaveTarget ? CodeReviewStrings.ResultCopy : _editorSession?.Origin switch
    {
        TextInputOrigin.SvnWorking => CodeReviewStrings.WorkingCopy,
        TextInputOrigin.LocalFile => CodeReviewStrings.LocalFile,
        _ => CodeReviewStrings.ResultCopy
    };
    public string EditorPermissionText => _editorModeError.Length != 0 ? CodeReviewStrings.ModeUnconfirmed : IsEditMode ? CodeReviewStrings.Editable : CodeReviewStrings.ReadOnly;
    public string EditorLocationText => _editorModeError.Length != 0 ? _editorModeError : _editorNotice.Length != 0 ? _editorNotice : _editorLocationText;
    public string EditorFormatText => _editorFormatText;
    public IAsyncRelayCommand ToggleEditModeCommand => _toggleEditModeCommand ??= new AsyncRelayCommand(() => SetEditModeAsync(!IsEditMode), () => IsEditableText && !IsEditModeChanging);
    public IAsyncRelayCommand SaveEditorCommand => _saveEditorCommand ??= new AsyncRelayCommand(async () => { await RequestEditorSaveAsync(false); }, () => CanEditorSave);
    public IAsyncRelayCommand SaveEditorAsCommand => _saveEditorAsCommand ??= new AsyncRelayCommand(async () => { await RequestEditorSaveAsync(true); }, () => CanEditorSaveAs);
    public IRelayCommand UndoEditorCommand => _undoEditorCommand ??= new RelayCommand(UndoEditor, () => CanEditorUndo);
    public IRelayCommand RedoEditorCommand => _redoEditorCommand ??= new RelayCommand(RedoEditor, () => CanEditorRedo);
    public IRelayCommand AdoptEditorCommand => _adoptEditorCommand ??= new RelayCommand(AdoptEditorHunk, () => CanEditorAdopt);
    public string EditorStateText => _editorSession is not { } editor ? string.Empty
        : string.Format(CultureInfo.CurrentCulture, EditorStateFormat, IsEditorDirty ? Strings.EditorUnsaved : Strings.EditorSaved,
            editor.Version, editor.EncodingName, EditorResultsCurrent ? string.Format(CultureInfo.CurrentCulture, EditorHunkCountFormat, editor.Hunks.Count) : Strings.EditorUpdating,
            editor.SaveTarget is null ? Strings.EditorUnknownTarget : editor.Origin == TextInputOrigin.SvnWorking ? Strings.EditorWorkingTarget : Strings.EditorLocalTarget);
    public int CurrentHunk => _currentHunk;
    public event EventHandler<object>? EditorCommandRequested;
    public event EventHandler? EditorDisposed;
    public Func<Task<bool>>? ConfirmDiscardEditorAsync { get; set; }
    public Func<Task>? SynchronizeEditorAsync { get; set; }
    public Func<bool, CancellationToken, Task>? ApplyEditorModeAsync { get; set; }
    public Func<bool, Task<bool>>? SaveEditorRequestedAsync { get; set; }

    public async Task SetEditModeAsync(bool enabled, CancellationToken token = default)
    {
        if (!IsEditableText || _isDisposed || IsEditModeChanging || IsEditMode == enabled && _editorModeError.Length == 0) return;
        var editor = _editorSession;
        var generation = ++_editorModeGeneration;
        _isEditModeChanging = true;
        // During the close handshake the renderer first freezes input, then flushes its final batch.
        // Keep accepting that batch until the ordered acknowledgement has arrived.
        _editorAcceptsInput = true;
        NotifyEditorState();
        try
        {
            if (ApplyEditorModeAsync is { } apply) await apply(enabled, token);
            token.ThrowIfCancellationRequested();
            if (_isDisposed || !ReferenceEquals(editor, _editorSession) || generation != _editorModeGeneration) return;
            _isEditMode = enabled;
            _editorAcceptsInput = enabled;
            _editorModeError = string.Empty;
            StatusText = EditorLocationText;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            if (_isDisposed || !ReferenceEquals(editor, _editorSession) || generation != _editorModeGeneration) return;
            // Do not claim a successful close or discard an unacknowledged document batch.
            _editorAcceptsInput = _isEditMode || enabled;
            _editorModeError = CodeReviewStrings.ModeChangeFailed;
            StatusText = _editorModeError;
        }
        finally
        {
            if (!_isDisposed && ReferenceEquals(editor, _editorSession) && generation == _editorModeGeneration)
            {
                _isEditModeChanging = false;
                NotifyEditorState();
            }
        }
    }

    public Task<bool> RequestEditorSaveAsync(bool saveAs) => (saveAs ? CanEditorSaveAs : CanEditorSave)
        && SaveEditorRequestedAsync is { } save ? save(saveAs) : Task.FromResult(false);

    public void UndoEditor() { if (CanEditorUndo) SendEditorCommand(new { type = "undo" }); }
    public void RedoEditor() { if (CanEditorRedo) SendEditorCommand(new { type = "redo" }); }
    internal void SetEditorSaving(bool value) { _editorSaving = value; NotifyEditorState(); }

    public void UpdateEditorViewportState(string sessionId, long version, string location, string context, string language, bool canUndo, bool canRedo)
    {
        if (_editorSession is not { } editor || editor.Id.ToString("N") != sessionId || version != editor.Version) return;
        _editorCanUndo = canUndo;
        _editorCanRedo = canRedo;
        if (_editorLanguage != language)
        {
            _editorLanguage = language;
            UpdateEditorFormat();
        }
        _editorLocationText = string.IsNullOrWhiteSpace(context) ? location : location + " · " + context;
        NotifyEditorChrome();
    }

    private void ClearEditorSession()
    {
        if (_editorSession is null) return;
        _editorProjectionCancellation?.Cancel();
        _editorInlineCancellation?.Cancel();
        SendEditorCommand(new { type = "clear" });
        _editorSession = null;
        _editorRequestedOptions = null;
        ClearEditorOverview();
        _editorModeGeneration++;
        _isEditModeChanging = false;
        _editorPendingText = _isEditMode = _editorAcceptsInput = _editorCanUndo = _editorCanRedo = false;
        _currentHunk = -1;
        _editorInlineCache.Clear();
        _editorOldLines.Clear();
        _editorNewLines.Clear();
        _editorLocationText = string.Empty;
        _editorModeError = string.Empty;
        _editorNotice = string.Empty;
        _editorLanguage = string.Empty;
        OnPropertyChanged(nameof(EditorSession));
        OnPropertyChanged(nameof(IsEditableText));
        NotifyEditorState();
    }

    internal void EnableDocumentEditor() => _editorEnabled = true;

    public void InvalidateEditorResults(string sessionId)
    {
        if (!_editorAcceptsInput || _editorSession is not { } editor || editor.Id.ToString("N") != sessionId) return;
        _editorPendingText = true;
        _editorProjectionCancellation?.Cancel();
        _editorInlineCancellation?.Cancel();
        editor.InvalidateResults();
        CurrentDocument = null;
        DifferenceCount = 0;
        DiffRows = [];
        InvalidateFind();
        NotifyEditorState();
    }

    private async Task<bool> TryLoadEditorAsync(bool refreshMine, Differ.Core.External.ExternalDiffSession source, DiffOptions options, CancellationToken token)
    {
        if (!_editorEnabled || !new TextDiffAdapter(new TextFileReader()).CanHandle(_session.Request.FileTypePath)) return false;
        if (_editorSession is not null && !refreshMine)
        {
            if (SynchronizeEditorAsync is { } synchronize) await synchronize();
            if (!string.Equals(_editorLoadedEncoding, options.TextEncodingName, StringComparison.OrdinalIgnoreCase))
            {
                if (_editorSession.Version != 1)
                {
                    SetEditorNotice(Strings.EditorEncodingAfterEdit);
                    return true;
                }
            }
            else
            {
                // Comparison options operate on the current memory document, never its old disk snapshot.
                await RecomputeEditorAsync(options);
                return true;
            }
        }
        var watch = Stopwatch.StartNew();
        var editor = await Task.Run(() => EditableTextSession.OpenAsync(source.BaseSnapshotPath,
            source.MineSnapshotPath, source.SessionDirectoryPath, options.TextEncodingName, token), token);
        token.ThrowIfCancellationRequested();
        if (editor is null) return false;
        editor.ProtectPath(source.Request.BaseFilePath);
        editor.RememberInputPath(source.Request.MineFilePath);
        var workingFile = await new Differ.Svn.SvnWorkingFileVerifier().VerifyAsync(source, token);
        token.ThrowIfCancellationRequested();
        if (workingFile is not null) editor.ConfirmSaveTarget(workingFile, TextInputOrigin.SvnWorking);
        else if (source.Request.HasExplicitLocalSaveTarget) await editor.TryConfirmLocalInputAsync(token);
        else if (!string.IsNullOrEmpty(source.Request.MineRevision) && source.Request.MineRevision is not ("WC" or "WORKING"))
            editor.SetReadOnlyOrigin(TextInputOrigin.HistoricalFile);
        token.ThrowIfCancellationRequested();
        _editorInlineCancellation?.Cancel();
        _editorSession = editor;
        ClearEditorOverview();
        _editorModeGeneration++;
        _isEditModeChanging = false;
        _isEditMode = _editorAcceptsInput = _editorCanUndo = _editorCanRedo = false;
        _editorLocationText = CodeReviewStrings.NotSelected;
        _editorModeError = string.Empty;
        _editorNotice = string.Empty;
        _editorLoadedEncoding = options.TextEncodingName;
        _editorPendingText = false;
        CurrentDocument = null;
        _currentHunk = -1;
        SetViewKind(FileKind.Text);
        FileKindText = Strings.EditorFileKind;
        _lastAppliedOptions = options;
        NotifyEditorState();
        OnPropertyChanged(nameof(IsEditableText));
        OnPropertyChanged(nameof(EditorSession));
        PerformanceLog.Write("editor-prepare", "Text", watch.Elapsed);
        await RecomputeEditorAsync(options);
        return true;
    }

    public void UpdateEditorText(string sessionId, long version, string text)
    {
        if (!_editorAcceptsInput || _isDisposed || _editorSession is not { } editor || editor.Id.ToString("N") != sessionId) return;
        if (version == editor.Version && text == editor.Text) return;
        if (version != editor.Version + 1 || !editor.Update(text, version - 1)) return;
        _editorPendingText = false;
        _editorProjectionCancellation?.Cancel();
        _currentHunk = -1;
        CurrentDocument = null;
        DifferenceCount = 0;
        DiffRows = [];
        InvalidateFind();
        NotifyEditorState();
        _ = RecomputeEditorAsync();
    }

    private async Task RecomputeEditorAsync(DiffOptions? requestedOptions = null)
    {
        if (_isDisposed || _editorSession is not { } editor) return;
        var snapshot = editor.CaptureText();
        var version = snapshot.Version;
        _editorNotice = string.Empty;
        editor.InvalidateResults();
        SetProperty(ref _currentHunk, -1, nameof(CurrentHunk));
        CurrentDocument = null;
        NotifyEditorState();
        // Cancellation can resume an inline waiter immediately; publish the invalid state first.
        _editorInlineCancellation?.Cancel();
        _editorProjectionCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        _editorProjectionCancellation = cancellation;
        if (requestedOptions is not null) _editorRequestedOptions = requestedOptions;
        var options = _editorRequestedOptions ?? Options.ToDiffOptions();
        try
        {
            var result = await _loadScheduler.RunAsync(() => IsActive, token => Task.Run(() =>
            {
                var comparison = EditorDiffEngine.Compare(snapshot, options, token);
                var document = EditableTextSession.CreateDocument(comparison, _session.Request.FileTypePath, _session.Request.BaseRevision);
                token.ThrowIfCancellationRequested();
                return (Comparison: comparison, Document: document, Presentation: ComparisonPresentationBuilder.Build(document, token, options),
                    OldLines: document.Nodes.Where(node => node.Kind != DiffKind.Unchanged && node.OldIndex.HasValue).ToDictionary(node => node.OldIndex!.Value),
                    NewLines: document.Nodes.Where(node => node.Kind != DiffKind.Unchanged && node.NewIndex.HasValue).ToDictionary(node => node.NewIndex!.Value));
            }, token), cancellation.Token);
            if (_isDisposed || _editorPendingText || !ReferenceEquals(editor, _editorSession) || version != editor.Version || cancellation.IsCancellationRequested) return;
            if (!editor.AcceptComparison(result.Comparison)) return;
            _editorInlineCache.Clear();
            _editorOldLines = result.OldLines;
            _editorNewLines = result.NewLines;
            IsApplyingEditorProjection = true;
            try { ApplyPresentation(result.Presentation); }
            finally { IsApplyingEditorProjection = false; }
            CurrentDocument = result.Document;
            DifferenceCount = editor.Hunks.Count;
            SummaryText = string.Format(CultureInfo.CurrentCulture, EditorSummaryFormat, editor.Hunks.Count, result.Document.Summary.Changed);
            _lastAppliedOptions = options;
            UpdatePendingOptions();
            NotifyEditorState();
            PublishEditorProjection();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_isDisposed && !_editorPendingText && !cancellation.IsCancellationRequested
                && ReferenceEquals(editor, _editorSession) && version == editor.Version)
            {
                _editorRequestedOptions = _lastAppliedOptions;
                ClearEditorOverview();
                SetEditorNotice(Strings.EditorComparisonFailed + exception.GetType().Name);
                SendEditorCommand(new { type = "projectionFailed", session = editor.Id.ToString("N"), version });
            }
        }
        finally
        {
            if (ReferenceEquals(_editorProjectionCancellation, cancellation)) _editorProjectionCancellation = null;
            cancellation.Dispose();
        }
    }

    public void PublishEditorProjection()
    {
        if (_editorSession is not { ResultsCurrent: true } editor || CurrentDocument is not { } document) return;
        SendEditorCommand(new
        {
            type = "projection",
            version = editor.Version,
            changes = editor.Hunks.Select(hunk => new
            {
                originalStartLineNumber = hunk.OldStartLine,
                originalEndLineNumber = hunk.OldEndLine,
                modifiedStartLineNumber = hunk.NewStartLine,
                modifiedEndLineNumber = hunk.NewEndLine
            }),
            rows = document.Nodes.Where(node => node.Kind != DiffKind.Unchanged).Select(node => new
            { oldLine = node.OldIndex, newLine = node.NewIndex, kind = node.Kind.ToString().ToLowerInvariant() })
        });
    }

    public async Task RequestEditorInlineAsync(long version, int oldStart, int oldEnd, int newStart, int newEnd)
    {
        if (_editorSession is not { ResultsCurrent: true } editor || version != editor.Version) return;
        var nodes = new Dictionary<string, DiffNode>(StringComparer.Ordinal);
        for (var i = Math.Max(1, oldStart); i <= Math.Min(oldEnd, oldStart + 250); i++)
            if (_editorOldLines.TryGetValue(i, out var node)) nodes.TryAdd(node.StableId, node);
        for (var i = Math.Max(1, newStart); i <= Math.Min(newEnd, newStart + 250); i++)
            if (_editorNewLines.TryGetValue(i, out var node)) nodes.TryAdd(node.StableId, node);
        _editorInlineCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        _editorInlineCancellation = cancellation;
        var missing = nodes.Values.Where(node => !_editorInlineCache.ContainsKey(node.StableId)).ToArray();
        var options = _lastAppliedOptions ?? new DiffOptions();
        try
        {
            var calculated = await _loadScheduler.RunAsync(() => IsActive, token => Task.Run(() => missing.Select(node =>
            {
                token.ThrowIfCancellationRequested();
                var inline = EditorInlineDiffer.Compare(node.OldValue, node.NewValue, options.IgnoreWhitespace, options.IgnoreCase, token);
                return (node.StableId, Value: (object)new
                {
                    oldLine = node.OldIndex,
                    newLine = node.NewIndex,
                    oldSegments = inline.OldSegments.Select(segment => new { length = segment.Text.Length, changed = segment.IsChanged }),
                    newSegments = inline.NewSegments.Select(segment => new { length = segment.Text.Length, changed = segment.IsChanged }),
                    approximate = inline.IsApproximate
                });
            }).ToArray(), token), cancellation.Token);
            if (_isDisposed || !ReferenceEquals(editor, _editorSession) || version != editor.Version || !editor.ResultsCurrent || cancellation.IsCancellationRequested) return;
            if (_editorInlineCache.Count + calculated.Length > 512) _editorInlineCache.Clear();
            foreach (var item in calculated) _editorInlineCache[item.StableId] = item.Value;
            SendEditorCommand(new { type = "inline", version, rows = nodes.Keys.Where(_editorInlineCache.ContainsKey).Select(key => _editorInlineCache[key]).ToArray() });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_editorInlineCancellation, cancellation)) _editorInlineCancellation = null; }
    }

    public void SendEditorCommand(object command) => EditorCommandRequested?.Invoke(this, command);

    public void MoveEditorDifference(int direction)
    {
        if (_editorSession is not { ResultsCurrent: true } editor || editor.Hunks.Count == 0) return;
        _currentHunk = _currentHunk < 0 ? direction < 0 ? editor.Hunks.Count - 1 : 0
            : (_currentHunk + direction + editor.Hunks.Count) % editor.Hunks.Count;
        SendEditorCommand(new { type = "revealHunk", index = _currentHunk, version = editor.Version });
        OnPropertyChanged(nameof(CurrentHunk));
        OnPropertyChanged(nameof(DifferencePositionText));
        NotifyEditorChrome();
    }

    public void SelectEditorHunk(int index, long version)
    {
        if (_editorSession is not { ResultsCurrent: true } editor || version != editor.Version || index < -1 || index >= editor.Hunks.Count) return;
        _currentHunk = index;
        OnPropertyChanged(nameof(CurrentHunk));
        OnPropertyChanged(nameof(DifferencePositionText));
        NotifyEditorChrome();
    }

    public void AdoptEditorHunk()
    {
        if (!CanEditorAdopt || _editorSession is not { ResultsCurrent: true } editor) return;
        var replacement = editor.GetLeftReplacement(_currentHunk, editor.Version);
        SendEditorCommand(new
        {
            type = "replace",
            version = editor.Version,
            offset = replacement.Offset,
            length = replacement.Length,
            text = replacement.Text
        });
    }

    public async Task SaveEditorAsync(string? saveAsPath = null, CancellationToken token = default)
    {
        if (_editorSession is not { } editor) return;
        foreach (var path in _protectedPaths?.Invoke() ?? [])
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(_session.Request.MineFilePath), StringComparison.OrdinalIgnoreCase))
                editor.ProtectPath(path);
        // The view flushes the browser's document before invoking this method.
        await Task.Run(() => editor.SaveAsync(saveAsPath, token), token);
        NotifyEditorState();
    }

    private void NotifyEditorState()
    {
        UpdateEditorFormat();
        if (_editorPendingText || !EditorResultsCurrent) _editorLocationText = CodeReviewStrings.Updating;
        OnPropertyChanged(nameof(IsEditorDirty));
        OnPropertyChanged(nameof(EditorStateText));
        OnPropertyChanged(nameof(EditorResultsCurrent));
        OnPropertyChanged(nameof(CanInteractWithOverview));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(TabBadgeText));
        OnPropertyChanged(nameof(HasTabBadge));
        OnPropertyChanged(nameof(DifferencePositionText));
        PreviousDifferenceCommand.NotifyCanExecuteChanged();
        NextDifferenceCommand.NotifyCanExecuteChanged();
        NotifyEditorChrome();
    }

    private void SetEditorNotice(string message)
    {
        _editorNotice = message;
        StatusText = EditorLocationText;
        OnPropertyChanged(nameof(EditorLocationText));
    }

    private void UpdateEditorFormat()
    {
        if (_editorSession is not { } editor) { _editorFormatText = string.Empty; return; }
        var lineEnding = editor.Version == 1 ? editor.OriginalLineEnding : !editor.Text.Contains('\n') ? "none"
            : editor.LineEnding == "\n" ? "LF" : editor.LineEnding == "\r" ? "CR" : "CRLF";
        _editorFormatText = string.Join(" · ", new[] { _editorLanguage, editor.EncodingName, lineEnding.ToUpperInvariant() }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private void NotifyEditorChrome()
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsEditModeChanging));
        OnPropertyChanged(nameof(CanEditorUndo));
        OnPropertyChanged(nameof(CanEditorRedo));
        OnPropertyChanged(nameof(CanEditorSave));
        OnPropertyChanged(nameof(CanEditorSaveAs));
        OnPropertyChanged(nameof(CanEditorAdopt));
        OnPropertyChanged(nameof(EditorSaveStatus));
        OnPropertyChanged(nameof(EditorSaveActionLabel));
        OnPropertyChanged(nameof(EditorBaseIdentity));
        OnPropertyChanged(nameof(EditorMineIdentity));
        OnPropertyChanged(nameof(HasAlternateEditorSaveTarget));
        OnPropertyChanged(nameof(EditorSaveTargetName));
        OnPropertyChanged(nameof(EditorMinePathHint));
        OnPropertyChanged(nameof(EditorRefreshHint));
        OnPropertyChanged(nameof(EditorSaveHint));
        OnPropertyChanged(nameof(EditorPermissionText));
        OnPropertyChanged(nameof(EditorLocationText));
        OnPropertyChanged(nameof(EditorFormatText));
        _toggleEditModeCommand?.NotifyCanExecuteChanged();
        _saveEditorCommand?.NotifyCanExecuteChanged();
        _saveEditorAsCommand?.NotifyCanExecuteChanged();
        _undoEditorCommand?.NotifyCanExecuteChanged();
        _redoEditorCommand?.NotifyCanExecuteChanged();
        _adoptEditorCommand?.NotifyCanExecuteChanged();
    }

    private void DisposeEditor()
    {
        _editorProjectionCancellation?.Cancel();
        _editorInlineCancellation?.Cancel();
        EditorDisposed?.Invoke(this, EventArgs.Empty);
        EditorCommandRequested = null;
        EditorDisposed = null;
        ConfirmDiscardEditorAsync = null;
        SynchronizeEditorAsync = null;
        ApplyEditorModeAsync = null;
        SaveEditorRequestedAsync = null;
    }
}
