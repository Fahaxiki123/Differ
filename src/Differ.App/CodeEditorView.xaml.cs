using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Formats.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Differ.App;

public partial class CodeEditorView : UserControl, IComparisonPaneFocusTarget
{
    public static readonly DependencyProperty ReadingScaleProperty = DependencyProperty.Register(
        nameof(ReadingScale), typeof(double), typeof(CodeEditorView),
        new FrameworkPropertyMetadata(1.0, OnReadingScaleChanged));

    public double ReadingScale
    {
        get => (double)GetValue(ReadingScaleProperty);
        set => SetValue(ReadingScaleProperty, value);
    }

    private static void OnReadingScaleChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditorView)source;
        if (editor._ready && !editor._disposed) editor.UpdateTheme();
    }

    private const string EditorOrigin = "https://differ.editor.local";
    private DiffWindowViewModel? _owner;
    private bool _initialized;
    private bool _ready;
    private bool _disposed;
    private bool _saving;
    private TaskCompletionSource? _flush;
    private string? _flushId;
    private TaskCompletionSource? _modeChange;
    private string? _modeChangeId;

    public CodeEditorView() => InitializeComponent();

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // At narrow widths the tab badge and header tooltip retain the save state;
        // commands keep their full hit targets beside the document they affect.
        MineSaveStatus.Visibility = ActualWidth >= 1000 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    internal bool IsEditorReady => _ready;
    internal bool IsEditorDisposed => _disposed;
    internal long RenderedVersion { get; private set; }
    internal double LastInputFrameMilliseconds { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _disposed || DataContext is not DiffWindowViewModel owner) return;
        _initialized = true;
        _owner = owner;
        owner.EditorCommandRequested += OnEditorCommand;
        owner.EditorDisposed += OnEditorDisposed;
        owner.PropertyChanged += OnOwnerPropertyChanged;
        owner.OverviewScrollRequested += OnOverviewScroll;
        owner.ConfirmDiscardEditorAsync = ConfirmDiscardAsync;
        owner.SynchronizeEditorAsync = FlushAsync;
        owner.ApplyEditorModeAsync = ApplyModeAsync;
        owner.SaveEditorRequestedAsync = SaveAsync;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Differ", "EditorRuntime"),
                new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update"));
            if (_disposed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_disposed) return;
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.SetVirtualHostNameToFolderMapping("differ.editor.local", Path.Combine(AppContext.BaseDirectory, "Editor"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => args.Cancel = !args.Uri.StartsWith(EditorOrigin + "/", StringComparison.Ordinal);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!args.Request.Uri.StartsWith(EditorOrigin + "/", StringComparison.Ordinal)
                    && !args.Request.Uri.StartsWith("blob:" + EditorOrigin + "/", StringComparison.Ordinal))
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Offline editor", "");
            };
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += (_, _) => ShowFailure(Strings.EditorProcessFailed);
            core.Navigate(EditorOrigin + "/index.html");
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
        {
            ShowFailure(Strings.EditorStartupFailed + Environment.NewLine + exception.GetType().Name);
        }
    }

    private void LoadDocument()
    {
        if (_owner?.EditorSession is not { } editor) return;
        Send(new
        {
            type = "load",
            session = editor.Id.ToString("N"),
            version = editor.Version,
            original = editor.OriginalText,
            modified = editor.Text,
            editMode = _owner.IsEditMode,
            language = GetLanguage(_owner.Session.Request.FileTypePath),
            dark = IsDarkTheme(),
            palette = CreatePalette(),
            navigationLabels = new
            {
                currentBlock = CodeReviewStrings.CurrentBlock,
                line = CodeReviewStrings.Line,
                lines = CodeReviewStrings.Lines,
                missingAtStart = CodeReviewStrings.MissingAtStart,
                missingAfter = CodeReviewStrings.MissingAfter,
                notSelected = CodeReviewStrings.NotSelected,
                noDifferences = CodeReviewStrings.NoDifferences,
                updating = CodeReviewStrings.Updating,
                added = CodeReviewStrings.Added,
                deleted = CodeReviewStrings.Deleted,
                modified = CodeReviewStrings.Modified,
                unchangedLocation = CodeReviewStrings.UnchangedLocation,
                resultSide = CodeReviewStrings.ResultSide,
                location = CodeReviewStrings.Location,
                adoptCurrent = CodeReviewStrings.AdoptCurrent,
                readOnlyBase = CodeReviewStrings.ReadOnlyBaseMessage,
                readOnlyMine = CodeReviewStrings.ReadOnlyMineMessage
            }
        });
        _owner.PublishEditorProjection();
        Browser.IsEnabled = true;
        StartupStatus.Visibility = Visibility.Collapsed;
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || _owner is null || !e.Source.StartsWith(EditorOrigin + "/", StringComparison.Ordinal)) return;
        try
        {
            using var payload = JsonDocument.Parse(e.WebMessageAsJson);
            var message = payload.RootElement;
            var type = message.GetProperty("type").GetString();
            switch (type)
            {
                case "ready": _ready = true; LoadDocument(); break;
                case "text": _owner.UpdateEditorText(message.GetProperty("session").GetString()!, message.GetProperty("version").GetInt64(), message.GetProperty("text").GetString()!); break;
                case "pending": _owner.InvalidateEditorResults(message.GetProperty("session").GetString()!); break;
                case "inlineRequest":
                    await _owner.RequestEditorInlineAsync(message.GetProperty("version").GetInt64(),
                    message.GetProperty("oldStart").GetInt32(), message.GetProperty("oldEnd").GetInt32(),
                    message.GetProperty("newStart").GetInt32(), message.GetProperty("newEnd").GetInt32()); break;
                case "selected":
                    if (message.GetProperty("session").GetString() == _owner.EditorSession?.Id.ToString("N"))
                        _owner.SelectEditorHunk(message.GetProperty("index").GetInt32(), message.GetProperty("version").GetInt64());
                    break;
                case "adopt":
                    if (_owner.EditorSession is { } activeEditor && activeEditor.Id.ToString("N") == message.GetProperty("session").GetString()
                        && activeEditor.Version == message.GetProperty("version").GetInt64()
                        && _owner.CurrentHunk == message.GetProperty("index").GetInt32()) _owner.AdoptEditorHunk();
                    break;
                case "status":
                    _owner.UpdateEditorViewportState(message.GetProperty("session").GetString()!, message.GetProperty("version").GetInt64(),
                        message.GetProperty("location").GetString()!, message.GetProperty("context").GetString()!,
                        message.GetProperty("language").GetString()!, message.GetProperty("canUndo").GetBoolean(), message.GetProperty("canRedo").GetBoolean());
                    break;
                case "save": await SaveAsync(false); break;
                case "saveAs": await SaveAsync(true); break;
                case "close": _owner.CloseTabCommand.Execute(null); break;
                case "navigate": _owner.MoveEditorDifference(message.GetProperty("direction").GetInt32()); break;
                case "metric":
                    var operation = message.GetProperty("operation").GetString()!;
                    var milliseconds = message.GetProperty("elapsedMs").GetDouble();
                    if (operation == "editor-input-frame") LastInputFrameMilliseconds = milliseconds;
                    PerformanceLog.Write(operation, "Text", TimeSpan.FromMilliseconds(milliseconds));
                    break;
                case "rendered": RenderedVersion = message.GetProperty("version").GetInt64(); break;
                case "viewport": _owner.SetOverviewViewport(message.GetProperty("start").GetDouble(), message.GetProperty("end").GetDouble()); break;
                case "flushed": if (message.GetProperty("id").GetString() == _flushId) _flush?.TrySetResult(); break;
                case "modeChanged":
                    if (message.GetProperty("id").GetString() == _modeChangeId
                        && message.GetProperty("session").GetString() == _owner.EditorSession?.Id.ToString("N")) _modeChange?.TrySetResult();
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            ShowFailure(Strings.EditorMessageFailed + Environment.NewLine + exception.GetType().Name);
        }
    }

    private void OnEditorCommand(object? sender, object command)
    {
        // Inspect only the small command discriminator; projections may contain thousands of rows.
        if (command.GetType().GetProperty("type")?.GetValue(command) is "undo" or "redo" or "replace") Browser.Focus();
        Send(command);
    }
    private void Send(object message)
    {
        if (_ready && !_disposed) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_owner is null) return;
        if (e.PropertyName == nameof(DiffWindowViewModel.EditorSession))
        {
            if (_owner.EditorSession is null) OnEditorDisposed(sender, EventArgs.Empty);
            else LoadDocument();
        }
        else if (e.PropertyName == nameof(DiffWindowViewModel.SelectedTextRow) && !_owner.IsApplyingEditorProjection && _owner.SelectedTextRow is { } row)
            Send(new { type = "reveal", mine = row.NewIndex is not null, line = row.NewIndex ?? row.OldIndex ?? 1, version = _owner.EditorSession?.Version });
        else if (e.PropertyName == nameof(DiffWindowViewModel.DisplayMode))
            Send(new { type = "display", mode = (int)_owner.DisplayMode });
    }

    private void OnOverviewScroll(object? sender, OverviewScrollEventArgs e) => Send(new { type = "scroll", delta = e.WheelDelta, fraction = e.TargetFraction });

    internal bool HasBrowserKeyboardFocus => Browser.IsKeyboardFocusWithin;
    internal Task<bool> SaveFromShellAsync(bool saveAs) => SaveAsync(saveAs);
    public void FocusBasePane() => FocusEditorPane(false);
    public void FocusMinePane() => FocusEditorPane(true);
    private void FocusEditorPane(bool mine)
    {
        if (!_ready || _disposed) return;
        Browser.Focus();
        Send(new { type = "focus", mine });
    }
    public void UpdateTheme() => Send(new { type = "theme", dark = IsDarkTheme(), palette = CreatePalette() });

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty && _ready && IsLoaded && !_disposed && e.NewValue is SolidColorBrush)
            _ = Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded && !_disposed) UpdateTheme(); }));
    }

    private object CreatePalette() => new
    {
        background = BrushColor("Sample.SurfaceBrush"),
        foreground = BrushColor("Sample.InkBrush"),
        border = BrushColor("Sample.BorderBrush"),
        focus = BrushColor("Sample.AccentBrush"),
        chrome = BrushColor("Sample.PaneHeaderBrush"),
        muted = BrushColor("Sample.MutedInkBrush"),
        selection = BrushColor("Sample.SelectionBrush"),
        inactiveSelection = BrushColor("Sample.InactiveSelectionBrush"),
        lineNumber = BrushColor("Sample.LineNumberBrush"),
        scrollThumb = BrushColor("Sample.ScrollThumbBrush"),
        scrollHover = BrushColor("Sample.ScrollHoverBrush"),
        scrollPressed = BrushColor("Sample.ScrollPressedBrush"),
        indent = BrushColor("Sample.IndentBrush"),
        missing = BrushColor("Sample.MissingBrush"),
        fontSize = (double)FindResource("Sample.BodyFontSize") * ReadingScale,
        lineHeight = (double)FindResource("Ui.TextLineHeight") * ReadingScale,
        added = BrushColor("Diff.Row.Added.Bg"),
        deleted = BrushColor("Diff.Row.Deleted.Bg"),
        modified = BrushColor("Diff.Row.Modified.Bg"),
        addedText = BrushColor("Diff.Row.Added.Border"),
        deletedText = BrushColor("Diff.Row.Deleted.Border")
    };
    private string BrushColor(string key)
    {
        var color = (TryFindResource(key) as SolidColorBrush
            ?? throw new InvalidOperationException($"Missing theme brush: {key}")).Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private bool IsDarkTheme() => string.Equals(TryFindResource("Theme.Name") as string, "Dark", StringComparison.OrdinalIgnoreCase);

    private async Task FlushAsync()
    {
        if (!_ready || _disposed) return;
        if (_flush is { Task.IsCompleted: false }) { await _flush.Task.WaitAsync(TimeSpan.FromSeconds(5)); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _flush = completion;
        _flushId = Guid.NewGuid().ToString("N");
        Send(new { type = "flush", id = _flushId });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task ApplyModeAsync(bool enabled, CancellationToken token)
    {
        if (!_ready || _disposed) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _modeChange = completion;
        _modeChangeId = Guid.NewGuid().ToString("N");
        var requestedSession = _owner?.EditorSession;
        Send(new { type = "editMode", enabled, id = _modeChangeId, session = requestedSession?.Id.ToString("N") });
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (!_disposed && ReferenceEquals(completion, _modeChange) && ReferenceEquals(requestedSession, _owner?.EditorSession))
            {
                Browser.IsEnabled = true;
                StartupStatus.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // An unacknowledged renderer cannot safely accept further keyboard input.
            // Its already queued batch remains eligible at the host until retry completes.
            if (!_disposed && ReferenceEquals(completion, _modeChange) && ReferenceEquals(requestedSession, _owner?.EditorSession))
            {
                Browser.IsEnabled = false;
                ShowFailure(CodeReviewStrings.ModeChangeFailed);
            }
            throw;
        }
    }

    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (_saving || _owner?.EditorSession is not { } editor || !(saveAs ? _owner.CanEditorSaveAs : _owner.CanEditorSave)) return false;
        _saving = true;
        _owner.SetEditorSaving(true);
        try
        {
            await FlushAsync();
            string? target = null;
            if (saveAs || editor.SaveTarget is null)
            {
                var dialog = new SaveFileDialog
                {
                    Title = Strings.EditorSaveTitle,
                    FileName = _owner.FileName,
                    Filter = Strings.EditorAllFilesFilter,
                    OverwritePrompt = true
                };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
                target = dialog.FileName;
            }
            if (editor.HasMixedLineEndings && (editor.IsDirty || editor.Version != 1))
            {
                var choice = MessageBox.Show(Window.GetWindow(this), Strings.EditorNewlinePrompt,
                    Strings.EditorNewlineTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return false;
                editor.ChooseUniformLineEnding(choice == MessageBoxResult.Yes ? "\r\n" : "\n");
            }
            await _owner.SaveEditorAsync(target);
            return true;
        }
        catch (OperationCanceledException) when (_disposed) { return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.EncoderFallbackException or InvalidOperationException or TimeoutException)
        {
            MessageBox.Show(Window.GetWindow(this), Strings.EditorSaveFailed + Environment.NewLine + exception.Message,
                "Differ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { _saving = false; _owner.SetEditorSaving(false); }
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        try { await FlushAsync(); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { return _disposed && _owner?.IsEditorDirty != true; }
        if (_owner?.IsEditorDirty != true) return true;
        var answer = MessageBox.Show(Window.GetWindow(this), Strings.EditorUnsavedPrompt,
            Strings.EditorUnsavedTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && await SaveAsync(false);
    }

    private void ShowFailure(string message)
    {
        StartupStatus.Text = message;
        StartupStatus.Visibility = Visibility.Visible;
    }

    private void OnEditorDisposed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _disposed = true;
        if (_owner is { } owner)
        {
            owner.EditorCommandRequested -= OnEditorCommand;
            owner.EditorDisposed -= OnEditorDisposed;
            owner.PropertyChanged -= OnOwnerPropertyChanged;
            owner.OverviewScrollRequested -= OnOverviewScroll;
            if (ReferenceEquals(owner.ConfirmDiscardEditorAsync?.Target, this)) owner.ConfirmDiscardEditorAsync = null;
            if (ReferenceEquals(owner.SynchronizeEditorAsync?.Target, this)) owner.SynchronizeEditorAsync = null;
            if (ReferenceEquals(owner.ApplyEditorModeAsync?.Target, this)) owner.ApplyEditorModeAsync = null;
            if (ReferenceEquals(owner.SaveEditorRequestedAsync?.Target, this)) owner.SaveEditorRequestedAsync = null;
        }
        _flush?.TrySetCanceled();
        _modeChange?.TrySetCanceled();
        Browser.Dispose();
    }

    private static string GetLanguage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".lua" => "lua",
        ".cs" => "csharp",
        ".js" or ".jsx" => "javascript",
        ".ts" or ".tsx" => "typescript",
        ".json" => "json",
        ".xml" or ".xaml" => "xml",
        ".py" => "python",
        ".cpp" or ".h" or ".c" => "cpp",
        ".sql" => "sql",
        ".html" => "html",
        ".css" => "css",
        ".md" => "markdown",
        ".yaml" or ".yml" => "yaml",
        _ => "plaintext"
    };
}
