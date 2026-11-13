using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

[Collection("UI performance")]
public sealed class CaptionGestureRuntimeTests
{
    private const uint WmActivate = 0x0006;
    private const uint WmCancelMode = 0x001F;
    private const uint WmNcMouseLeave = 0x02A2;
    private const uint WmNcMouseMove = 0x00A0;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmNcLButtonUp = 0x00A2;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonUp = 0x0202;
    private const int WmSysCommand = 0x0112;
    private const int ScClose = 0xF060;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedPressCanLeaveAndReenterBeforeOneReleaseCommand(bool nonClientRelease)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.PressClose();
            Assert.Equal(fixture.Handle, GetCapture());
            Assert.True(DiffWindow.GetIsCaptionPressed(fixture.CloseButton));

            fixture.Send(WmNcMouseLeave);
            Assert.False(DiffWindow.GetIsCaptionHovered(fixture.CloseButton));
            Assert.False(DiffWindow.GetIsCaptionPressed(fixture.CloseButton));
            Assert.Equal(fixture.Handle, GetCapture());

            fixture.MoveClient(fixture.BodyPoint);
            Assert.False(DiffWindow.GetIsCaptionPressed(fixture.CloseButton));
            fixture.MoveClient(fixture.ClosePoint);
            Assert.True(DiffWindow.GetIsCaptionHovered(fixture.CloseButton));
            Assert.True(DiffWindow.GetIsCaptionPressed(fixture.CloseButton));

            if (nonClientRelease)
                fixture.Send(WmNcLButtonUp, DiffWindow.HtClose, Pack(fixture.ClosePoint));
            else
                fixture.ReleaseClient(fixture.ClosePoint);
            fixture.Flush();

            var command = Assert.Single(fixture.Commands);
            Assert.Equal(ScClose, command.Code);
            Assert.False(command.PressedWhenInvoked);
            Assert.NotEqual(fixture.Handle, command.CaptureWhenInvoked);
            Assert.NotEqual(fixture.Handle, GetCapture());
            Assert.True(fixture.Window.IsVisible);

            // A duplicate release cannot trigger a second native window command.
            fixture.ReleaseClient(fixture.ClosePoint);
            fixture.Send(WmNcLButtonUp, DiffWindow.HtClose, Pack(fixture.ClosePoint));
            fixture.Flush();
            Assert.Single(fixture.Commands);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasingOutsideThePressedButtonDoesNotInvokeAnotherCommand(bool releaseOverAnotherButton)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.PressClose();
            var releasePoint = releaseOverAnotherButton ? fixture.MaximizePoint : fixture.BodyPoint;
            fixture.MoveClient(releasePoint);
            Assert.False(DiffWindow.GetIsCaptionPressed(fixture.CloseButton));
            Assert.False(DiffWindow.GetIsCaptionPressed(fixture.MaximizeButton));
            fixture.ReleaseClient(releasePoint);
            fixture.Flush();
            Assert.Empty(fixture.Commands);
            Assert.NotEqual(fixture.Handle, GetCapture());
            Assert.Equal(WindowState.Normal, fixture.Window.WindowState);
        });
    }

    [Theory]
    [InlineData(WmCancelMode)]
    [InlineData(WmActivate)]
    public void CancellationOrDeactivationDisarmsTheOriginalPress(uint cancellationMessage)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.PressClose();
            fixture.Send(cancellationMessage);
            Assert.False(DiffWindow.GetIsCaptionPressed(fixture.CloseButton));
            Assert.NotEqual(fixture.Handle, GetCapture());
            fixture.Send(WmNcMouseMove, DiffWindow.HtClose, Pack(fixture.ClosePoint));
            fixture.Send(WmNcLButtonUp, DiffWindow.HtClose, Pack(fixture.ClosePoint));
            fixture.Flush();
            Assert.Empty(fixture.Commands);
        });
    }

    [Fact]
    public void CaptureLossDoesNotReleaseTheNewOwnersCaptureOrInvokeTheStalePress()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            using var recipient = new HwndSource(new HwndSourceParameters("Differ capture recipient")
            {
                Width = 1,
                Height = 1,
                PositionX = -16000,
                PositionY = -16000,
                WindowStyle = unchecked((int)0x80000000)
            });
            try
            {
                fixture.PressClose();
                _ = SetCapture(recipient.Handle);
                Assert.Equal(recipient.Handle, GetCapture());
                Assert.False(DiffWindow.GetIsCaptionPressed(fixture.CloseButton));
                fixture.Send(WmNcLButtonUp, DiffWindow.HtClose, Pack(fixture.ClosePoint));
                fixture.Flush();
                Assert.Empty(fixture.Commands);
                Assert.Equal(recipient.Handle, GetCapture());
            }
            finally
            {
                if (GetCapture() == recipient.Handle) _ = ReleaseCapture();
            }
        });
    }

    [Fact]
    public void EscapeCancelsTheGestureWithoutClosingTheWindow()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.PressClose();
            var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(fixture.Window));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            fixture.Window.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.NotEqual(fixture.Handle, GetCapture());
            Assert.False(DiffWindow.GetIsCaptionPressed(fixture.CloseButton));
            fixture.ReleaseClient(fixture.ClosePoint);
            fixture.Flush();
            Assert.Empty(fixture.Commands);
            Assert.True(fixture.Window.IsVisible);
        });
    }

    [Fact]
    public void ClosingTheWindowReleasesAnyCaptionCapture()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.PressClose();
            Assert.Equal(fixture.Handle, GetCapture());
            fixture.Window.Close();
            fixture.Flush();
            Assert.False(fixture.Window.IsVisible);
            Assert.NotEqual(fixture.Handle, GetCapture());
            Assert.False(DiffWindow.GetIsCaptionPressed(fixture.CloseButton));
        });
    }

    private sealed record CommandRecord(int Code, nint CaptureWhenInvoked, bool PressedWhenInvoked);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.CaptionGesture.Tests", Guid.NewGuid().ToString("N"));
        private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;
        private readonly UserPreferencesService _preferences;
        private readonly ComparisonLoadScheduler _scheduler;
        private readonly ComparisonWorkspaceViewModel _workspace;
        private readonly ThemeService _themes;
        private readonly HwndSource _source;

        public Fixture()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Directory.CreateDirectory(_directory);
            _preferences = new UserPreferencesService(Path.Combine(_directory, "preferences.json"));
            _scheduler = new ComparisonLoadScheduler();
            _workspace = new ComparisonWorkspaceViewModel(new DiffCoordinator([]), _scheduler,
                new ExternalDiffSessionStore(Path.Combine(_directory, "sessions")), NullLoggerFactory.Instance, _preferences);
            var resources = LoadApplicationResources();
            _themes = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
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
            Window.Resources.MergedDictionaries.Add(resources);
            Window.Show();
            Flush();
            Handle = new WindowInteropHelper(Window).Handle;
            _source = Assert.IsType<HwndSource>(HwndSource.FromHwnd(Handle));
            _source.AddHook(RecordSystemCommand);
            CloseButton = Assert.IsType<Button>(Window.FindName("CloseCaptionButton"));
            MaximizeButton = Assert.IsType<Button>(Window.FindName("MaximizeRestoreCaptionButton"));
        }

        public DiffWindow Window { get; }
        public nint Handle { get; }
        public Button CloseButton { get; }
        public Button MaximizeButton { get; }
        public Point ClosePoint => CloseButton.PointToScreen(new Point(CloseButton.ActualWidth / 2, CloseButton.ActualHeight / 2));
        public Point MaximizePoint => MaximizeButton.PointToScreen(new Point(MaximizeButton.ActualWidth / 2, MaximizeButton.ActualHeight / 2));
        public Point BodyPoint => Window.PointToScreen(new Point(150, 120));
        public List<CommandRecord> Commands { get; } = [];

        public void PressClose() => Send(WmNcLButtonDown, DiffWindow.HtClose, Pack(ClosePoint));

        public void MoveClient(Point screenPoint) => Send(WmMouseMove, 1, ClientCoordinates(screenPoint));

        public void ReleaseClient(Point screenPoint) => Send(WmLButtonUp, 0, ClientCoordinates(screenPoint));

        public void Send(uint message, nint wParam = 0, nint lParam = 0) => _ = SendMessageW(Handle, message, wParam, lParam);

        private nint ClientCoordinates(Point screenPoint)
        {
            var point = new NativePoint { X = (int)Math.Round(screenPoint.X), Y = (int)Math.Round(screenPoint.Y) };
            Assert.True(ScreenToClient(Handle, ref point));
            return Pack(new Point(point.X, point.Y));
        }

        private nint RecordSystemCommand(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == WmSysCommand)
            {
                Commands.Add(new CommandRecord((int)(wParam.ToInt64() & 0xFFF0), GetCapture(), DiffWindow.GetIsCaptionPressed(CloseButton)));
                // Keep the hidden test window hidden; the parent task exercises real window actions in the foreground.
                handled = true;
            }
            return 0;
        }

        public void Flush()
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
            if (GetCapture() == Handle) _ = ReleaseCapture();
            if (!_source.IsDisposed) _source.RemoveHook(RecordSystemCommand);
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

    private static nint Pack(Point point) => ((int)Math.Round(point.X) & 0xFFFF) | (((int)Math.Round(point.Y) & 0xFFFF) << 16);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetCapture();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ReleaseCapture();
}
