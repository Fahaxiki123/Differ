using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

[Collection("UI performance")]
public sealed class ThemeWindowClockTests
{
    private static readonly double[] InvalidBrowserElapsed = [double.NaN, -1, double.PositiveInfinity, 681];

    [Fact]
    public void LocalClockAdvancesWithoutBrowserReportsAndReleasesAtCompletion()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            var initial = fixture.Colour;
            fixture.Begin(17);
            var target = fixture.TargetColour;
            Assert.True(fixture.ClockRunning);
            Assert.Equal(-1, fixture.BrowserElapsed);

            fixture.Render(200);

            Assert.Equal(200, fixture.LocalElapsed);
            Assert.NotEqual(initial, fixture.Colour);
            Assert.NotEqual(target, fixture.Colour);
            Assert.True(fixture.FogOpacity > 0);
            Assert.Equal(-1, fixture.BrowserElapsed);

            fixture.Render(680);

            fixture.AssertStopped();
            Assert.Equal(target, fixture.Colour);
            fixture.Render(120);
            Assert.Equal(target, fixture.Colour);
        });
    }

    [Fact]
    public void BrowserReportsOnlySuperviseTheCurrentTransition()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Begin(51);
            var target = fixture.TargetColour;
            fixture.Render(100);
            var displayed = fixture.Colour;
            var opacity = fixture.FogOpacity;

            fixture.Report(50, 250);
            Assert.Equal(-1, fixture.BrowserElapsed);
            fixture.Report(51, 250);
            Assert.Equal(250, fixture.BrowserElapsed);
            fixture.Report(51, 200);
            foreach (var elapsed in InvalidBrowserElapsed) fixture.Report(51, elapsed);
            fixture.Report(50, 680);

            Assert.True(fixture.ClockRunning);
            Assert.Equal(250, fixture.BrowserElapsed);
            Assert.Equal(100, fixture.LocalElapsed);
            Assert.Equal(displayed, fixture.Colour);
            Assert.Equal(opacity, fixture.FogOpacity);

            fixture.Report(51, 680);
            fixture.AssertStopped();
            Assert.Equal(target, fixture.Colour);
        });
    }

    [Fact]
    public void ReversalPreservesTheLastLocalColourAndClosingReleasesTheClock()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Begin(70);
            fixture.Render(200);
            var displayed = fixture.Colour;

            fixture.Stop(completePalette: false);
            fixture.AssertStopped();
            Assert.Equal(displayed, fixture.Colour);
            fixture.Begin(71, expectedInitial: displayed);
            fixture.Render(150);
            var target = fixture.TargetColour;
            fixture.Stop(completePalette: true);
            fixture.AssertStopped();
            Assert.Equal(target, fixture.Colour);

            fixture.Begin(72);
            target = fixture.TargetColour;
            fixture.Render(100);
            fixture.Window.Close();
            fixture.AssertStopped();
            Assert.False(fixture.Window.IsVisible);
            Assert.Equal(target, fixture.Colour);
            fixture.Report(72, 300);
            fixture.Render(300);
            Assert.Equal(target, fixture.Colour);
        });
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly MethodInfo ReadyMethod = RequireWindowMethod("OnThemeTransitionReady", [typeof(long)]);
        private static readonly MethodInfo RenderMethod = RequireWindowMethod("RenderThemeClockFrame", [typeof(double)]);
        private static readonly MethodInfo ReportMethod = RequireWindowMethod("OnEditorThemeFrame", [typeof(long), typeof(double)]);
        private static readonly MethodInfo StopMethod = RequireWindowMethod("StopThemeMist", [typeof(bool)]);
        private static readonly MethodInfo DisposeEditorMethod = typeof(CodeEditorView).GetMethod("OnEditorDisposed", PrivateInstance)!;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.ThemeWindowClock.Tests", Guid.NewGuid().ToString("N"));
        private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;
        private readonly UserPreferencesService _preferences;
        private readonly ComparisonLoadScheduler _scheduler;
        private readonly ComparisonWorkspaceViewModel _workspace;
        private readonly ResourceDictionary _resources;
        private readonly ThemeService _themes;
        private readonly CodeEditorView _editor = new();
        private readonly ThemeMistOverlay _fog;

        public Fixture()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Directory.CreateDirectory(_directory);
            _preferences = new UserPreferencesService(Path.Combine(_directory, "preferences.json"));
            _scheduler = new ComparisonLoadScheduler();
            _workspace = new ComparisonWorkspaceViewModel(new DiffCoordinator([]), _scheduler,
                new ExternalDiffSessionStore(Path.Combine(_directory, "sessions")), NullLoggerFactory.Instance, _preferences);
            _resources = LoadApplicationResources();
            _themes = ThemeTestSupport.CreateService(_resources, new FakeSystemThemeMonitor(AppTheme.Light));
            _themes.InitializeFromSystem();
            Window = new DiffWindow(_workspace, _themes)
            {
                Width = 720,
                Height = 620,
                Left = -16000,
                Top = -16000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            Window.Resources.MergedDictionaries.Add(_resources);
            Window.Show();
            Flush();
            _fog = Assert.IsType<ThemeMistOverlay>(Window.FindName("ThemeMist"));
        }

        public DiffWindow Window { get; }
        public Color Colour => ThemeTestSupport.ResolveColour(_resources, "Dui.Surface.Content");
        public Color TargetColour => ThemeTestSupport.ResolveColour(
            Assert.IsType<ResourceDictionary>(_resources[ThemeService.TargetThemeKey]), "Dui.Surface.Content");
        public bool ClockRunning => Assert.IsType<bool>(Read("_themeClockRunning"));
        public double LocalElapsed => Assert.IsType<double>(Read("_lastThemeElapsed"));
        public double BrowserElapsed => Assert.IsType<double>(Read("_lastBrowserThemeElapsed"));
        public double FogOpacity => Assert.IsType<DrawingVisual>(VisualTreeHelper.GetChild(_fog, 0)).Opacity;

        public void Begin(long id, Color? expectedInitial = null)
        {
            var root = Assert.IsAssignableFrom<FrameworkElement>(Window.FindName("ComparisonRoot"));
            var target = _themes.CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
            // The unloaded editor intentionally emits no browser messages. This fixture tests the host after ready,
            // not WebView2 startup or the final desktop composition of the two independent rendering surfaces.
            Write("_mistEditor", _editor);
            Write("_themeTransitionId", id);
            Write("_mistTheme", target);
            Write("_themeOrigin", new Point(root.ActualWidth - 78, 58));
            Write("_themeArea", root.RenderSize);
            Write("_lastThemeElapsed", -1d);
            Write("_lastBrowserThemeElapsed", -1d);
            _themes.PrepareToggle();
            if (expectedInitial is { } initial) Assert.Equal(initial, Colour);
            ReadyMethod.Invoke(Window, [id]);
        }

        public void Render(double elapsed) => RenderMethod.Invoke(Window, [elapsed]);
        public void Report(long id, double elapsed) => ReportMethod.Invoke(Window, [id, elapsed]);
        public void Stop(bool completePalette) => StopMethod.Invoke(Window, [completePalette]);

        public void AssertStopped()
        {
            Assert.False(ClockRunning);
            Assert.Null(Read("_mistEditor"));
            Assert.Equal(0, FogOpacity);
        }

        private object? Read(string name) => typeof(DiffWindow).GetField(name, PrivateInstance)!.GetValue(Window);
        private void Write(string name, object value) => typeof(DiffWindow).GetField(name, PrivateInstance)!.SetValue(Window, value);
        private static MethodInfo RequireWindowMethod(string name, Type[] types) =>
            typeof(DiffWindow).GetMethod(name, PrivateInstance, binder: null, types, modifiers: null)
            ?? throw new InvalidOperationException($"Required theme clock method not found: {name}");

        private void Flush()
        {
            Window.UpdateLayout();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            if (Window.IsVisible) Window.Close();
            DisposeEditorMethod.Invoke(_editor, [null, EventArgs.Empty]);
            _workspace.Dispose();
            _themes.Dispose();
            _scheduler.Dispose();
            _preferences.Dispose();
            SynchronizationContext.SetSynchronizationContext(_previousContext);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Differ.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "src", "Differ.App", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = document.Root!.Element(presentation + "Application.Resources")!.Element(presentation + "ResourceDictionary")!;
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/Differ.App;component/App.xaml", UriKind.Absolute) };
        context.XmlnsDictionary.Add(string.Empty, presentation.NamespaceName);
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        return Assert.IsType<ResourceDictionary>(XamlReader.Parse(resources.ToString(), context));
    }
}
