using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Presentation.Theme;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace Differ.App;

public partial class MergeWindow : Window
{
    private const string Origin = "https://differ.editor.local";
    private static readonly DependencyProperty ThemeRevisionProperty = DependencyProperty.Register(
        "ThemeRevision", typeof(object), typeof(MergeWindow),
        new PropertyMetadata(null, (owner, _) => ((MergeWindow)owner).QueueThemeUpdate()));
    private readonly TextMergeViewModel _viewModel;
    private readonly IThemeService _themeService;
    private readonly UserPreferencesService? _preferences;
    private readonly DispatcherTimer _themePreparationTimeout = new(DispatcherPriority.Background);
    private bool _initialized;
    private bool _closed;
    private bool _closing;
    private bool _allowClose;
    private bool _busy;
    private AppTheme _lastTheme;
    private ThemeTransition? _themeTransition;
    private DispatcherOperation? _pendingThemeUpdate;
    private long _themeTransitionId;
    private long? _themeClockTimestamp;
    private double _lastBrowserThemeElapsed;
    private TaskCompletionSource? _flush;
    private string? _flushId;
    private bool _showBase;
    private long _preferenceSequence;
    private string? _preferenceWarning;
    private Task _pendingPreferenceSave = Task.CompletedTask;

    public MergeWindow(TextMergeViewModel viewModel, IThemeService themeService, UserPreferencesService? preferences = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _preferences = preferences;
        InitializeComponent();
        DataContext = viewModel;
        _lastTheme = themeService.CurrentTheme;
        _themePreparationTimeout.Tick += (_, _) => StopThemeTransition();
        SetResourceReference(ThemeRevisionProperty, ThemeService.ThemeRevisionKey);
        ThemeMist.Stopped += (_, _) => StopThemeTransition();
        Deactivated += (_, _) => StopThemeTransition();
        DpiChanged += (_, _) => StopThemeTransition();
        StateChanged += (_, _) => StopThemeTransition();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public bool CompletionSucceeded { get; private set; }
    internal bool IsEditorReady { get; private set; }
    internal TextMergeViewModel ViewModel => _viewModel;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        StopThemeTransition();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        if (_preferences is not null)
        {
            try
            {
                await _preferences.LoadAsync();
                _showBase = _preferences.MergeShowBase;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException)
            {
                _preferenceWarning = MergeStrings.PreferencesWarning;
            }
        }
        if (_closed) return;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(AppRuntimeProfile.DataRoot, "EditorRuntime"),
                new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update"));
            if (_closed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping("differ.editor.local", Path.Combine(AppContext.BaseDirectory, "Editor"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => args.Cancel = !args.Uri.StartsWith(Origin + "/", StringComparison.Ordinal);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!args.Request.Uri.StartsWith(Origin + "/", StringComparison.Ordinal)
                    && !args.Request.Uri.StartsWith("blob:" + Origin + "/", StringComparison.Ordinal))
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Offline editor", "");
            };
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += (_, _) => ShowFailure(MergeStrings.StartupFailed);
            core.Navigate(Origin + "/merge.html");
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or InvalidOperationException
            or IOException or COMException)
        {
            ShowFailure(MergeStrings.StartupFailed + Environment.NewLine + exception.GetType().Name);
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || !e.Source.StartsWith(Origin + "/", StringComparison.Ordinal)) return;
        long acknowledgement = 0;
        try
        {
            using var payload = JsonDocument.Parse(e.WebMessageAsJson);
            var message = payload.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "ready":
                    IsEditorReady = true;
                    Send(new
                    {
                        type = "load",
                        labels = MergeStrings.Labels,
                        mine = _viewModel.Session.MineText,
                        ancestor = _viewModel.Session.BaseText,
                        theirs = _viewModel.Session.TheirsText,
                        result = _viewModel.Session.ResultText,
                        mineName = _viewModel.MineName,
                        baseName = _viewModel.BaseName,
                        theirsName = _viewModel.TheirsName,
                        minePath = _viewModel.Session.MinePath,
                        basePath = _viewModel.Session.BasePath,
                        theirsPath = _viewModel.Session.TheirsPath,
                        outputPath = _viewModel.Session.OutputPath,
                        showBase = _showBase,
                        preferenceWarning = _preferenceWarning
                    });
                    SendTheme();
                    PublishState(true);
                    StartupStatus.Visibility = Visibility.Collapsed;
                    Browser.Visibility = Visibility.Visible;
                    break;
                case "themeToggle":
                    if (!_busy && !_closing && message.TryGetProperty("x", out var x) && x.TryGetDouble(out var originX)
                        && message.TryGetProperty("y", out var y) && y.TryGetDouble(out var originY))
                    {
                        ToggleTheme(originX, originY);
                    }
                    break;
                case "themeTransitionReady":
                    if (IsCurrentThemeTransition(message)) StartThemeClock();
                    break;
                case "themeTransitionFrame":
                    if (IsCurrentThemeTransition(message) && message.TryGetProperty("elapsed", out var elapsed)
                        && elapsed.TryGetDouble(out var milliseconds)) OnBrowserThemeFrame(milliseconds);
                    break;
                case "themeTransitionUnavailable":
                case "themeTransitionInvalidated":
                    if (IsCurrentThemeTransition(message)) StopThemeTransition();
                    break;
                case "change":
                    acknowledgement = message.GetProperty("sequence").GetInt64();
                    var accepted = !_busy && _viewModel.UpdateResult(message.GetProperty("text").GetString()!, message.GetProperty("version").GetInt64());
                    PublishState(false, acknowledgement, accepted ? null : MergeStrings.Stale);
                    break;
                case "showBase":
                    _pendingPreferenceSave = SaveShowBasePreferenceAsync(message.GetProperty("value").GetBoolean(),
                        message.GetProperty("sequence").GetInt64());
                    await _pendingPreferenceSave;
                    break;
                case "command":
                    if (_busy) { PublishState(false, error: MergeStrings.Pending); break; }
                    await ExecuteCommandAsync(message.GetProperty("command").GetString()!, message.GetProperty("version").GetInt64(),
                        message.TryGetProperty("index", out var index) ? index.GetInt32() : -1,
                        message.TryGetProperty("offset", out var offset) ? offset.GetInt32() : -1);
                    break;
                case "flushed":
                    if (message.GetProperty("id").GetString() == _flushId) _flush?.TrySetResult();
                    break;
                case "flushFailed":
                    if (message.GetProperty("id").GetString() == _flushId)
                        _flush?.TrySetException(new InvalidOperationException(MergeStrings.SyncFailed));
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException
            or COMException)
        {
            PublishState(false, acknowledgement, exception.Message);
        }
    }

    private async Task SaveShowBasePreferenceAsync(bool value, long sequence)
    {
        _showBase = value;
        _preferenceSequence = sequence;
        string? warning = null;
        try
        {
            if (_preferences is not null) await _preferences.SetMergeShowBaseAsync(value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or OperationCanceledException)
        {
            warning = MergeStrings.PreferencesWarning;
        }
        if (sequence != _preferenceSequence) return;
        _preferenceWarning = warning;
        Send(new { type = "preference", sequence, warning });
    }

    private async Task ExecuteCommandAsync(string command, long version, int index, int offset)
    {
        if (version != _viewModel.Session.Version) { PublishState(false, error: MergeStrings.Stale); return; }
        switch (command)
        {
            case "previous": _viewModel.Navigate(-1); PublishState(true); return;
            case "next": _viewModel.Navigate(1); PublishState(true); return;
            case "select": _viewModel.Select(index); PublishState(true); return;
            case "selectResult": PublishState(_viewModel.SelectResultOffset(offset), revealResult: false); return;
            case "toggleAdvance": _viewModel.AutoAdvance = !_viewModel.AutoAdvance; PublishState(false); return;
            case "save": await SaveResultAsync(false); return;

            case "finish": await SaveResultAsync(true); return;
        }
        if (command is "mine" or "base" or "theirs" or "confirm" && index != _viewModel.SelectedBlock)
        {
            PublishState(false, error: MergeStrings.Stale);
            return;
        }
        var accepted = _viewModel.Apply(command, version);
        PublishState(true, error: accepted ? null : MergeStrings.Stale);
    }

    internal async Task<bool> SaveResultAsync(bool finish, CancellationToken cancellationToken = default)
    {
        if (_busy || !IsEditorReady) return false;
        await FlushAsync(cancellationToken);
        _busy = true;
        try
        {
            if (_viewModel.Session.UnresolvedCount != 0)
            {
                PublishState(false, error: MergeStrings.UnresolvedSave);
                return false;
            }
            await _viewModel.Session.SaveAsync(_viewModel.Session.InitialOutputPath, cancellationToken);
            _viewModel.Refresh();
            PublishState(false);
            if (finish)
            {
                CompletionSucceeded = true;
                _allowClose = true;
                Close();
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Text.EncoderFallbackException)
        {
            PublishState(false, error: exception.Message);
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEditorReady || _closed) return;
        if (_flush is not null)
        {
            await _flush.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await _pendingPreferenceSave.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return;
        }
        _flush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _flushId = Guid.NewGuid().ToString("N");
        try
        {
            Send(new { type = "flush", id = _flushId });
            await _flush.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            // The browser's flush follows all earlier view-preference messages.
            // Keep the process alive for their bounded on-disk write as well.
            await _pendingPreferenceSave.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally { _flush?.TrySetCanceled(cancellationToken); _flush = null; _flushId = null; }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        StopThemeTransition();
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing || _busy) return;
        _closing = true;
        try
        {
            await FlushAsync();
            var session = _viewModel.Session;
            if (session.UnresolvedCount > 0)
            {
                if (MessageBox.Show(this, MergeStrings.UnresolvedClose, MergeStrings.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            }
            else if (session.IsDirty || !session.HasSaved)
            {
                var answer = MessageBox.Show(this, MergeStrings.ClosePrompt, MergeStrings.Title,
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes && !await SaveResultAsync(false)) return;
            }
            _allowClose = true;
            Close();
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or IOException
            or COMException or OperationCanceledException)
        {
            if (IsEditorReady) PublishState(false, error: MergeStrings.SyncFailed);
            else StartupStatus.Text = MergeStrings.SyncFailed;
        }
        finally
        {
            _closing = false;
            if (!_closed) Send(new { type = "resume" });
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        StopThemeTransition();
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _pendingThemeUpdate?.Abort();
        _pendingThemeUpdate = null;
        _flush?.TrySetCanceled();
        Browser.Dispose();
    }

    internal void CloseForTest()
    {
        _allowClose = true;
        Close();
    }

    private void PublishState(bool navigate, long acknowledgement = 0, string? error = null, bool revealResult = true)
    {
        var session = _viewModel.Session;
        Send(new
        {
            type = "state",
            version = session.Version,
            text = session.ResultText,
            blocks = session.Blocks.Select(block => new
            {
                index = block.Index,
                conflict = block.IsConflict,
                resolved = block.IsResolved,
                canChoose = block.CanChoose,
                choice = block.Choice.ToString(),
                baseStart = block.BaseStartLine,
                baseEnd = block.BaseEndLine,
                mineStart = block.MineStartLine,
                mineEnd = block.MineEndLine,
                theirsStart = block.TheirsStartLine,
                theirsEnd = block.TheirsEndLine,
                offset = block.ResultOffset,
                length = block.ResultLength
            }),
            selected = _viewModel.SelectedBlock,
            autoAdvance = _viewModel.AutoAdvance,
            unresolved = session.UnresolvedCount,
            status = _viewModel.Status,
            dirty = session.IsDirty || !session.HasSaved,
            canUndo = session.CanUndo,
            canRedo = session.CanRedo,
            encoding = session.EncodingName,
            outputPath = session.OutputPath,
            warnings = session.Warnings,
            navigate,
            revealResult,
            acknowledgement,
            error
        });
    }

    private void ToggleTheme(double x, double y)
    {
        StopThemeTransition(completePalette: false);
        var viewport = Browser.RenderSize;
        var area = MergeRoot.RenderSize;
        if (!IsEditorReady || !IsActive || !Browser.IsVisible || !SystemParameters.ClientAreaAnimation
            || SystemParameters.HighContrast || !double.IsFinite(x) || x is < 0 or > 1
            || !double.IsFinite(y) || y is < 0 or > 1 || viewport.Width <= 0 || viewport.Height <= 0
            || area.Width <= 0 || area.Height <= 0)
        {
            _themeService.Toggle();
            SendTheme();
            return;
        }

        var id = ++_themeTransitionId;
        var origin = Browser.TranslatePoint(new Point(x * viewport.Width, y * viewport.Height), MergeRoot);
        _themeTransition = new ThemeTransition(id, origin, area);
        _lastBrowserThemeElapsed = -1;
        _themeService.PrepareToggle();
        if (TrySendTheme(new { x, y, width = area.Width / viewport.Width, height = area.Height / viewport.Height, transitionId = id }))
        {
            _themePreparationTimeout.Interval = TimeSpan.FromMilliseconds(500);
            _themePreparationTimeout.Start();
        }
        else
        {
            StopThemeTransition();
        }
    }

    private bool IsCurrentThemeTransition(JsonElement message) =>
        _themeTransition is { } transition && message.TryGetProperty("id", out var id)
        && id.TryGetInt64(out var value) && value == transition.Id;

    private void StartThemeClock()
    {
        if (_themeTransition is not { } transition || _themeClockTimestamp is not null) return;
        _themePreparationTimeout.Stop();
        if (!ThemeMist.Start(transition.Origin, transition.Area, _lastTheme == AppTheme.Dark, 0, automaticClock: false))
        {
            StopThemeTransition();
            return;
        }

        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _themeClockTimestamp = Stopwatch.GetTimestamp();
        if (!TrySendThemeMessage(new { type = "themeTransitionStart", id = transition.Id, startedAt }))
        {
            StopThemeTransition();
            return;
        }

        CompositionTarget.Rendering += OnThemeRendering;
        _themePreparationTimeout.Interval = TimeSpan.FromMilliseconds(1100);
        _themePreparationTimeout.Start();
    }

    private void OnThemeRendering(object? sender, EventArgs e)
    {
        if (_themeClockTimestamp is not { } timestamp) return;
        var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        _themeService.RenderTransitionFrame(elapsed);
        ThemeMist.RenderFrame(elapsed);
    }

    private void OnBrowserThemeFrame(double elapsed)
    {
        if (_themeClockTimestamp is null || !double.IsFinite(elapsed) || elapsed < 0
            || elapsed > ThemeService.TransitionDurationMilliseconds || elapsed < _lastBrowserThemeElapsed) return;
        _lastBrowserThemeElapsed = elapsed;
        if (elapsed >= ThemeService.TransitionDurationMilliseconds) StopThemeTransition();
    }

    private void StopThemeTransition(bool completePalette = true)
    {
        if (_themeTransition is null) return;
        _themeTransition = null;
        _themeClockTimestamp = null;
        _themePreparationTimeout.Stop();
        CompositionTarget.Rendering -= OnThemeRendering;
        ThemeMist.Stop();
        if (completePalette) _themeService.CompleteTransition();
        TrySendThemeMessage(new { type = "themeMistStop", completePalette });
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))) return;
        if (Dispatcher.CheckAccess()) StopThemeTransition();
        else Dispatcher.InvokeAsync(() => StopThemeTransition());
    }

    private void QueueThemeUpdate()
    {
        if (_closed || !IsEditorReady || _pendingThemeUpdate is not null) return;
        // ThemeService publishes the new dictionary before updating CurrentTheme.
        _pendingThemeUpdate = Dispatcher.InvokeAsync(() =>
        {
            _pendingThemeUpdate = null;
            if (_lastTheme == _themeService.CurrentTheme) return;
            StopThemeTransition();
            SendTheme();
        }, DispatcherPriority.Render);
    }

    internal void SendTheme() => TrySendTheme();

    private bool TrySendTheme(object? mist = null)
    {
        _lastTheme = _themeService.CurrentTheme;
        var target = TryFindResource(ThemeService.TargetThemeKey) as ResourceDictionary;
        string Colour(string key) => (target?[key] ?? TryFindResource(key)) is SolidColorBrush brush
            ? EditorThemeAdapter.ToCssHex(brush) : "transparent";
        return TrySendThemeMessage(new
        {
            type = "theme",
            dark = _lastTheme == AppTheme.Dark,
            mist,
            palette = new
            {
                background = Colour("Sample.SurfaceBrush"),
                toolbar = Colour("Sample.ToolbarBrush"),
                header = Colour("Sample.PaneHeaderBrush"),
                ink = Colour("Sample.InkBrush"),
                muted = Colour("Sample.MutedInkBrush"),
                border = Colour("Sample.BorderBrush"),
                accent = Colour("Sample.AccentBrush"),
                hover = Colour("Sample.HoverBrush"),
                pressed = Colour("Sample.PressedBrush"),
                onAccent = Colour("Sample.OnAccentBrush"),
                conflict = Colour("Diff.Cell.Conflict"),
                conflictInk = Colour("Diff.Row.Conflict.Border"),
                resolved = Colour("Diff.Cell.Added"),
                resolvedInk = Colour("Diff.Row.Added.Border"),
                selection = Colour("Sample.SelectionBrush")
            }
        });
    }

    private bool TrySendThemeMessage(object message)
    {
        if (!IsEditorReady || _closed) return false;
        try
        {
            Send(message);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private void Send(object message)
    {
        if (IsEditorReady && !_closed) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void ShowFailure(string message)
    {
        IsEditorReady = false;
        StopThemeTransition();
        StartupStatus.Text = message;
        StartupStatus.Visibility = Visibility.Visible;
        _flush?.TrySetException(new InvalidOperationException(MergeStrings.SyncFailed));
    }

    private sealed record ThemeTransition(long Id, Point Origin, Size Area);
}
