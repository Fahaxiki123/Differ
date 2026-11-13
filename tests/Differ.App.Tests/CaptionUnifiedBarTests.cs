using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Differ.App.Controls;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Differ.Formats.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Wpf;

namespace Differ.App.Tests;

[Collection("UI performance")]
public sealed class CaptionUnifiedBarTests
{
    private const uint WmNcHitTest = 0x0084;
    private const uint WmNcMouseMove = 0x00A0;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmNcLButtonUp = 0x00A2;
    private const uint WmNcMouseLeave = 0x02A2;
    private const uint WmCancelMode = 0x001F;
    private const int HtTop = 12;
    private const int HtTopRight = 14;
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly int[] TransitionSampleFrames = [0, 60, 150, 250, 306, 420, 500, 620, 680];
    private static readonly int[] ReverseSampleFrames = [0, 120, 340, 680];
    private static readonly (int Width, int Height)[] EvidenceWindowSizes = [(720, 620), (1500, 860)];
    private static readonly int[] EvidenceLightToDarkFrames = [0, 150, 306, 500, 680];
    private static readonly int[] EvidenceDarkToLightFrames = [0, 150, 250, 500, 680];

    [Theory]
    [InlineData(720, 620, true)]
    [InlineData(720, 620, false)]
    [InlineData(1500, 860, true)]
    [InlineData(1500, 860, false)]
    public void TitleBarSurfaceRemainsContinuousAcrossCaptionBoundariesDuringTransitions(
        int width,
        int height,
        bool toDark)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var dispatcherContext = new DispatcherContextScope();
            var tempRoot = Path.Combine(Path.GetTempPath(), "Differ.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            using var preferences = new UserPreferencesService(Path.Combine(tempRoot, "preferences.json"));
            using var scheduler = new ComparisonLoadScheduler();
            using var workspace = new ComparisonWorkspaceViewModel(
                new DiffCoordinator([]),
                scheduler,
                new ExternalDiffSessionStore(Path.Combine(tempRoot, "sessions")),
                NullLoggerFactory.Instance,
                preferences);
            var resources = LoadApplicationResources();
            var initialTheme = toDark ? AppTheme.Light : AppTheme.Dark;
            using var themes = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(initialTheme));
            themes.InitializeFromSystem();

            var window = new DiffWindow(workspace, themes)
            {
                Width = width,
                Height = height,
                Left = -16_000,
                Top = -16_000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                FontSize = 13
            };
            window.Resources.MergedDictionaries.Add(resources);

            try
            {
                window.Show();
                FlushLayout(window);
                var root = Assert.IsType<Grid>(window.Content);
                var fog = Assert.IsType<ThemeMistOverlay>(window.FindName("ThemeMist"));
                var toggleBtn = Assert.IsType<Button>(window.FindName("ThemeToggleButton"));
                var origin = toggleBtn.TranslatePoint(new Point(toggleBtn.ActualWidth / 2, toggleBtn.ActualHeight / 2), root);
                var area = root.RenderSize;

                Assert.True(fog.Start(origin, area, toDark, 0, automaticClock: false));
                themes.PrepareToggle();
                FlushLayout(window);

                int[] boundaries = [width - 200, width - 156, width - 138, width - 92, width - 46];
                foreach (var elapsed in TransitionSampleFrames)
                {
                    if (elapsed > 0)
                    {
                        themes.RenderTransitionFrame(elapsed);
                        fog.RenderFrame(elapsed);
                    }

                    FlushLayout(window);
                    var frame = RenderElement(root);
                    AssertSeamlessCaptionRow(frame, width, boundaries, elapsed);
                }

                // Rapid reverse mid-transition check: start again and reverse at 240 ms.
                fog.Stop();
                themes.CompleteTransition();
                FlushLayout(window);

                Assert.True(fog.Start(origin, area, !toDark, 0, automaticClock: false));
                themes.PrepareToggle();
                themes.RenderTransitionFrame(240);
                fog.RenderFrame(240);
                FlushLayout(window);
                AssertSeamlessCaptionRow(RenderElement(root), width, boundaries, 240);

                // Reverse immediately back toward toDark while mid-flight.
                Assert.True(fog.Start(origin, area, toDark, 0, automaticClock: false));
                themes.PrepareToggle();
                foreach (var reverseElapsed in ReverseSampleFrames)
                {
                    if (reverseElapsed > 0)
                    {
                        themes.RenderTransitionFrame(reverseElapsed);
                        fog.RenderFrame(reverseElapsed);
                    }

                    FlushLayout(window);
                    AssertSeamlessCaptionRow(RenderElement(root), width, boundaries, reverseElapsed);
                }
            }
            finally
            {
                window.Close();
                FlushLayout(window);
                workspace.Dispose();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        });
    }

    [Fact]
    public void CaptionButtonsPreserveNonClientHitTestingResizeBordersAndVisualStates() =>
        RunCaptionButtonChecks(foregroundCommands: false);

    [ForegroundCaptionFact]
    [Trait("Category", "Foreground")]
    public void ForegroundCaptionCommandsMaximizeRestoreAndMinimize() =>
        RunCaptionButtonChecks(foregroundCommands: true);

    private static void RunCaptionButtonChecks(bool foregroundCommands)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var dispatcherContext = new DispatcherContextScope();
            var tempRoot = Path.Combine(Path.GetTempPath(), "Differ.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            using var preferences = new UserPreferencesService(Path.Combine(tempRoot, "preferences.json"));
            using var scheduler = new ComparisonLoadScheduler();
            using var workspace = new ComparisonWorkspaceViewModel(
                new DiffCoordinator([]),
                scheduler,
                new ExternalDiffSessionStore(Path.Combine(tempRoot, "sessions")),
                NullLoggerFactory.Instance,
                preferences);
            var resources = LoadApplicationResources();
            using var themes = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            themes.InitializeFromSystem();

            var window = new DiffWindow(workspace, themes)
            {
                Width = 720,
                Height = 620,
                Left = foregroundCommands ? 40 : -8_000,
                Top = foregroundCommands ? 40 : -8_000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = foregroundCommands,
                ShowInTaskbar = foregroundCommands,
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                FontSize = 13
            };
            window.Resources.MergedDictionaries.Add(resources);

            try
            {
                window.Show();
                FlushLayout(window);

                var hwnd = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(0, hwnd);
                var root = Assert.IsType<Grid>(window.Content);
                var minBtn = Assert.IsType<Button>(window.FindName("MinimizeCaptionButton"));
                var maxBtn = Assert.IsType<Button>(window.FindName("MaximizeRestoreCaptionButton"));
                var closeBtn = Assert.IsType<Button>(window.FindName("CloseCaptionButton"));
                var glyphPath = Assert.IsType<System.Windows.Shapes.Path>(window.FindName("MaximizeGlyphPath"));

                Assert.Equal(ChromeStrings.MinimizeWindow, AutomationProperties.GetName(minBtn));
                Assert.Equal(ChromeStrings.MaximizeWindow, AutomationProperties.GetName(maxBtn));
                Assert.Equal(ChromeStrings.CloseWindow, AutomationProperties.GetName(closeBtn));

                // Center hit-tests return native caption hit codes for Windows 11 Snap Layouts & system behavior.
                Assert.Equal(DiffWindow.HtMinButton, HitTestAtElement(hwnd, minBtn, minBtn.ActualWidth / 2, minBtn.ActualHeight / 2));
                Assert.Equal(DiffWindow.HtMaxButton, HitTestAtElement(hwnd, maxBtn, maxBtn.ActualWidth / 2, maxBtn.ActualHeight / 2));
                Assert.Equal(DiffWindow.HtClose, HitTestAtElement(hwnd, closeBtn, closeBtn.ActualWidth / 2, closeBtn.ActualHeight / 2));

                // Top outer 6 DIP in Normal resizable state preserves top/top-right window resize grip.
                var topAboveMax = HitTestAtElement(hwnd, root, root.ActualWidth - 69, 2);
                var topRightCorner = HitTestAtElement(hwnd, root, root.ActualWidth - 2, 2);
                Assert.Equal(HtTop, topAboveMax);
                Assert.Equal(HtTopRight, topRightCorner);

                // Non-client hover & press visual states (including red danger hover on Close).
                // Use UpdateLayout() before pumping the Win32 queue because TrackMouseEvent(TME_NONCLIENT | TME_LEAVE)
                // immediately posts WM_NCMOUSELEAVE on the next message pump when the physical cursor is not over (-8000, -8000).
                var closeCoords = PackScreenCoordinates(closeBtn.PointToScreen(new Point(closeBtn.ActualWidth / 2, closeBtn.ActualHeight / 2)));
                _ = SendMessageW(hwnd, WmNcMouseMove, DiffWindow.HtClose, closeCoords);
                window.UpdateLayout();
                Assert.True(DiffWindow.GetIsCaptionHovered(closeBtn));
                Assert.False(DiffWindow.GetIsCaptionHovered(maxBtn));
                var closeChrome = Assert.IsType<Border>(closeBtn.Template.FindName("Chrome", closeBtn));
                var expectedHoverRed = ThemeTestSupport.ResolveColour(resources, "Caption.CloseHoverBrush");
                Assert.Equal(expectedHoverRed, Assert.IsType<SolidColorBrush>(closeChrome.Background).Color);
                Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(closeBtn.Foreground).Color);

                _ = SendMessageW(hwnd, WmNcLButtonDown, DiffWindow.HtClose, closeCoords);
                window.UpdateLayout();
                Assert.True(DiffWindow.GetIsCaptionPressed(closeBtn));
                var expectedPressedRed = ThemeTestSupport.ResolveColour(resources, "Caption.ClosePressedBrush");
                Assert.Equal(expectedPressedRed, Assert.IsType<SolidColorBrush>(closeChrome.Background).Color);

                // Leaving clears visual hover, but only an explicit cancellation ends the captured gesture.
                FlushLayout(window);
                _ = SendMessageW(hwnd, WmNcMouseLeave, 0, 0);
                FlushLayout(window);
                Assert.False(DiffWindow.GetIsCaptionHovered(closeBtn));
                Assert.False(DiffWindow.GetIsCaptionPressed(closeBtn));
                _ = SendMessageW(hwnd, WmCancelMode, 0, 0);
                FlushLayout(window);
                Assert.True(window.IsVisible);

                var maxInvoke = Assert.IsAssignableFrom<IInvokeProvider>(
                    new ButtonAutomationPeer(maxBtn).GetPattern(PatternInterface.Invoke));
                var minInvoke = Assert.IsAssignableFrom<IInvokeProvider>(
                    new ButtonAutomationPeer(minBtn).GetPattern(PatternInterface.Invoke));
                if (foregroundCommands)
                {
                    // These commands can bring an off-screen HWND onto the desktop; never run them implicitly.
                    var normalGlyphText = glyphPath.Data.ToString(CultureInfo.InvariantCulture);
                    var maxCoords = PackScreenCoordinates(maxBtn.PointToScreen(new Point(maxBtn.ActualWidth / 2, maxBtn.ActualHeight / 2)));
                    _ = SendMessageW(hwnd, WmNcLButtonDown, DiffWindow.HtMaxButton, maxCoords);
                    _ = SendMessageW(hwnd, WmNcLButtonUp, DiffWindow.HtMaxButton, maxCoords);
                    FlushLayout(window);
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    Assert.Equal(ChromeStrings.RestoreWindow, AutomationProperties.GetName(maxBtn));
                    Assert.NotEqual(normalGlyphText, glyphPath.Data.ToString(CultureInfo.InvariantCulture));
                    Assert.Equal(DiffWindow.HtClose, HitTestAtElement(hwnd, closeBtn, closeBtn.ActualWidth - 1, 1));

                    maxInvoke.Invoke();
                    FlushLayout(window);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.Equal(ChromeStrings.MaximizeWindow, AutomationProperties.GetName(maxBtn));

                    minInvoke.Invoke();
                    FlushLayout(window);
                    Assert.Equal(WindowState.Minimized, window.WindowState);
                    SystemCommands.RestoreWindow(window);
                    FlushLayout(window);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                }
                else
                {
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.True(window.Left < 0 && window.Top < 0);
                }

                // Closing this isolated, empty window is safe in either mode and must still use WPF Closing.
                var closeInvoke = Assert.IsAssignableFrom<IInvokeProvider>(
                    new ButtonAutomationPeer(closeBtn).GetPattern(PatternInterface.Invoke));
                var closingFired = false;
                window.Closing += (_, _) => closingFired = true;
                closeInvoke.Invoke();
                FlushLayout(window);
                Assert.True(closingFired);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                    FlushLayout(window);
                }

                workspace.Dispose();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        });
    }

    [CaptionEvidenceFact]
    public void CapturesComparisonWindowCaptionTransitionEvidenceWhenConfigured()
    {
        var outputDirectory = Assert.IsType<string>(Environment.GetEnvironmentVariable("DIFFER_CAPTION_EVIDENCE_DIR"));
        var stage = Assert.IsType<string>(Environment.GetEnvironmentVariable("DIFFER_CAPTION_EVIDENCE_STAGE"));
        Assert.False(string.IsNullOrWhiteSpace(outputDirectory), "DIFFER_CAPTION_EVIDENCE_DIR must be configured.");
        Assert.False(string.IsNullOrWhiteSpace(stage), "DIFFER_CAPTION_EVIDENCE_STAGE must be configured.");

        ThemeTestSupport.RunSta(() =>
        {
            using var dispatcherContext = new DispatcherContextScope();
            Directory.CreateDirectory(outputDirectory);
            var tempRoot = Path.Combine(Path.GetTempPath(), "Differ.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var basePath = Path.Combine(tempRoot, "base.lua");
            var minePath = Path.Combine(tempRoot, "mine.lua");
            WriteLuaFixtures(basePath, minePath);

            try
            {
                var records = new List<object>();
                foreach (var (width, height) in EvidenceWindowSizes)
                {
                    records.Add(CaptureWindowScenario(outputDirectory, stage, tempRoot, basePath, minePath, width, height, readingScale: 1.0));
                }

                records.Add(CaptureWindowScenario(outputDirectory, stage, tempRoot, basePath, minePath, 720, 620, readingScale: 1.5));

                var json = JsonSerializer.Serialize(records, IndentedJson);
                File.WriteAllText(Path.Combine(outputDirectory, $"{stage}-seam-evidence.json"), json);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        });
    }

    [Fact]
    public void MissingEditorCannotSilentlyProduceCaptionEvidence()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => CaptureComposite(new Grid()));
            Assert.Contains("ready text editor", error.Message, StringComparison.Ordinal);
        });
    }

    private static void AssertSeamlessCaptionRow(BitmapSource frame, int width, int[] boundaries, int elapsed)
    {
        // Row y = 28 lies inside the 38 DIP FileTabBar, safely below the 10x10 glyphs (y = 13..23) and above y = 37 border.
        const int sampleY = 28;
        for (var x = 0; x < width; x++)
        {
            var pixel = ReadBgra(frame, x, sampleY);
            Assert.True(pixel[3] == 255, $"Expected opaque WPF title bar pixel at x={x}, elapsed={elapsed}ms, got alpha={pixel[3]}.");
        }

        foreach (var boundary in boundaries)
        {
            var left = ReadBgra(frame, boundary - 1, sampleY);
            var right = ReadBgra(frame, boundary, sampleY);
            var delta = MaxDelta(left, right);
            Assert.True(
                delta <= 2,
                $"Seam discontinuity at x={boundary} ({elapsed}ms): left={FormatRgba(left)}, right={FormatRgba(right)}, delta={delta}.");
        }
    }

    private static int HitTestAtElement(nint hwnd, Visual element, double localX, double localY)
    {
        var screenPoint = element.PointToScreen(new Point(localX, localY));
        return (int)SendMessageW(hwnd, WmNcHitTest, 0, PackScreenCoordinates(screenPoint));
    }

    private static nint PackScreenCoordinates(Point screenPoint)
    {
        var x = (int)Math.Round(screenPoint.X) & 0xFFFF;
        var y = ((int)Math.Round(screenPoint.Y) & 0xFFFF) << 16;
        return x | y;
    }

    private static RenderTargetBitmap RenderElement(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static object CaptureWindowScenario(
        string outputDirectory,
        string stage,
        string tempRoot,
        string basePath,
        string minePath,
        int width,
        int height,
        double readingScale)
    {
        var scenarioTemp = Path.Combine(tempRoot, $"{width}x{height}-{readingScale:0.0}");
        Directory.CreateDirectory(scenarioTemp);
        using var preferences = new UserPreferencesService(Path.Combine(scenarioTemp, "preferences.json"));
        preferences.ReadingScale = readingScale;
        using var scheduler = new ComparisonLoadScheduler();
        using var workspace = new ComparisonWorkspaceViewModel(
            new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]),
            scheduler,
            new ExternalDiffSessionStore(Path.Combine(scenarioTemp, "sessions")),
            NullLoggerFactory.Instance,
            preferences);
        var resources = LoadApplicationResources();
        using var themes = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
        themes.InitializeFromSystem();

        var window = new DiffWindow(workspace, themes)
        {
            Width = width,
            Height = height,
            Left = -20_000,
            Top = -20_000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 13
        };
        window.Resources.MergedDictionaries.Add(resources);
        var viewModel = workspace.AddSession(new ExternalDiffSession
        {
            SessionId = Guid.NewGuid(),
            BaseSnapshotPath = basePath,
            MineSnapshotPath = minePath,
            Request = new ExternalDiffRequest
            {
                BaseFilePath = basePath,
                MineFilePath = minePath,
                OriginalFileName = "mine.lua",
                BaseDisplayName = "mine.lua (BASE)",
                MineDisplayName = "mine.lua (工作副本)",
                BaseRevision = "108"
            }
        });

        try
        {
            window.Show();
            PumpUntil(() => !viewModel.IsBusy && viewModel.DifferenceCount > 0, window);
            Assert.False(viewModel.HasError, viewModel.ErrorText);
            var editorView = Assert.Single(FindVisuals<CodeEditorView>(window), item => item.IsVisible);
            PumpUntil(() => editorView.IsEditorReady && viewModel.EditorResultsCurrent
                && editorView.RenderedVersion == viewModel.EditorSession!.Version, window);
            var root = Assert.IsType<Grid>(window.Content);
            var browser = RequireReadyBrowser(root);
            AssertBrowserDocumentAndScale(browser, window, basePath, minePath, readingScale);
            ExecuteScript(browser, window, "window.captionEvidenceModelIds=[original.id,modified.id];window.captionEvidencePaintReady=false;requestAnimationFrame(()=>requestAnimationFrame(()=>window.captionEvidencePaintReady=true));");
            PumpUntil(() => ExecuteScript(browser, window, "window.captionEvidencePaintReady===true") == "true", window);
            var fog = Assert.IsType<ThemeMistOverlay>(window.FindName("ThemeMist"));
            var toggleBtn = Assert.IsType<Button>(window.FindName("ThemeToggleButton"));
            FlushLayout(window);

            var scaleTag = Math.Abs(readingScale - 1.0) < 0.01
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"-zoom{(int)Math.Round(readingScale * 100)}");
            var prefix = string.Create(CultureInfo.InvariantCulture, $"{stage}-{width}x{height}{scaleTag}");

            SaveComposite(root, Path.Combine(outputDirectory, $"{prefix}-light.png"));

            var origin = toggleBtn.TranslatePoint(new Point(toggleBtn.ActualWidth / 2, toggleBtn.ActualHeight / 2), root);
            var area = root.RenderSize;

            var lightToDarkSamples = new List<object>();
            Assert.True(fog.Start(origin, area, true, 0, automaticClock: false));
            themes.PrepareToggle();
            FlushLayout(window);
            foreach (var elapsed in EvidenceLightToDarkFrames)
            {
                if (elapsed > 0)
                {
                    themes.RenderTransitionFrame(elapsed);
                    fog.RenderFrame(elapsed);
                }

                FlushLayout(window);
                var composite = CaptureComposite(root);
                if (elapsed is 150 or 306 or 500)
                {
                    SaveBitmap(composite, Path.Combine(outputDirectory, $"{prefix}-l2d-{elapsed}ms.png"));
                }

                lightToDarkSamples.Add(SampleSeamRow(composite, width, elapsed));
            }

            fog.Stop();
            themes.CompleteTransition();
            themes.Apply(AppTheme.Dark);
            FlushLayout(window);
            SaveComposite(root, Path.Combine(outputDirectory, $"{prefix}-dark.png"));

            // Also capture close-button hover state at 720x620 to document danger hover visuals.
            if (width == 720 && Math.Abs(readingScale - 1.0) < 0.01 && window.FindName("CloseCaptionButton") is Button closeBtn)
            {
                DiffWindow.SetIsCaptionHovered(closeBtn, true);
                FlushLayout(window);
                SaveComposite(root, Path.Combine(outputDirectory, $"{prefix}-close-hover-dark.png"));
                DiffWindow.SetIsCaptionHovered(closeBtn, false);
                FlushLayout(window);
            }

            var darkToLightSamples = new List<object>();
            Assert.True(fog.Start(origin, area, false, 0, automaticClock: false));
            themes.PrepareToggle();
            FlushLayout(window);
            foreach (var elapsed in EvidenceDarkToLightFrames)
            {
                if (elapsed > 0)
                {
                    themes.RenderTransitionFrame(elapsed);
                    fog.RenderFrame(elapsed);
                }

                FlushLayout(window);
                var composite = CaptureComposite(root);
                if (elapsed is 150 or 250 or 500)
                {
                    SaveBitmap(composite, Path.Combine(outputDirectory, $"{prefix}-d2l-{elapsed}ms.png"));
                }

                darkToLightSamples.Add(SampleSeamRow(composite, width, elapsed));
            }

            fog.Stop();
            themes.CompleteTransition();
            FlushLayout(window);
            AssertBrowserDocumentAndScale(browser, window, basePath, minePath, readingScale);
            Assert.Equal("true", ExecuteScript(browser, window,
                "captionEvidenceModelIds[0]===original.id&&captionEvidenceModelIds[1]===modified.id"));

            return new
            {
                Stage = stage,
                CaptureKind = "WPF-controlled frozen frames with a ready, rendered Monaco document; not foreground natural-playback evidence",
                Size = $"{width}x{height}",
                ReadingScale = readingScale,
                EditorReady = editorView.IsEditorReady,
                RenderedVersion = editorView.RenderedVersion,
                BrowserVersion = browser.CoreWebView2.Environment.BrowserVersionString,
                VerifiedFontSize = 15 * readingScale,
                VerifiedLineHeight = 24 * readingScale,
                SeamX = width - 156,
                ButtonsStartX = width - 138,
                LightToDark = lightToDarkSamples,
                DarkToLight = darkToLightSamples
            };
        }
        finally
        {
            window.Close();
            PumpUntil(() => !window.IsVisible, window);
            workspace.Dispose();
        }
    }

    private static object SampleSeamRow(BitmapSource bitmap, int width, int elapsed)
    {
        var seam156 = width - 156;
        var seam138 = width - 138;
        var left156 = ReadBgra(bitmap, seam156 - 2, 28);
        var right156 = ReadBgra(bitmap, seam156 + 2, 28);
        var left138 = ReadBgra(bitmap, seam138 - 2, 28);
        var right138 = ReadBgra(bitmap, seam138 + 2, 28);
        return new
        {
            ElapsedMs = elapsed,
            Left156Rgba = FormatRgba(left156),
            Right156Rgba = FormatRgba(right156),
            MaxChannelDeltaAt156 = MaxDelta(left156, right156),
            AlphaDeltaAt156 = Math.Abs(right156[3] - left156[3]),
            Left138Rgba = FormatRgba(left138),
            Right138Rgba = FormatRgba(right138),
            MaxChannelDeltaAt138 = MaxDelta(left138, right138),
            AlphaDeltaAt138 = Math.Abs(right138[3] - left138[3])
        };
    }

    private static byte[] ReadBgra(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static string FormatRgba(byte[] bgra) =>
        string.Create(CultureInfo.InvariantCulture, $"({bgra[2]},{bgra[1]},{bgra[0]},A={bgra[3]})");

    private static int MaxDelta(byte[] a, byte[] b) =>
        Math.Max(Math.Max(Math.Abs(a[0] - b[0]), Math.Abs(a[1] - b[1])), Math.Max(Math.Abs(a[2] - b[2]), Math.Abs(a[3] - b[3])));

    private static RenderTargetBitmap CaptureComposite(Grid root)
    {
        var browser = RequireReadyBrowser(root);
        var window = Window.GetWindow(root)
            ?? throw new InvalidOperationException("Caption evidence requires a hosted comparison window.");
        using var stream = new MemoryStream();
        var capture = browser.CoreWebView2.CapturePreviewAsync(
            Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
        PumpUntil(() => capture.IsCompleted, window);
        capture.GetAwaiter().GetResult();
        Assert.True(stream.Length > 0, "The browser did not produce screenshot data.");
        stream.Position = 0;
        var browserFrame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.True(browserFrame.PixelWidth > 0 && browserFrame.PixelHeight > 0);
        var browserBounds = browser.TransformToAncestor(root).TransformBounds(new Rect(browser.RenderSize));
        Assert.True(browserBounds.Width > 0 && browserBounds.Height > 0, "The browser must occupy visible comparison space.");
        var wpfBitmap = RenderElement(root);

        var composition = new DrawingVisual();
        using (var context = composition.RenderOpen())
        {
            context.DrawImage(wpfBitmap, new Rect(root.RenderSize));
            context.DrawImage(browserFrame, browserBounds);
        }

        var combined = new RenderTargetBitmap(wpfBitmap.PixelWidth, wpfBitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        combined.Render(composition);
        return combined;
    }

    private static WebView2 RequireReadyBrowser(Grid root)
    {
        var editor = FindVisuals<CodeEditorView>(root).SingleOrDefault(view => view.IsVisible);
        if (editor is not { IsEditorReady: true, DataContext: DiffWindowViewModel owner }
            || !owner.EditorResultsCurrent || owner.EditorSession is not { } session
            || editor.RenderedVersion != session.Version
            || editor.FindName("Browser") is not WebView2 browser || browser.CoreWebView2 is null)
        {
            throw new InvalidOperationException("Caption evidence requires a ready text editor and its current rendered document; a WPF-only placeholder is not evidence.");
        }

        return browser;
    }

    private static void AssertBrowserDocumentAndScale(WebView2 browser, Window window,
        string basePath, string minePath, double readingScale)
    {
        var before = JsonSerializer.Serialize(File.ReadAllText(basePath).ReplaceLineEndings("\n"));
        var after = JsonSerializer.Serialize(File.ReadAllText(minePath).ReplaceLineEndings("\n"));
        var condition = FormattableString.Invariant(
            $"original.getValue()==={before}&&modified.getValue()==={after}&&[left,right].every(editor=>editor.getOption(monaco.editor.EditorOption.fontSize)==={15 * readingScale}&&editor.getOption(monaco.editor.EditorOption.lineHeight)==={24 * readingScale}&&editor.getVisibleRanges().length>0&&editor.getDomNode().querySelector('.view-line')?.textContent.includes('local'))");
        PumpUntil(() => ExecuteScript(browser, window, condition) == "true", window);
    }

    private static string ExecuteScript(WebView2 browser, Window window, string script)
    {
        var result = browser.ExecuteScriptAsync(script);
        PumpUntil(() => result.IsCompleted, window);
        return result.GetAwaiter().GetResult();
    }

    private static void SaveComposite(Grid root, string path) => SaveBitmap(CaptureComposite(root), path);

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteLuaFixtures(string basePath, string minePath)
    {
        var baseLines = new List<string>(60);
        var mineLines = new List<string>(60);
        for (var line = 1; line <= 60; line++)
        {
            var index = line.ToString("D3", CultureInfo.InvariantCulture);
            if (line == 45)
            {
                baseLines.Add($"local entry_{index} = 'BASE_LONG_LINE_用于验证窄窗与水平同步滚动_这是一段足够长的合成Lua文本内容_0123456789_ABCDEFGHIJKLMNOPQRSTUVWXYZ'");
                mineLines.Add($"local entry_{index} = 'MINE_LONG_LINE_用于验证窄窗与水平同步滚动_这是一段足够长的合成Lua文本内容_9876543210_ABCDEFGHIJKLMNOPQRSTUVWXYZ'");
                continue;
            }

            baseLines.Add(string.Create(CultureInfo.InvariantCulture, $"local entry_{index} = {line * 10}"));
            mineLines.Add(line switch
            {
                20 => $"local entry_{index} = 2000",
                35 => $"local entry_{index} = 3500",
                _ => string.Create(CultureInfo.InvariantCulture, $"local entry_{index} = {line * 10}")
            });
        }

        File.WriteAllLines(basePath, baseLines);
        File.WriteAllLines(minePath, mineLines);
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Differ.App", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = document.Root!.Element(presentation + "Application.Resources")!
            .Element(presentation + "ResourceDictionary")!;
        var context = new ParserContext
        {
            BaseUri = new Uri("pack://application:,,,/Differ.App;component/App.xaml", UriKind.Absolute)
        };
        context.XmlnsDictionary.Add(string.Empty, presentation.NamespaceName);
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        return Assert.IsType<ResourceDictionary>(XamlReader.Parse(resources.ToString(), context));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "DEVELOPMENT_SPEC.md")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Differ repository root was not found.");
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisuals<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void PumpUntil(Func<bool> completed, Window window)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!completed() && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            FlushLayout(window);
            Thread.Sleep(5);
        }

        Assert.True(completed(), "The comparison did not complete within 10 seconds.");
        FlushLayout(window);
    }

    private static void FlushLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    private sealed class DispatcherContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        public DispatcherContextScope() => SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }

    private sealed class ForegroundCaptionFactAttribute : FactAttribute
    {
        public ForegroundCaptionFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DIFFER_CAPTION_FOREGROUND_TESTS") != "1")
                Skip = "Explicit desktop check: set DIFFER_CAPTION_FOREGROUND_TESTS=1 to exercise real maximize/minimize commands.";
        }
    }

    private sealed class CaptionEvidenceFactAttribute : FactAttribute
    {
        public CaptionEvidenceFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DIFFER_CAPTION_EVIDENCE_DIR"))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DIFFER_CAPTION_EVIDENCE_STAGE")))
                Skip = "Optional image evidence: set DIFFER_CAPTION_EVIDENCE_DIR and DIFFER_CAPTION_EVIDENCE_STAGE.";
        }
    }
}
