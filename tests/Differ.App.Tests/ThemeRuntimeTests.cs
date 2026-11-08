using System.Windows;
using System.Windows.Media;
using Differ.App.Services;

namespace Differ.App.Tests;

public sealed class ThemeRuntimeTests
{
    [Fact]
    public void BothThemeDictionariesLoadInWpf()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var light = ThemeTestSupport.Load("Themes/Light.xaml");
            var dark = ThemeTestSupport.Load("Themes/Dark.xaml");

            Assert.Equal("Light", light["Theme.Name"]);
            Assert.Equal("Dark", dark["Theme.Name"]);
            Assert.IsType<SolidColorBrush>(light["CanvasBrush"]);
            Assert.IsType<SolidColorBrush>(dark["CanvasBrush"]);
        });
    }

    [Fact]
    public void ApplyReplacesTheMergedThemeAndChangesResolvedBrushes()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(
                ThemeTestSupport.Load("Themes/Light.xaml"));
            var monitor = new FakeSystemThemeMonitor(AppTheme.Light);
            using var service = ThemeTestSupport.CreateService(resources, monitor);

            service.Apply(AppTheme.Light);
            var lightCanvas = ThemeTestSupport.ResolveColour(
                resources,
                "CanvasBrush");
            var lightInk = ThemeTestSupport.ResolveColour(resources, "InkBrush");

            service.Apply(AppTheme.Dark);
            var darkCanvas = ThemeTestSupport.ResolveColour(
                resources,
                "CanvasBrush");
            var darkInk = ThemeTestSupport.ResolveColour(resources, "InkBrush");

            Assert.Equal(AppTheme.Dark, service.CurrentTheme);
            Assert.NotEqual(lightCanvas, darkCanvas);
            Assert.NotEqual(lightInk, darkInk);
            Assert.Single(resources.MergedDictionaries);

            service.Apply(AppTheme.Light);
            Assert.Equal(
                lightCanvas,
                ThemeTestSupport.ResolveColour(resources, "CanvasBrush"));
            Assert.Single(resources.MergedDictionaries);
        });
    }

    [Fact]
    public void ToggleFlipsBetweenThemes()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var resources = new ResourceDictionary();
            var monitor = new FakeSystemThemeMonitor(AppTheme.Light);
            using var service = ThemeTestSupport.CreateService(resources, monitor);

            service.InitializeFromSystem();
            service.Toggle();
            Assert.Equal(AppTheme.Dark, service.CurrentTheme);

            service.Toggle();
            Assert.Equal(AppTheme.Light, service.CurrentTheme);
        });
    }

    [Fact]
    public void SystemPreferenceChangeUpdatesTheActiveTheme()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var resources = new ResourceDictionary();
            var monitor = new FakeSystemThemeMonitor(AppTheme.Light);
            using var service = ThemeTestSupport.CreateService(resources, monitor);

            service.InitializeFromSystem();
            monitor.SetTheme(AppTheme.Dark);

            Assert.Equal(AppTheme.Dark, service.CurrentTheme);
            Assert.Equal(
                "Dark",
                ThemeTestSupport.FindResource(resources, "Theme.Name"));
        });
    }

    [Fact]
    public void SeparateThemeServicesKeepIndependentState()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var firstResources = new ResourceDictionary();
            var secondResources = new ResourceDictionary();
            using var first = ThemeTestSupport.CreateService(
                firstResources,
                new FakeSystemThemeMonitor(AppTheme.Light));
            using var second = ThemeTestSupport.CreateService(
                secondResources,
                new FakeSystemThemeMonitor(AppTheme.Dark));

            first.InitializeFromSystem();
            second.InitializeFromSystem();
            first.Toggle();

            Assert.Equal(AppTheme.Dark, first.CurrentTheme);
            Assert.Equal(AppTheme.Dark, second.CurrentTheme);
            second.Toggle();
            Assert.Equal(AppTheme.Dark, first.CurrentTheme);
            Assert.Equal(AppTheme.Light, second.CurrentTheme);
        });
    }
}
