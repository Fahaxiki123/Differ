using System.Windows;
using Differ.App.Services;

namespace Differ.App.Tests;

public sealed class ThemeMergeOrderTests
{
    [Fact]
    public void ApplyChangesResolvedBrushesWithProductionMergeOrder()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(
                ThemeTestSupport.Load("Themes/Controls.xaml"));
            using var service = ThemeTestSupport.CreateService(
                resources,
                new FakeSystemThemeMonitor(AppTheme.Light));

            service.InitializeFromSystem();
            var lightCanvas = ThemeTestSupport.ResolveColour(
                resources,
                "CanvasBrush");
            var lightInk = ThemeTestSupport.ResolveColour(resources, "InkBrush");
            var lightWindow = ThemeTestSupport.ResolveColour(
                resources,
                "WindowBackgroundBrush");

            service.Apply(AppTheme.Dark);
            var darkCanvas = ThemeTestSupport.ResolveColour(
                resources,
                "CanvasBrush");
            var darkInk = ThemeTestSupport.ResolveColour(resources, "InkBrush");
            var darkWindow = ThemeTestSupport.ResolveColour(
                resources,
                "WindowBackgroundBrush");

            Assert.Equal(AppTheme.Dark, service.CurrentTheme);
            Assert.NotEqual(lightCanvas, darkCanvas);
            Assert.NotEqual(lightInk, darkInk);
            Assert.NotEqual(lightWindow, darkWindow);
            Assert.NotNull(ThemeTestSupport.FindResource(
                resources,
                typeof(System.Windows.Controls.Primitives.ScrollBar)));
            Assert.Equal(2, resources.MergedDictionaries.Count);
        });
    }

    [Fact]
    public void ControlsDictionaryDoesNotShadowThemeKeys()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var light = ThemeTestSupport.Load("Themes/Light.xaml");
            var controls = ThemeTestSupport.Load("Themes/Controls.xaml");

            var shadowed = controls.Keys
                .Cast<object>()
                .Where(key => light.Contains(key))
                .Select(key => key.ToString())
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();

            Assert.Empty(shadowed);
        });
    }

    [Fact]
    public void ThemeDictionaryIsFoundEvenWhenItIsNotFirst()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(
                ThemeTestSupport.Load("Themes/Controls.xaml"));
            resources.MergedDictionaries.Add(
                ThemeTestSupport.Load("Themes/Light.xaml"));
            using var service = ThemeTestSupport.CreateService(
                resources,
                new FakeSystemThemeMonitor(AppTheme.Light));

            service.Apply(AppTheme.Dark);

            Assert.Equal(2, resources.MergedDictionaries.Count);
            Assert.Equal(
                "Dark",
                resources.MergedDictionaries[1]["Theme.Name"]);
        });
    }
}
