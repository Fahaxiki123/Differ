using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Microsoft.Win32;

namespace Differ.App;

public partial class DiffWindow : Window
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmActivate = 0x0006;
    private const int WmCancelMode = 0x001F;
    private const int WmCaptureChanged = 0x0215;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonUp = 0x0202;
    private const int WmNcMouseLeave = 0x02A2;
    internal const int HtMinButton = 8;
    internal const int HtMaxButton = 9;
    internal const int HtClose = 20;
    private const uint TmeLeave = 0x00000002;
    private const uint TmeNonClient = 0x00000010;
    private const double CompactToolbarThreshold = 940;
    private static readonly DependencyPropertyKey IsCompactToolbarPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCompactToolbar), typeof(bool), typeof(DiffWindow), new PropertyMetadata(false));
    private static readonly DependencyProperty NativeCaptionThemeProperty = DependencyProperty.Register(
        "NativeCaptionTheme", typeof(string), typeof(DiffWindow),
        new PropertyMetadata(null, (window, _) => ((DiffWindow)window).QueueNativeCaptionTheme()));

    public static readonly DependencyProperty IsCompactToolbarProperty = IsCompactToolbarPropertyKey.DependencyProperty;
    public static readonly DependencyProperty IsCaptionHoveredProperty = DependencyProperty.RegisterAttached(
        "IsCaptionHovered", typeof(bool), typeof(DiffWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCaptionPressedProperty = DependencyProperty.RegisterAttached(
        "IsCaptionPressed", typeof(bool), typeof(DiffWindow), new PropertyMetadata(false));
    private static readonly CompositeFormat ExportFailedMessageFormat = CompositeFormat.Parse(Strings.ExportFailedFormat);
    private static readonly CompositeFormat FileOverflowFormat = CompositeFormat.Parse(ChromeStrings.FileOverflowFormat);
    private readonly ComparisonWorkspaceViewModel _workspace;
    private readonly IThemeService _themeService;
    private readonly bool _persistWindowLayout;
    private bool _closeAfterSave;
    private bool _savingWindowLayout;
    private bool _nativeCaptionThemeQueued;
    private HwndSource? _hwndSource;
    private bool _trackingNonClientMouse;
    private int _hoveredCaptionHit;
    private int _pressedCaptionHit;
    private CodeEditorView? _mistEditor;
    private AppTheme _mistTheme;
    private long _themeTransitionId;
    private Point _themeOrigin;
    private Size _themeArea;
    private double _lastThemeElapsed;
    private double _lastBrowserThemeElapsed;
    private long _themeClockTimestamp;
    private double _themeClockInitialElapsed;
    private bool _themeClockRunning;
    private readonly DispatcherTimer _themePreparationTimeout;
    private ScrollViewer? _fileTabScroll;
    private int _hiddenFileTabCount;

    public DiffWindow(
        ComparisonWorkspaceViewModel workspace,
        IThemeService themeService,
        bool persistWindowLayout = false)
    {
        _workspace = workspace;
        _themeService = themeService;
        _persistWindowLayout = persistWindowLayout;
        InitializeComponent();
        _themePreparationTimeout = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _themePreparationTimeout.Tick += (_, _) => StopThemeMist();
        SetResourceReference(NativeCaptionThemeProperty, "Theme.Name");
        DataContext = workspace;
        _workspace.AllTabsClosed += OnAllTabsClosed;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        AddHandler(CodeEditorView.ThemeToggleRequestedEvent, new RoutedEventHandler(OnEditorThemeToggleRequested));
        ThemeMist.Stopped += (_, _) => StopThemeMist();
        Deactivated += (_, _) =>
        {
            StopThemeMist();
            CancelCaptionGesture();
        };
        DpiChanged += (_, _) =>
        {
            StopThemeMist();
            CancelCaptionGesture();
        };
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        Closing += OnClosing;
        Closed += OnClosed;
        if (_persistWindowLayout)
        {
            RestoreWindowLayout();
        }
    }

    public bool IsCompactToolbar => (bool)GetValue(IsCompactToolbarProperty);

    public static bool GetIsCaptionHovered(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsCaptionHoveredProperty);
    }

    public static void SetIsCaptionHovered(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsCaptionHoveredProperty, value);
    }

    public static bool GetIsCaptionPressed(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsCaptionPressedProperty);
    }

    public static void SetIsCaptionPressed(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsCaptionPressedProperty, value);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        StopThemeMist();
        CancelCaptionGesture();
        SetValue(IsCompactToolbarPropertyKey, sizeInfo.NewSize.Width < CompactToolbarThreshold);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        StopThemeMist();
        CancelCaptionGesture();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);
        }

        ApplyNativeCaptionTheme();
    }

    private void QueueNativeCaptionTheme()
    {
        if (_mistEditor is not null)
        {
            if (!Equals(GetValue(NativeCaptionThemeProperty), _mistTheme.ToString()))
            {
                StopThemeMist();
            }

            return;
        }

        if (!IsLoaded || _nativeCaptionThemeQueued) return;
        _nativeCaptionThemeQueued = true;
        CompositionTarget.Rendering += OnNativeCaptionThemeRendering;
    }

    private void OnNativeCaptionThemeRendering(object? sender, EventArgs e)
    {
        CancelNativeCaptionTheme();
        ApplyNativeCaptionTheme();
    }

    private void CancelNativeCaptionTheme()
    {
        if (!_nativeCaptionThemeQueued) return;
        _nativeCaptionThemeQueued = false;
        CompositionTarget.Rendering -= OnNativeCaptionThemeRendering;
    }

    private void ApplyNativeCaptionTheme()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        var dark = Equals(GetValue(NativeCaptionThemeProperty), nameof(AppTheme.Dark)) ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, in dark, sizeof(int));
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => ExecuteCaptionCommand(HtMinButton);

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e) => ExecuteCaptionCommand(HtMaxButton);

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => ExecuteCaptionCommand(HtClose);

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                var hit = HitTestCaptionButtons(lParam);
                if (hit != 0)
                {
                    handled = true;
                    return hit;
                }

                break;

            case WmNcMouseMove:
                var moveHit = NormalizeCaptionHit(wParam);
                if (moveHit != 0)
                {
                    EnsureNonClientMouseTracking(hwnd);
                }

                if (_pressedCaptionHit != 0)
                {
                    UpdateCapturedCaptionGesture(hwnd, HitTestCaptionButtons(lParam));
                    handled = true;
                    return 0;
                }

                SetCaptionInteractionState(moveHit, 0);
                break;

            case WmNcMouseLeave:
                _trackingNonClientMouse = false;
                // Leaving only disarms the visual. The original press survives until release or cancellation.
                SetCaptionInteractionState(0, _pressedCaptionHit);
                break;

            case WmMouseMove:
                if (_pressedCaptionHit != 0)
                {
                    UpdateCapturedCaptionGesture(hwnd, HitTestClientCaptionButtons(hwnd, lParam));
                    handled = true;
                    return 0;
                }

                if (_hoveredCaptionHit != 0) SetCaptionInteractionState(0, 0);
                break;

            case WmLButtonUp:
                if (_pressedCaptionHit != 0)
                {
                    CompleteCaptionGesture(hwnd, HitTestClientCaptionButtons(hwnd, lParam));
                    handled = true;
                    return 0;
                }

                break;

            case WmNcLButtonDown:
            case WmNcLButtonDblClk:
                var downHit = NormalizeCaptionHit(wParam);
                if (downHit != 0)
                {
                    EnsureNonClientMouseTracking(hwnd);
                    BeginCaptionGesture(hwnd, downHit);
                    handled = true;
                    return 0;
                }

                break;

            case WmNcLButtonUp:
                if (_pressedCaptionHit != 0)
                {
                    CompleteCaptionGesture(hwnd, HitTestCaptionButtons(lParam));
                    handled = true;
                    return 0;
                }

                break;

            case WmCaptureChanged:
                // The new owner may be another control or window; never release its capture here.
                CancelCaptionGesture(releaseCapture: false);
                break;

            case WmCancelMode:
                CancelCaptionGesture();
                break;

            case WmActivate:
                if ((wParam.ToInt64() & 0xFFFF) == 0) CancelCaptionGesture();
                break;
        }

        return 0;
    }

    private int HitTestCaptionButtons(nint lParam)
    {
        var screenX = (short)(lParam.ToInt64() & 0xFFFF);
        var screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        return HitTestCaptionButtons(new Point(screenX, screenY));
    }

    private int HitTestClientCaptionButtons(nint hwnd, nint lParam)
    {
        var point = new NativePoint
        {
            X = (short)(lParam.ToInt64() & 0xFFFF),
            Y = (short)((lParam.ToInt64() >> 16) & 0xFFFF)
        };
        return ClientToScreen(hwnd, ref point) ? HitTestCaptionButtons(new Point(point.X, point.Y)) : 0;
    }

    private int HitTestCaptionButtons(Point screenPoint)
    {
        if (PresentationSource.FromVisual(ComparisonRoot) is null || ComparisonRoot.ActualWidth <= 0)
        {
            return 0;
        }

        var rootPoint = ComparisonRoot.PointFromScreen(screenPoint);
        if (rootPoint.X < 0 || rootPoint.Y < 0 ||
            rootPoint.X >= ComparisonRoot.ActualWidth || rootPoint.Y >= FileTabBar.ActualHeight)
        {
            return 0;
        }

        if (WindowState == WindowState.Normal &&
            ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            const double resizeGrip = 6;
            if (rootPoint.Y < resizeGrip || rootPoint.X >= ComparisonRoot.ActualWidth - resizeGrip)
            {
                return 0;
            }
        }

        if (IsPointInButton(MinimizeCaptionButton, screenPoint)) return HtMinButton;
        if (IsPointInButton(MaximizeRestoreCaptionButton, screenPoint)) return HtMaxButton;
        if (IsPointInButton(CloseCaptionButton, screenPoint)) return HtClose;
        return 0;
    }

    private void BeginCaptionGesture(nint hwnd, int hitCode)
    {
        CancelCaptionGesture();
        SetCaptionInteractionState(hitCode, hitCode);
        _ = SetCapture(hwnd);
        if (GetCapture() != hwnd) CancelCaptionGesture(releaseCapture: false);
    }

    private void UpdateCapturedCaptionGesture(nint hwnd, int hitCode)
    {
        if (GetCapture() != hwnd)
        {
            CancelCaptionGesture(releaseCapture: false);
            return;
        }

        SetCaptionInteractionState(hitCode, _pressedCaptionHit);
    }

    private void CompleteCaptionGesture(nint hwnd, int releaseHit)
    {
        var command = GetCapture() == hwnd && releaseHit == _pressedCaptionHit ? releaseHit : 0;
        // Clear before releasing: WM_CAPTURECHANGED can run synchronously and commands can close this window.
        CancelCaptionGesture();
        SetCaptionInteractionState(releaseHit, 0);
        if (releaseHit != 0) EnsureNonClientMouseTracking(hwnd);
        if (command != 0) ExecuteCaptionCommand(command);
    }

    private void CancelCaptionGesture(bool releaseCapture = true)
    {
        var hadPress = _pressedCaptionHit != 0;
        SetCaptionInteractionState(0, 0);
        if (releaseCapture && hadPress && _hwndSource is { Handle: not 0 } source && GetCapture() == source.Handle)
        {
            _ = ReleaseCapture();
        }
    }

    private static bool IsPointInButton(Button button, Point screenPoint)
    {
        if (!button.IsVisible || !button.IsEnabled || button.ActualWidth <= 0 || button.ActualHeight <= 0)
        {
            return false;
        }

        var local = button.PointFromScreen(screenPoint);
        return local.X >= 0 && local.X < button.ActualWidth &&
               local.Y >= 0 && local.Y < button.ActualHeight;
    }

    private static int NormalizeCaptionHit(nint wParam)
    {
        var code = (int)(wParam.ToInt64() & 0xFFFF);
        return code is HtMinButton or HtMaxButton or HtClose ? code : 0;
    }

    private void SetCaptionInteractionState(int hoveredHit, int pressedHit)
    {
        if (_hoveredCaptionHit == hoveredHit && _pressedCaptionHit == pressedHit)
        {
            return;
        }

        _hoveredCaptionHit = hoveredHit;
        _pressedCaptionHit = pressedHit;
        UpdateButtonCaptionState(MinimizeCaptionButton, HtMinButton);
        UpdateButtonCaptionState(MaximizeRestoreCaptionButton, HtMaxButton);
        UpdateButtonCaptionState(CloseCaptionButton, HtClose);
    }

    private void UpdateButtonCaptionState(Button button, int hitCode)
    {
        var hovered = _hoveredCaptionHit == hitCode && (_pressedCaptionHit == 0 || _pressedCaptionHit == hitCode);
        var pressed = _pressedCaptionHit == hitCode && _hoveredCaptionHit == hitCode;
        SetIsCaptionHovered(button, hovered);
        SetIsCaptionPressed(button, pressed);
    }

    private void ExecuteCaptionCommand(int hitCode)
    {
        switch (hitCode)
        {
            case HtMinButton:
                if (ResizeMode != ResizeMode.NoResize)
                {
                    SystemCommands.MinimizeWindow(this);
                }

                break;

            case HtMaxButton:
                if (ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
                {
                    if (WindowState == WindowState.Maximized)
                    {
                        SystemCommands.RestoreWindow(this);
                    }
                    else
                    {
                        SystemCommands.MaximizeWindow(this);
                    }
                }

                break;

            case HtClose:
                SystemCommands.CloseWindow(this);
                break;
        }
    }

    private void EnsureNonClientMouseTracking(nint hwnd)
    {
        if (_trackingNonClientMouse || hwnd == 0)
        {
            return;
        }

        var options = new TrackMouseEventOptions
        {
            CbSize = (uint)Marshal.SizeOf<TrackMouseEventOptions>(),
            DwFlags = TmeLeave | TmeNonClient,
            HwndTrack = hwnd,
            DwHoverTime = 0
        };
        if (TrackMouseEvent(ref options))
        {
            _trackingNonClientMouse = true;
        }
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventOptions
    {
        public uint CbSize;
        public uint DwFlags;
        public nint HwndTrack;
        public uint DwHoverTime;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventOptions eventTrack);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetCapture();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    private void ComparisonCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border canvas) return;
        // Native WebView2 stays between the WPF header and footer, outside the curved corners.
        var radius = canvas.CornerRadius.TopLeft;
        var clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        clip.Freeze();
        canvas.Clip = clip;
    }

    private void DiffGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is DataGrid
            {
                DataContext: DiffWindowViewModel viewModel
            } grid
            && viewModel.SelectedDiffRow is not null)
        {
            grid.ScrollIntoView(viewModel.SelectedDiffRow);
        }
    }

    private void ComparisonOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OnAllTabsClosed(object? sender, EventArgs e)
    {
        Close();
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pressedCaptionHit != 0 && e.Key == Key.Escape)
        {
            CancelCaptionGesture();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _workspace.SelectedTab is { } active
            && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            if (active.IsFindOpen && IsInsidePanel(e.OriginalSource as DependencyObject, "FindPanel"))
            {
                ExecuteSelectedCommand(Keyboard.Modifiers == ModifierKeys.Shift
                    ? active.FindPreviousCommand : active.FindNextCommand);
                e.Handled = true;
                return;
            }

            if (active.IsGoToOpen && IsInsidePanel(e.OriginalSource as DependencyObject, "GoToPanel"))
            {
                ExecuteSelectedCommand(active.GoToCommand);
                e.Handled = true;
                return;
            }
        }

        var shortcut = ComparisonShortcutResolver.Resolve(
            e.Key,
            e.SystemKey,
            Keyboard.Modifiers);
        switch (shortcut)
        {
            case ComparisonShortcut.Undo:
            case ComparisonShortcut.Redo:
                if (_workspace.SelectedTab is { IsEditableText: true } editable
                    && !IsTextInput(e.OriginalSource as DependencyObject)
                    && FindVisiblePaneTarget(ComparisonContentHost) is CodeEditorView activeEditor
                    && !activeEditor.HasBrowserKeyboardFocus)
                {
                    e.Handled = true;
                    ExecuteSelectedCommand(shortcut == ComparisonShortcut.Undo
                        ? editable.UndoEditorCommand : editable.RedoEditorCommand);
                }
                return;
            case ComparisonShortcut.Save:
            case ComparisonShortcut.SaveAs:
                if (_workspace.SelectedTab?.IsEditableText == true
                    && FindVisiblePaneTarget(ComparisonContentHost) is CodeEditorView editor
                    && !editor.HasBrowserKeyboardFocus)
                {
                    e.Handled = true;
                    await editor.SaveFromShellAsync(shortcut == ComparisonShortcut.SaveAs);
                }
                // Native WebView focus retains its own Monaco commands; handling here as well would save twice.
                return;
            case ComparisonShortcut.PreviousDifference:
                e.Handled = true;
                ExecuteSelectedCommand(
                    _workspace.SelectedTab?.PreviousDifferenceCommand);
                return;
            case ComparisonShortcut.NextDifference:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.SelectedTab?.NextDifferenceCommand);
                return;
            case ComparisonShortcut.ToggleTheme:
                if (FindVisiblePaneTarget(ComparisonContentHost) is CodeEditorView
                    { IsEditorReady: true, HasBrowserKeyboardFocus: true }) return;
                e.Handled = true;
                if (!e.IsRepeat) ToggleComparisonTheme();
                return;
            case ComparisonShortcut.ToggleUnchanged:
                if (_workspace.SelectedTab is { } selectedTab)
                {
                    selectedTab.ShowUnchanged = !selectedTab.ShowUnchanged;
                }

                e.Handled = true;
                return;
            case ComparisonShortcut.FocusBase:
                e.Handled = FocusComparisonPane(useMineSide: false);
                return;
            case ComparisonShortcut.FocusMine:
                e.Handled = FocusComparisonPane(useMineSide: true);
                return;
            case ComparisonShortcut.CloseTab:
                e.Handled = true;
                _workspace.CloseSelectedTab();
                return;
            case ComparisonShortcut.CloseWindow:
                e.Handled = true;
                if (!CloseLocalPanel())
                {
                    Close();
                }

                return;
            case ComparisonShortcut.Find:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.SelectedTab?.OpenFindCommand);
                FocusLocalInput("FindInput");
                return;
            case ComparisonShortcut.GoTo:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.SelectedTab?.OpenGoToCommand);
                FocusLocalInput("GoToInput");
                return;
            case ComparisonShortcut.Refresh:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.RefreshSelectedCommand);
                return;
            case ComparisonShortcut.NextTab:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.NextTabCommand);
                return;
            case ComparisonShortcut.PreviousTab:
                e.Handled = true;
                ExecuteSelectedCommand(_workspace.PreviousTabCommand);
                return;
        }
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e) => ToggleComparisonTheme();

    private void OnEditorThemeToggleRequested(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not CodeEditorView editor ||
            !ReferenceEquals(FindVisiblePaneTarget(ComparisonContentHost), editor)) return;
        e.Handled = true;
        ToggleComparisonTheme();
    }

    private void ToggleComparisonTheme()
    {
        StopThemeMist(false);
        var nextTheme = _themeService.CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || !IsActive ||
            FindVisiblePaneTarget(ComparisonContentHost) is not CodeEditorView editor)
        {
            _themeService.Toggle();
            return;
        }

        var origin = ThemeToggleButton.TranslatePoint(
            new Point(ThemeToggleButton.ActualWidth / 2, ThemeToggleButton.ActualHeight / 2), ComparisonRoot);
        var area = ComparisonRoot.RenderSize;
        var dark = nextTheme == AppTheme.Dark;
        var id = ++_themeTransitionId;
        if (editor.PrepareThemeMist(ComparisonRoot, origin, area, dark, 0, id))
        {
            _mistEditor = editor;
            _mistTheme = nextTheme;
            _themeOrigin = origin;
            _themeArea = area;
            editor.ThemeMistInvalidated += OnThemeMistInvalidated;
            editor.ThemeTransitionReady += OnThemeTransitionReady;
            editor.ThemeTransitionFrame += OnEditorThemeFrame;
            _lastThemeElapsed = -1;
            _lastBrowserThemeElapsed = -1;
            _themePreparationTimeout.Interval = TimeSpan.FromMilliseconds(500);
            _themePreparationTimeout.Start();
        }

        if (_mistEditor is not null) _themeService.PrepareToggle();
        else _themeService.Toggle();
    }

    private void OnThemeTransitionReady(long id)
    {
        if (id != _themeTransitionId || _mistEditor is not { } editor || _themeClockRunning) return;
        editor.ThemeTransitionReady -= OnThemeTransitionReady;
        _themePreparationTimeout.Stop();
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _themeClockTimestamp = Stopwatch.GetTimestamp();
        _themeClockInitialElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt;
        if (!ThemeMist.Start(_themeOrigin, _themeArea, _mistTheme == AppTheme.Dark,
            _themeClockInitialElapsed, automaticClock: false))
        {
            StopThemeMist();
            return;
        }
        _themeClockRunning = true;
        CompositionTarget.Rendering += OnThemeClockRendering;
        RenderThemeClockFrame(_themeClockInitialElapsed);
        _themePreparationTimeout.Interval = TimeSpan.FromMilliseconds(1100);
        _themePreparationTimeout.Start();
        editor.StartPreparedTheme(id, startedAt);
    }

    private void OnThemeClockRendering(object? sender, EventArgs e) =>
        RenderThemeClockFrame(_themeClockInitialElapsed + Stopwatch.GetElapsedTime(_themeClockTimestamp).TotalMilliseconds);

    private void RenderThemeClockFrame(double elapsed)
    {
        if (!_themeClockRunning || _mistEditor is null || !double.IsFinite(elapsed)) return;
        elapsed = Math.Clamp(elapsed, 0, ThemeService.TransitionDurationMilliseconds);
        if (elapsed < _lastThemeElapsed) return;
        _lastThemeElapsed = elapsed;
        // Both WPF layers use this render pass's elapsed time, rather than a browser frame received later over IPC.
        _themeService.RenderTransitionFrame(elapsed);
        ThemeMist.RenderFrame(elapsed);
        if (elapsed >= ThemeService.TransitionDurationMilliseconds) StopThemeMist();
    }

    private void OnEditorThemeFrame(long id, double elapsed)
    {
        if (id != _themeTransitionId || _mistEditor is null || !_themeClockRunning || !double.IsFinite(elapsed)
            || elapsed < 0 || elapsed > ThemeService.TransitionDurationMilliseconds || elapsed < _lastBrowserThemeElapsed) return;
        _lastBrowserThemeElapsed = elapsed;
        if (elapsed >= ThemeService.TransitionDurationMilliseconds) StopThemeMist();
    }

    private void StopThemeClock()
    {
        if (!_themeClockRunning) return;
        _themeClockRunning = false;
        CompositionTarget.Rendering -= OnThemeClockRendering;
    }

    private void StopThemeMist() => StopThemeMist(true);

    private void StopThemeMist(bool completePalette)
    {
        StopThemeClock();
        if (_mistEditor is not { } editor) return;
        _mistEditor = null;
        _themePreparationTimeout.Stop();
        editor.ThemeMistInvalidated -= OnThemeMistInvalidated;
        editor.ThemeTransitionReady -= OnThemeTransitionReady;
        editor.ThemeTransitionFrame -= OnEditorThemeFrame;
        ThemeMist.Stop();
        if (completePalette)
        {
            _lastThemeElapsed = ThemeService.TransitionDurationMilliseconds;
            _themeService.CompleteTransition();
        }
        editor.StopThemeMist(completePalette);
        QueueNativeCaptionTheme();
    }

    private void OnThemeMistInvalidated(object? sender, EventArgs e) => StopThemeMist();

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComparisonWorkspaceViewModel.SelectedTab)) StopThemeMist();
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))) return;
        if (Dispatcher.CheckAccess()) StopThemeMist();
        else Dispatcher.InvokeAsync(StopThemeMist);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            ChangeZoom(e.Delta > 0 ? 0.1 : -0.1);
            e.Handled = true;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(0.1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-0.1);

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => _workspace.Preferences.ReadingScale = 1;

    private void ChangeZoom(double change) => _workspace.Preferences.ReadingScale =
        Math.Round(_workspace.Preferences.ReadingScale + change, 2);

    private void FilterTabs_Click(object sender, RoutedEventArgs e)
    {
        if (TabFilterPopup.IsOpen)
        {
            CloseTabPicker();
            return;
        }

        // The button moves as the filtered tab strip shrinks. Anchor the open picker to the title bar.
        var anchor = FilterTabsButton.TranslatePoint(new Point(), FileTabBar);
        TabFilterPopup.HorizontalOffset = Math.Clamp(anchor.X, 0, Math.Max(0, FileTabBar.ActualWidth - TabPickerSurface.Width));
        TabFilterPopup.VerticalOffset = FileTabBar.ActualHeight;
        TabPicker.SetCurrentValue(Selector.SelectedItemProperty, _workspace.SelectedTab);
        TabFilterPopup.IsOpen = true;
    }

    private void FileTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && sender is ListBox { SelectedItem: { } selected } tabs)
        {
            tabs.ScrollIntoView(selected);
        }
    }

    private void FileTabs_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scroll) return;

        _fileTabScroll = scroll;
        if ((e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0) && FileTabs.SelectedItem is { } selected)
        {
            FileTabs.ScrollIntoView(selected);
        }

        var hiddenCount = 0;
        var firstVisible = e.ViewportWidth;
        var lastVisible = 0d;
        if (scroll.ScrollableWidth > 0)
        {
            for (var index = 0; index < FileTabs.Items.Count; index++)
            {
                if (FileTabs.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) continue;

                var left = LayoutInformation.GetLayoutSlot(container).Left - e.HorizontalOffset;
                var right = left + container.ActualWidth;
                if (left < -0.5 || right > e.ViewportWidth + 0.5)
                {
                    hiddenCount++;
                }
                else
                {
                    firstVisible = Math.Min(firstVisible, Math.Max(0, left));
                    lastVisible = Math.Max(lastVisible, Math.Min(e.ViewportWidth, right));
                }
            }
        }

        // Clip incomplete tabs, including their close buttons, and keep the picker beside the whole tabs.
        var trim = hiddenCount > 0 && lastVisible > firstVisible;
        FileTabs.Clip = trim
            ? new RectangleGeometry(new Rect(firstVisible, 0, lastVisible - firstVisible, FileTabs.ActualHeight))
            : null;
        FileTabsOffset.X = trim ? -firstVisible : 0;
        FilePickerOffset.X = trim ? lastVisible - firstVisible - e.ViewportWidth : 0;

        if (_hiddenFileTabCount == hiddenCount) return;
        _hiddenFileTabCount = hiddenCount;
        var hasOverflow = hiddenCount > 0;
        var overflowText = hasOverflow ? string.Format(CultureInfo.CurrentCulture, FileOverflowFormat, hiddenCount) : string.Empty;
        OverflowLabel.Text = overflowText;
        OverflowLabel.Visibility = OverflowChevron.Visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
        TabFilterIcon.Visibility = hasOverflow ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(FilterTabsButton, hasOverflow ? overflowText : ChromeStrings.FilePicker);
    }

    private void FileTabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || _fileTabScroll is not { ScrollableWidth: > 0 } scroll) return;
        var moveLeft = e.Delta > 0;
        var target = moveLeft ? 0 : scroll.ScrollableWidth;
        for (var index = 0; index < FileTabs.Items.Count; index++)
        {
            if (FileTabs.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) continue;
            var start = LayoutInformation.GetLayoutSlot(container).Left;
            if (moveLeft && start < scroll.HorizontalOffset - 0.5)
            {
                target = start;
            }
            else if (!moveLeft && start > scroll.HorizontalOffset + 0.5)
            {
                target = start;
                break;
            }
        }

        scroll.ScrollToHorizontalOffset(target);
        e.Handled = true;
    }

    private void TabPicker_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(TabPicker, e.OriginalSource as DependencyObject)
            is ListBoxItem { DataContext: DiffWindowViewModel tab })
        {
            ActivatePickedTab(tab);
            e.Handled = true;
        }
    }

    private void TabPicker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TabPicker.SelectedItem is DiffWindowViewModel tab)
        {
            ActivatePickedTab(tab);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseTabPicker();
            e.Handled = true;
        }
    }

    private void ActivatePickedTab(DiffWindowViewModel tab)
    {
        _workspace.SelectedTab = tab;
        FileTabs.ScrollIntoView(tab);
        CloseTabPicker();
    }

    private void FilterInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter)
        {
            CloseTabPicker();
            e.Handled = true;
        }
    }

    private void CloseTabPicker()
    {
        TabFilterPopup.IsOpen = false;
        FilterTabsButton.Focus();
    }

    private void LocalInput_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } input)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (input.IsVisible)
                {
                    input.Focus();
                    input.SelectAll();
                }
            }));
        }
    }

    private void FocusLocalInput(string name)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var input = FindVisibleInput(ComparisonContentHost, name);
            input?.Focus();
            input?.SelectAll();
        }));
    }

    private static TextBox? FindVisibleInput(DependencyObject parent, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox { IsVisible: true } input && input.Name == name)
            {
                return input;
            }

            var nested = FindVisibleInput(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private bool CloseLocalPanel()
    {
        if (TabFilterPopup.IsOpen)
        {
            CloseTabPicker();
            return true;
        }

        var active = _workspace.SelectedTab;
        if (active?.IsFindOpen == true)
        {
            ExecuteSelectedCommand(active.CloseFindCommand);
            return true;
        }

        if (active?.IsGoToOpen == true)
        {
            ExecuteSelectedCommand(active.CloseGoToCommand);
            return true;
        }

        return false;
    }

    private static bool IsInsidePanel(DependencyObject? element, string name)
    {
        while (element is not null)
        {
            if (element is FrameworkElement frameworkElement && frameworkElement.Name == name)
            {
                return true;
            }

            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private static bool IsTextInput(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)
            {
                return true;
            }

            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private async void ExportHtml_Click(object sender, RoutedEventArgs e) => await ExportAsync(html: true);

    private async void ExportSelectedText_Click(object sender, RoutedEventArgs e) => await ExportAsync(html: false);

    private async Task ExportAsync(bool html)
    {
        var tab = _workspace.SelectedTab;
        if (tab?.CanExport != true)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = html ? Strings.ExportHtml : Strings.ExportSelectedText,
            Filter = html ? Strings.ExportHtmlFilter : Strings.ExportTextFilter,
            DefaultExt = html ? ".html" : ".txt",
            FileName = Strings.ExportReportTitle + (html ? ".html" : ".txt"),
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (html)
            {
                await tab.ExportHtmlAsync(dialog.FileName, CancellationToken.None);
            }
            else
            {
                await tab.ExportSelectedTextAsync(dialog.FileName, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(this, string.Format(CultureInfo.CurrentCulture, ExportFailedMessageFormat, exception.Message),
                Strings.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestoreWindowLayout()
    {
        var preferences = _workspace.Preferences;
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var desired = new Rect(double.IsFinite(preferences.WindowLeft) ? preferences.WindowLeft : SystemParameters.WorkArea.Left + 30,
            double.IsFinite(preferences.WindowTop) ? preferences.WindowTop : SystemParameters.WorkArea.Top + 30,
            preferences.WindowWidth, preferences.WindowHeight);
        var bounds = ClampWindowBounds(desired, desktop, new Size(MinWidth, MinHeight));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (preferences.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    internal static Rect ClampWindowBounds(Rect requested, Rect desktop, Size minimum)
    {
        var width = Math.Min(desktop.Width, Math.Max(minimum.Width, requested.Width));
        var height = Math.Min(desktop.Height, Math.Max(minimum.Height, requested.Height));
        var left = double.IsFinite(requested.Left) ? requested.Left : desktop.Left;
        var top = double.IsFinite(requested.Top) ? requested.Top : desktop.Top;
        return new Rect(Math.Clamp(left, desktop.Left, desktop.Right - width),
            Math.Clamp(top, desktop.Top, desktop.Bottom - height), width, height);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        CancelCaptionGesture();
        if (!_persistWindowLayout && _workspace.Tabs.All(tab => !tab.IsEditableText)) return;
        if (_closeAfterSave)
        {
            return;
        }

        e.Cancel = true;
        if (_savingWindowLayout)
        {
            return;
        }

        _savingWindowLayout = true;
        if (!await _workspace.ConfirmCloseAllAsync())
        {
            _savingWindowLayout = false;
            return;
        }
        if (!_persistWindowLayout)
        {
            _closeAfterSave = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
            return;
        }
        var preferences = _workspace.Preferences;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            preferences.WindowLeft = bounds.Left;
            preferences.WindowTop = bounds.Top;
            preferences.WindowWidth = bounds.Width;
            preferences.WindowHeight = bounds.Height;
        }

        preferences.IsMaximized = WindowState == WindowState.Maximized;
        try
        {
            await preferences.SaveAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning(Strings.PreferencesSaveFailed);
        }
        finally
        {
            _closeAfterSave = true;
            // Even a cached/synchronous save must leave the first Closing
            // callback before asking WPF to close the window again.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
    }

    private bool FocusComparisonPane(bool useMineSide)
    {
        var target = FindVisiblePaneTarget(ComparisonContentHost);
        if (target is null)
        {
            return false;
        }

        if (useMineSide)
        {
            target.FocusMinePane();
        }
        else
        {
            target.FocusBasePane();
        }

        return true;
    }

    private static void ExecuteSelectedCommand(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private static IComparisonPaneFocusTarget? FindVisiblePaneTarget(
        DependencyObject parent)
    {
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement { IsVisible: true }
                && child is IComparisonPaneFocusTarget target)
            {
                return target;
            }

            var nested = FindVisiblePaneTarget(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CancelCaptionGesture();
        StopThemeMist();
        CancelNativeCaptionTheme();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _workspace.AllTabsClosed -= OnAllTabsClosed;
        _workspace.Dispose();
    }
}
