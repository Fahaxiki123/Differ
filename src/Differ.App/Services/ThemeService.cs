using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
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
}

public interface ISystemThemeMonitor
{
    AppTheme CurrentTheme { get; }

    event EventHandler? ThemeChanged;
}

public sealed class ThemeService : IThemeService
{
    private const string ThemeNameKey = "Theme.Name";

    private readonly ResourceDictionary _applicationResources;
    private readonly Dispatcher _dispatcher;
    private readonly ISystemThemeMonitor _systemThemeMonitor;
    private bool _isDisposed;

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
    }

    public void Toggle()
    {
        Apply(CurrentTheme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
    }

    public void Apply(AppTheme theme)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _dispatcher.VerifyAccess();

        var replacement = LoadThemeDictionary(theme);
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
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _systemThemeMonitor.ThemeChanged -= OnSystemThemeChanged;
    }

    private static ResourceDictionary LoadThemeDictionary(AppTheme theme)
    {
        var uri = new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"pack://application:,,,/Differ.App;component/Themes/{theme}.xaml"),
            UriKind.Absolute);
        return new ResourceDictionary { Source = uri };
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
