using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Differ.App.Services;

public enum AppTheme
{
    Light,
    Dark
}

public interface IThemeService : IDisposable
{
    AppTheme CurrentTheme { get; }

    void InitializeFromSystem();

    void Toggle();

    void Apply(AppTheme theme);

    void ToggleAnimated(long startedAt);

    void PrepareToggle();

    void StartTransition(long startedAt);

    void RenderTransitionFrame(double elapsedMilliseconds);

    void CompleteTransition();

    event EventHandler? TransitionFrame;
}

public interface ISystemThemeMonitor
{
    AppTheme CurrentTheme { get; }

    event EventHandler? ThemeChanged;
}

public sealed class ThemeService : IThemeService
{
    private const string ThemeNameKey = "Theme.Name";
    internal const string ThemeRevisionKey = "Theme.Revision";
    internal static readonly object TargetThemeKey = new();
    internal const double TransitionDurationMilliseconds = 680;

    private readonly ResourceDictionary _applicationResources;
    private readonly Dispatcher _dispatcher;
    private readonly ISystemThemeMonitor _systemThemeMonitor;
    private readonly Dictionary<AppTheme, ResourceDictionary> _themeDictionaries = [];
    private DispatcherOperation? _preload;
    private bool _hasAppliedTheme;
    private bool _isDisposed;
    private readonly List<(SolidColorBrush Brush, Color From, Color To)> _colorTransitions = [];
    private long _transitionClock;
    private double _initialTransitionElapsed;
    private bool _transitionRunning;

    public event EventHandler? TransitionFrame;

    public ThemeService(
        ResourceDictionary applicationResources,
        Dispatcher dispatcher,
        ISystemThemeMonitor systemThemeMonitor)
    {
        _applicationResources = applicationResources
            ?? throw new ArgumentNullException(nameof(applicationResources));
        _dispatcher = dispatcher
            ?? throw new ArgumentNullException(nameof(dispatcher));
        _systemThemeMonitor = systemThemeMonitor
            ?? throw new ArgumentNullException(nameof(systemThemeMonitor));
        _systemThemeMonitor.ThemeChanged += OnSystemThemeChanged;
    }

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Light;

    public void InitializeFromSystem()
    {
        Apply(_systemThemeMonitor.CurrentTheme);
        var alternate = CurrentTheme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
        if (_preload is null && !_themeDictionaries.ContainsKey(alternate))
        {
            _preload = _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                _preload = null;
                GetThemeDictionary(alternate);
            });
        }
    }

    public void Toggle()
    {
        Apply(CurrentTheme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
    }

    public void Apply(AppTheme theme)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _dispatcher.VerifyAccess();
        if (_hasAppliedTheme && CurrentTheme == theme && _colorTransitions.Count == 0) return;

        StopTransition();

        var replacement = GetThemeDictionary(theme);
        var dictionaries = _applicationResources.MergedDictionaries;
        var existingIndex = FindThemeDictionaryIndex(dictionaries);
        if (existingIndex >= 0)
        {
            dictionaries[existingIndex] = replacement;
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }

        CurrentTheme = theme;
        _hasAppliedTheme = true;
    }

    public void ToggleAnimated(long startedAt)
    {
        PrepareToggle();
        StartTransition(startedAt);
    }

    public void PrepareToggle()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _dispatcher.VerifyAccess();
        if (_transitionRunning) OnTransitionRendering(this, EventArgs.Empty);
        StopTransition();

        var nextTheme = CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        var target = GetThemeDictionary(nextTheme);
        var replacement = new ResourceDictionary();
        replacement.MergedDictionaries.Add(target);
        var brushes = new Dictionary<object, SolidColorBrush>();
        CollectBrushes(target, brushes);
        var mutableDuringTransition = new DoubleAnimation(1, 1, Duration.Forever);
        mutableDuringTransition.Freeze();
        foreach (var (key, targetBrush) in brushes)
        {
            var from = _applicationResources[key] is SolidColorBrush source ? EffectiveColor(source) : EffectiveColor(targetBrush);
            var to = EffectiveColor(targetBrush);
            var animated = targetBrush.CloneCurrentValue();
            animated.Color = from;
            animated.Opacity = 1;
            // Shared WPF styles can freeze plain brushes. An active clock keeps this temporary palette mutable.
            animated.BeginAnimation(Brush.OpacityProperty, mutableDuringTransition);
            replacement[key] = animated;
            _colorTransitions.Add((animated, from, to));
        }
        replacement[ThemeRevisionKey] = new object();
        replacement[TargetThemeKey] = target;
        var dictionaries = _applicationResources.MergedDictionaries;
        var index = FindThemeDictionaryIndex(dictionaries);
        if (index >= 0) dictionaries[index] = replacement;
        else dictionaries.Insert(0, replacement);
        CurrentTheme = nextTheme;
        _hasAppliedTheme = true;
    }

    public void StartTransition(long startedAt)
    {
        _dispatcher.VerifyAccess();
        if (_colorTransitions.Count == 0 || _transitionRunning) return;
        _initialTransitionElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt;
        _transitionClock = Stopwatch.GetTimestamp();
        _transitionRunning = true;
        CompositionTarget.Rendering += OnTransitionRendering;
    }

    public void CompleteTransition()
    {
        _dispatcher.VerifyAccess();
        if (_colorTransitions.Count > 0) RenderTransitionFrame(TransitionDurationMilliseconds);
    }

    private void OnTransitionRendering(object? sender, EventArgs e) =>
        RenderTransitionFrame(_initialTransitionElapsed + Stopwatch.GetElapsedTime(_transitionClock).TotalMilliseconds);

    public void RenderTransitionFrame(double elapsedMilliseconds)
    {
        if (_colorTransitions.Count == 0) return;
        var progress = Math.Clamp(elapsedMilliseconds / TransitionDurationMilliseconds, 0, 1);
        var amount = EaseTransition(progress);
        foreach (var (brush, from, to) in _colorTransitions) brush.Color = Interpolate(from, to, amount);
        if (progress >= 1) StopTransition();
        TransitionFrame?.Invoke(this, EventArgs.Empty);
    }

    // Same easing used by both the palette and the mist in the selected scheme-2 prototype.
    internal static double EaseTransition(double progress)
    {
        return progress < 0.5
            ? 4 * progress * progress * progress
            : 1 - Math.Pow(2 - 2 * progress, 3) / 2;
    }

    private static Color Interpolate(Color from, Color to, double amount)
    {
        if (amount <= 0) return from;
        if (amount >= 1) return to;
        var alpha = from.A + (to.A - from.A) * amount;
        byte Channel(byte left, byte right) => alpha <= 0 ? right :
            (byte)Math.Round((left * from.A * (1 - amount) + right * to.A * amount) / alpha);
        return new Color
        {
            A = (byte)Math.Round(alpha),
            R = Channel(from.R, to.R),
            G = Channel(from.G, to.G),
            B = Channel(from.B, to.B)
        };
    }

    private static Color EffectiveColor(SolidColorBrush brush)
    {
        var color = brush.Color;
        color.A = (byte)Math.Round(color.A * brush.Opacity);
        return color;
    }

    private static void CollectBrushes(ResourceDictionary resources, Dictionary<object, SolidColorBrush> brushes)
    {
        foreach (var merged in resources.MergedDictionaries) CollectBrushes(merged, brushes);
        foreach (var key in resources.Keys)
            if (resources[key] is SolidColorBrush brush) brushes[key] = brush;
    }

    private void StopTransition()
    {
        CompositionTarget.Rendering -= OnTransitionRendering;
        _transitionRunning = false;
        foreach (var (brush, _, _) in _colorTransitions) brush.BeginAnimation(Brush.OpacityProperty, null);
        _colorTransitions.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopTransition();
        _preload?.Abort();
        _preload = null;
        _systemThemeMonitor.ThemeChanged -= OnSystemThemeChanged;
    }

    private ResourceDictionary GetThemeDictionary(AppTheme theme)
    {
        if (_themeDictionaries.TryGetValue(theme, out var dictionary)) return dictionary;
        var uri = new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"pack://application:,,,/Differ.App;component/Themes/{theme}.xaml"),
            UriKind.Absolute);
        dictionary = new ResourceDictionary { Source = uri };
        // Each theme has its own marker. Consumers see a change even when both dictionaries are cached.
        dictionary[ThemeRevisionKey] = new object();
        _themeDictionaries.Add(theme, dictionary);
        return dictionary;
    }

    private static int FindThemeDictionaryIndex(
        Collection<ResourceDictionary> dictionaries)
    {
        for (var index = 0; index < dictionaries.Count; index++)
        {
            if (dictionaries[index].Contains(ThemeNameKey))
            {
                return index;
            }
        }

        return -1;
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            ApplySystemThemeIfNeeded();
            return;
        }

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            ApplySystemThemeIfNeeded);
    }

    private void ApplySystemThemeIfNeeded()
    {
        if (_isDisposed)
        {
            return;
        }

        var theme = _systemThemeMonitor.CurrentTheme;
        if (theme != CurrentTheme)
        {
            Apply(theme);
        }
    }
}

public sealed class WindowsSystemThemeMonitor : ISystemThemeMonitor, IDisposable
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private bool _isSubscribed;
    private bool _isDisposed;
    private AppTheme _currentTheme;

    public WindowsSystemThemeMonitor()
    {
        _currentTheme = DetectSystemTheme();
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _isSubscribed = true;
        }
        catch (InvalidOperationException)
        {
            // Some non-interactive Windows sessions do not expose system events.
            // The startup registry value still provides a deterministic theme.
        }
    }

    public AppTheme CurrentTheme => _currentTheme;

    public event EventHandler? ThemeChanged;

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_isSubscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _isSubscribed = false;
        }
    }

    private static AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue(AppsUseLightThemeValue) is int appsUseLight)
            {
                return appsUseLight == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch (Exception exception) when (
            exception is SecurityException
            or UnauthorizedAccessException
            or IOException)
        {
            // Theme detection is cosmetic and must never prevent startup.
        }

        return AppTheme.Light;
    }

    private void OnUserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        var detectedTheme = DetectSystemTheme();
        if (detectedTheme == _currentTheme)
        {
            return;
        }

        _currentTheme = detectedTheme;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}
