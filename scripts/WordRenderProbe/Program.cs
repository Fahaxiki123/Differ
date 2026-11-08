using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Differ.App;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Differ.Formats.Word;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var input = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var exitCode = 0;
        application.Dispatcher.BeginInvoke(async () =>
        {
            try { await RunAsync(root, input, input, output); }
            catch (Exception exception) { Console.Error.WriteLine(exception); exitCode = 1; }
            finally { application.Shutdown(); }
        });
        application.Run();
        return exitCode;
    }

    private static async Task RunAsync(string root, string before, string after, string output)
    {
        using var preferences = new UserPreferencesService(Path.Combine(output, "probe-preferences.json"));
        using var scheduler = new ComparisonLoadScheduler();
        var store = new ExternalDiffSessionStore(Path.Combine(output, "sessions"));
        using var workspace = new ComparisonWorkspaceViewModel(new DiffCoordinator([new WordDiffAdapter()]),
            scheduler, store, NullLoggerFactory.Instance, preferences);
        var resources = LoadResources(root);
        using var themes = new ThemeService(resources, Dispatcher.CurrentDispatcher, new Monitor());
        themes.InitializeFromSystem();
        var window = new DiffWindow(workspace, themes)
        {
            Width = 2048, Height = 1104, WindowStyle = WindowStyle.None,
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.Resources.MergedDictionaries.Add(resources);
        var owner = workspace.AddSession(await store.CreateAsync(new ExternalDiffRequest
        {
            BaseFilePath = before, MineFilePath = after, OriginalFileName = "F-商业化付费型.docx",
            BaseDisplayName = "F-商业化付费型.docx：BASE 只读副本",
            MineDisplayName = "F-商业化付费型.docx：当前只读副本"
        }, CancellationToken.None));
        window.Show();
        var watch = Stopwatch.StartNew();
        while (owner.IsBusy || owner.CurrentDocument is null)
        {
            if (owner.HasError) throw new InvalidOperationException(owner.ErrorText);
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Word comparison did not load.");
            await Task.Delay(10);
        }
        await Drain(window);
        var list = Visuals<ListBox>(window).Single(value => value.Name == "WordRows");
        var evidence = new List<object>();
        foreach (var width in new[] { 2048d, 960d })
        {
            window.Width = width;
            window.Height = width > 1000 ? 1104 : 780;
            await Drain(window);
            var minimumGap = double.PositiveInfinity;
            var samples = 0;
            foreach (var row in owner.WordDisplayRows)
            {
                list.ScrollIntoView(row);
                await Drain(window);
                foreach (var panel in Visuals<WordParagraphLayout>(list))
                {
                    if (panel.Children[0] is not TextBlock marker || string.IsNullOrWhiteSpace(marker.Text)) continue;
                    var body = panel.Children[1];
                    var markerBounds = marker.TransformToAncestor(panel).TransformBounds(new Rect(marker.RenderSize));
                    var bodyBounds = body.TransformToAncestor(panel).TransformBounds(new Rect(body.RenderSize));
                    var gap = bodyBounds.Left - markerBounds.Right;
                    if (gap < 5.5) throw new InvalidOperationException($"Rendered list overlaps: {markerBounds} / {bodyBounds}");
                    minimumGap = Math.Min(minimumGap, gap);
                    samples++;
                }
            }
            var scroll = Visuals<ScrollViewer>(list).First();
            scroll.ScrollToTop();
            await Drain(window);
            var image = new RenderTargetBitmap((int)(window.ActualWidth * 1.25), (int)(window.ActualHeight * 1.25),
                120, 120, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            var name = width > 1000 ? "word-real-wide.png" : "word-real-narrow.png";
            using (var file = File.Create(Path.Combine(output, name))) encoder.Save(file);
            evidence.Add(new { WidthDip = width, HeightDip = window.ActualHeight, RenderDpi = 120,
                ObservedListLayouts = samples, MinimumMarkerBodyGapDip = minimumGap, Screenshot = name });
        }
        File.WriteAllText(Path.Combine(output, "word-real-layout.json"), JsonSerializer.Serialize(new
        {
            SourceFile = "F-商业化付费型.docx (read-only copy)",
            owner.DifferenceCount, Rows = owner.WordDisplayRows.Count, Layouts = evidence,
            Method = "Actual adapter and DiffWindow/WordDiffView; offscreen Windows WPF. Raster DPI is 125%; this is not an OS DPI setting change."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(evidence));
        window.Close();
    }

    private static Task Drain(Window window) => Dispatcher.CurrentDispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle).Task;
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }

    private static ResourceDictionary LoadResources(string root)
    {
        var document = XDocument.Load(Path.Combine(root, "src/Differ.App/App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resource = document.Root!.Element(ns + "Application.Resources")!.Element(ns + "ResourceDictionary")!;
        var parser = new ParserContext { BaseUri = new Uri("pack://application:,,,/Differ.App;component/App.xaml") };
        parser.XmlnsDictionary.Add(string.Empty, ns.NamespaceName);
        parser.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        return (ResourceDictionary)XamlReader.Parse(resource.ToString(), parser);
    }
    private sealed class Monitor : ISystemThemeMonitor
    {
        public AppTheme CurrentTheme => AppTheme.Light;
        public event EventHandler? ThemeChanged { add { } remove { } }
    }
}
