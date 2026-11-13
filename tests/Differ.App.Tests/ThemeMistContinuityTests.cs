using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Differ.App.Controls;
using Differ.App.Services;

namespace Differ.App.Tests;

public sealed class ThemeMistContinuityTests
{
    [Theory]
    [InlineData(1500, 860)]
    [InlineData(720, 620)]
    public void DuskStartsAtTheLightPaletteAndOnlyLosesCompositeBrightness(int width, int height)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var fog = new ThemeMistOverlay();
            fog.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/ThemeMist.xaml"));
            var resources = new ResourceDictionary();
            using var themes = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            themes.InitializeFromSystem();
            var root = new Grid();
            root.Resources.MergedDictionaries.Add(resources);
            root.SetResourceReference(Panel.BackgroundProperty, "Dui.Surface.Content");
            root.Children.Add(fog);
            var window = new Window
            {
                Content = root,
                Width = width,
                Height = height,
                Left = -20000,
                Top = -20000,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            Point[] samples = [new(width - 78, 20), new(width - 156, 37), new(width / 2, height / 2),
                new(24, height - 40), new(width - 30, height - 80), new(width / 4, height / 4)];
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.True(fog.Start(new Point(width - 78, 58), fog.RenderSize, true, 0));
                themes.PrepareToggle();
                Assert.Same(resources["Dui.Surface.Content"], root.Background);
                root.UpdateLayout();
                var previous = ReadSamples(Render(root, width, height), samples);
                Assert.All(previous, value => Assert.Equal(255, value));
                for (var elapsed = 20; elapsed <= 680; elapsed += 20)
                {
                    themes.RenderTransitionFrame(elapsed);
                    fog.RenderFrame(elapsed);
                    var current = ReadSamples(Render(root, width, height), samples);
                    for (var pixel = 0; pixel < current.Length; pixel += 4)
                        Assert.True(Luminance(current, pixel) <= Luminance(previous, pixel) + 1,
                            $"Dusk brightness rose at {elapsed} ms, sample {pixel / 4}: {Luminance(previous, pixel):F2} -> {Luminance(current, pixel):F2}.");
                    previous = current;
                }
                var target = ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content");
                Assert.Equal(target.B, previous[0]);
                Assert.Equal(target.G, previous[1]);
                Assert.Equal(target.R, previous[2]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1500, 860, true, 306)]
    [InlineData(1500, 860, true, 500)]
    [InlineData(1500, 860, false, 250)]
    [InlineData(720, 620, true, 306)]
    [InlineData(720, 620, true, 500)]
    [InlineData(720, 620, false, 250)]
    public void FogContinuesThroughCaptionAndClearsOnCancellation(int width, int height, bool dark, double elapsed)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var fog = new ThemeMistOverlay();
            fog.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/ThemeMist.xaml"));
            var window = new Window
            {
                Content = fog,
                Width = width,
                Height = height,
                Left = -20000,
                Top = -20000,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var area = fog.RenderSize;
                var origin = new Point(area.Width - 78, 58);
                Assert.True(fog.Start(origin, area, dark, 0));
                fog.RenderFrame(elapsed);
                var bitmap = Render(fog, width, height);
                var boundary = width - 156;
                Assert.False(fog.IsHitTestVisible);
                Assert.Null(fog.OpacityMask);
                Assert.InRange(Math.Abs(Alpha(bitmap, boundary - 1, 12) - Alpha(bitmap, boundary, 12)), 0, 3);
                Assert.True(Alpha(bitmap, width - 78, 12) > 0, "Native caption coordinates must receive the same fog field.");
                var captionRow = Enumerable.Range(boundary, 150).Select(x => Alpha(bitmap, x, 12)).ToArray();
                Assert.True(captionRow.Max() > captionRow.Min(), "The caption must not become a uniform rectangle.");

                var stopped = 0;
                fog.Stopped += (_, _) => stopped++;
                fog.Stop();
                fog.RenderFrame(elapsed);
                Assert.Equal(1, stopped);
                Assert.Equal(0, Alpha(Render(fog, width, height), width - 78, 12));
                Assert.True(fog.Start(origin, area, !dark, 0));
                fog.RenderFrame(680);
                Assert.Equal(2, stopped);
                Assert.Equal(0, Alpha(Render(fog, width, height), width - 78, 12));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static RenderTargetBitmap Render(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static int Alpha(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static byte[] ReadSamples(BitmapSource bitmap, Point[] points)
    {
        var result = new byte[points.Length * 4];
        for (var index = 0; index < points.Length; index++)
            bitmap.CopyPixels(new Int32Rect((int)points[index].X, (int)points[index].Y, 1, 1), result, 4, index * 4);
        return result;
    }

    private static double Luminance(byte[] pixels, int index) =>
        pixels[index] * 0.0722 + pixels[index + 1] * 0.7152 + pixels[index + 2] * 0.2126;
}
