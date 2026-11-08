using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Services;

namespace Differ.App.Tests;

internal static class ThemeTestSupport
{
    private static readonly BlockingCollection<Action> WorkItems = [];
    private static readonly ManualResetEventSlim HostReady = new();

    static ThemeTestSupport()
    {
        var hostThread = new Thread(() =>
        {
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            HostReady.Set();
            foreach (var workItem in WorkItems.GetConsumingEnumerable())
            {
                workItem();
            }
        })
        {
            IsBackground = true,
            Name = "Differ theme test host"
        };
        hostThread.SetApartmentState(ApartmentState.STA);
        hostThread.Start();
    }

    public static void RunSta(Action action)
    {
        HostReady.Wait();
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        WorkItems.Add(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });
        completed.Wait();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public static ResourceDictionary Load(string relativePath)
    {
        return new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/Differ.App;component/{relativePath}",
                UriKind.Absolute)
        };
    }

    public static ThemeService CreateService(
        ResourceDictionary resources,
        ISystemThemeMonitor monitor)
    {
        return new ThemeService(
            resources,
            Dispatcher.CurrentDispatcher,
            monitor);
    }

    public static Color ResolveColour(
        ResourceDictionary resources,
        string key)
    {
        return Assert.IsType<SolidColorBrush>(FindResource(resources, key)).Color;
    }

    public static object? FindResource(
        ResourceDictionary resources,
        object key)
    {
        if (resources.Keys.Cast<object>().Any(candidate => Equals(candidate, key)))
        {
            return resources[key];
        }

        for (var index = resources.MergedDictionaries.Count - 1;
             index >= 0;
             index--)
        {
            var value = FindResource(resources.MergedDictionaries[index], key);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }
}

internal sealed class FakeSystemThemeMonitor(AppTheme initialTheme)
    : ISystemThemeMonitor
{
    public AppTheme CurrentTheme { get; private set; } = initialTheme;

    public event EventHandler? ThemeChanged;

    public void SetTheme(AppTheme theme)
    {
        CurrentTheme = theme;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}
