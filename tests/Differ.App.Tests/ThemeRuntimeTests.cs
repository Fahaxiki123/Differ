using System.Windows;
using System.Windows.Media;
using Differ.App.Presentation.Theme;
using Differ.App.Services;

namespace Differ.App.Tests;

public sealed class ThemeRuntimeTests
{
    private static readonly string[] DiffKinds = ["Added", "Deleted", "Modified", "Moved", "Conflict"];

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

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void DiffForegroundsRemainReadableOnBodyAndCellBackgrounds(string theme)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var resources = ThemeTestSupport.Load($"Themes/{theme}.xaml");
            foreach (var kind in DiffKinds)
            {
                var foreground = ThemeTestSupport.ResolveColour(resources, $"Diff.Inline.{kind}.Brush");
                foreach (var backgroundKey in new[] { $"Diff.Row.{kind}.Bg", $"Diff.Cell.{kind}" })
                {
                    var background = ThemeTestSupport.ResolveColour(resources, backgroundKey);
                    var contrast = (Math.Max(Luminance(foreground), Luminance(background)) + 0.05) /
                        (Math.Min(Luminance(foreground), Luminance(background)) + 0.05);
                    Assert.True(contrast >= 4.5, $"{theme} {kind} on {backgroundKey}: {contrast:F2}:1.");
                }
            }
        });
    }

    private static double Luminance(Color colour) =>
        0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);

    private static double Linear(byte component)
    {
        var value = component / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
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
            var lightDictionary = Assert.Single(resources.MergedDictionaries);
            service.Apply(AppTheme.Light);
            Assert.Same(lightDictionary, Assert.Single(resources.MergedDictionaries));
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
            Assert.Same(lightDictionary, Assert.Single(resources.MergedDictionaries));
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
    public void AnimatedThemeKeepsOneRootAndMovesContinuouslyToItsTarget()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var resources = new ResourceDictionary();
            using var service = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            service.InitializeFromSystem();
            var light = ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content");
            service.ToggleAnimated(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.Equal(AppTheme.Dark, service.CurrentTheme);
            Assert.Single(resources.MergedDictionaries);
            Assert.Equal(light, ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content"));
            var target = Assert.IsType<ResourceDictionary>(resources[ThemeService.TargetThemeKey]);
            var dark = ThemeTestSupport.ResolveColour(target, "Dui.Surface.Content");
            Assert.NotEqual(light, dark);
            var previous = light;
            for (var elapsed = 20; elapsed <= 680; elapsed += 20)
            {
                service.RenderTransitionFrame(elapsed);
                var current = ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content");
                Assert.InRange(current.R, dark.R, previous.R);
                Assert.InRange(current.G, dark.G, previous.G);
                Assert.InRange(current.B, dark.B, previous.B);
                previous = current;
            }
            Assert.Equal(dark, previous);
            Assert.Equal(dark, ThemeTestSupport.ResolveColour(target, "Dui.Surface.Content"));
            service.ToggleAnimated(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.Equal(dark, ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content"));
            service.CompleteTransition();
            Assert.Equal(light, ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content"));
            Assert.Single(resources.MergedDictionaries);
        });
    }

    [Fact]
    public void ReversingAnAnimatedThemeStartsFromItsVisibleColor()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var resources = new ResourceDictionary();
            using var service = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            service.InitializeFromSystem();
            // The window drives its shared timeline, so reversal must preserve the last rendered color.
            service.PrepareToggle();
            service.RenderTransitionFrame(200);
            var before = ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content");
            service.PrepareToggle();
            var reversed = ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content");
            Assert.Equal(before, reversed);
            service.Apply(AppTheme.Dark);
            var applied = ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content");
            service.RenderTransitionFrame(680);
            Assert.Equal(applied, ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content"));
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
    public void EditorCssBrushConversionUsesRgbaAndRespectsOpacity()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var opaque = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56));
            var translucent = new SolidColorBrush(Color.FromArgb(0xA0, 0x12, 0x34, 0x56)) { Opacity = 0.5 };

            Assert.Equal("#123456", EditorThemeAdapter.ToCssHex(opaque));
            Assert.Equal("#12345650", EditorThemeAdapter.ToCssHex(translucent));
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
