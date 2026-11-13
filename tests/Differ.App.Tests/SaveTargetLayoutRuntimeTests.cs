using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
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
public sealed class SaveTargetLayoutRuntimeTests
{
    private static readonly double[] WindowWidths = [1500.0, 720.0];
    private static readonly double[] ReadingScales = [1.0, 1.5];
    private static readonly string[] EditorCommandNames = ["EditModeToggle", "UndoButton", "RedoButton", "SaveButton", "SaveMenuButton"];

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void SavedTargetRemainsRecognizableWithExpandedEditingToolsAcrossLayouts(AppTheme theme)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var directory = Path.Combine(Path.GetTempPath(), "Differ.SaveTarget.Layout", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var basePath = Path.Combine(directory, "base.cs");
            var minePath = Path.Combine(directory, "原始来源.cs");
            var targetPath = Path.Combine(directory, "另存目标_这是一个需要截断但必须能辨认的中文长文件名称.cs");
            File.WriteAllText(basePath, "class Example { int Count => 10; }\n");
            File.WriteAllText(minePath, "class Example { int Count => 20; }\n");
            using var preferences = new UserPreferencesService(Path.Combine(directory, "preferences.json"));
            using var scheduler = new ComparisonLoadScheduler();
            var store = new ExternalDiffSessionStore(Path.Combine(directory, "sessions"));
            using var workspace = new ComparisonWorkspaceViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]),
                scheduler, store, NullLoggerFactory.Instance, preferences);
            var resources = LoadApplicationResources();
            using var themes = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(theme));
            themes.InitializeFromSystem();
            var window = new DiffWindow(workspace, themes)
            {
                Width = 1500,
                Height = 860,
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
            CodeEditorView? editorView = null;
            try
            {
                var creating = store.CreateAsync(new ExternalDiffRequest
                {
                    BaseFilePath = basePath,
                    MineFilePath = minePath,
                    MineDisplayName = "原始来源标题",
                    InvocationSource = ExternalDiffRequest.LocalFileSource
                }, CancellationToken.None);
                Wait(creating, window);
                var owner = workspace.AddSession(creating.Result);
                window.Show();
                PumpUntil(() => !owner.IsBusy && owner.EditorResultsCurrent, window);
                Assert.False(owner.HasError, owner.ErrorText);
                editorView = Assert.Single(FindVisuals<CodeEditorView>(window), view => view.IsVisible);
                PumpUntil(() => editorView.IsEditorReady && editorView.RenderedVersion == owner.EditorSession!.Version, window);
                Assert.Equal(minePath, owner.EditorSession!.SaveTarget);
                Wait(owner.SetEditModeAsync(true), window);
                Wait(owner.SaveEditorAsync(targetPath), window);
                Assert.Equal(targetPath, owner.EditorSession.SaveTarget);
                Assert.True(owner.IsEditMode);
                var browser = Assert.IsType<WebView2>(editorView.FindName("Browser"));

                foreach (var width in WindowWidths)
                {
                    window.Width = width;
                    window.Height = width == 720 ? 620 : 860;
                    foreach (var readingScale in ReadingScales)
                    {
                        preferences.ReadingScale = readingScale;
                        FlushLayout(window);
                        PumpUntil(() =>
                        {
                            var check = browser.ExecuteScriptAsync(FormattableString.Invariant(
                                $"[left,right].every(editor=>editor.getOption(monaco.editor.EditorOption.fontSize)==={15 * readingScale})"));
                            Wait(check, window);
                            return check.Result == "true";
                        }, window);
                        AssertSavedTargetLayout(window, editorView, owner, targetPath, minePath,
                            $"{theme}, width={width}, readingScale={readingScale}");
                    }
                }
                Assert.Equal(File.ReadAllText(minePath), File.ReadAllText(targetPath));
            }
            finally
            {
                window.Close();
                PumpUntil(() => !window.IsVisible, window);
                workspace.Dispose();
                if (editorView is not null) Assert.True(editorView.IsEditorDisposed);
                SynchronizationContext.SetSynchronizationContext(previousContext);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    private static void AssertSavedTargetLayout(Window window, CodeEditorView editor, DiffWindowViewModel owner,
        string targetPath, string sourcePath, string scenario)
    {
        var header = Assert.IsType<Grid>(editor.FindName("MinePaneHeader"));
        var headers = Assert.IsType<Border>(editor.FindName("EditorPaneHeaders"));
        var target = Assert.IsType<TextBlock>(editor.FindName("MineSaveTarget"));
        var status = Assert.IsType<TextBlock>(editor.FindName("MineSaveStatus"));
        var compact = Assert.IsType<DiffWindow>(window).IsCompactToolbar;
        Assert.Equal(compact ? CodeReviewStrings.ResultSide : owner.EditorMineIdentity,
            Assert.IsType<TextBlock>(editor.FindName("MinePaneRole")).Text);
        var editToggle = Assert.IsType<ToggleButton>(editor.FindName("EditModeToggle"));
        Assert.Equal(!compact, Assert.Single(FindVisuals<Differ.UI.Controls.AppIcon>(editToggle)).IsVisible);
        var dpi = VisualTreeHelper.GetDpi(target);
        var pixelTolerance = 1 / dpi.DpiScaleY + 0.0001;
        Assert.InRange(headers.ActualHeight, 34 - pixelTolerance, 34 + pixelTolerance);
        Assert.True(target.IsVisible, $"Saved target is hidden: {scenario}");
        Assert.Equal(Path.GetFileName(targetPath), target.Text);
        Assert.Equal(owner.EditorSaveTargetName, target.Text);
        // Enough space for the first two Chinese characters and the actual ellipsis,
        // rather than accepting a nonzero slot that still shows no useful name.
        var minimumName = new FormattedText(target.Text[..2] + "…", CultureInfo.CurrentCulture,
            target.FlowDirection, new Typeface(target.FontFamily, target.FontStyle, target.FontWeight, target.FontStretch),
            target.FontSize, Brushes.Black, dpi.PixelsPerDip).WidthIncludingTrailingWhitespace;
        Assert.True(target.ActualWidth >= minimumName,
            $"Saved target has {target.ActualWidth:0.00} DIP; needs {minimumName:0.00} DIP: {scenario}");
        Assert.True(status.ActualWidth > 0 && status.IsVisible, $"Save state disappeared: {scenario}");
        var hint = Assert.IsType<string>(target.ToolTip);
        Assert.Equal(owner.EditorMinePathHint, hint);
        Assert.Contains(targetPath, hint, StringComparison.Ordinal);
        Assert.Contains(sourcePath, hint, StringComparison.Ordinal);
        Assert.Contains(CodeReviewStrings.RefreshUsesSource, hint, StringComparison.Ordinal);
        var refresh = Assert.IsType<Button>(window.FindName("RefreshMineButton"));
        Assert.Equal(owner.EditorRefreshHint, refresh.ToolTip);
        Assert.Contains(sourcePath, Assert.IsType<string>(refresh.ToolTip), StringComparison.Ordinal);
        Assert.Contains(CodeReviewStrings.RefreshUsesSource, Assert.IsType<string>(refresh.ToolTip), StringComparison.Ordinal);

        var targetBounds = target.TransformToAncestor(header).TransformBounds(new Rect(target.RenderSize));
        Assert.InRange(targetBounds.Left, -1, header.ActualWidth);
        Assert.InRange(targetBounds.Right, 0, header.ActualWidth + 1);
        foreach (var name in EditorCommandNames)
        {
            var command = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName(name));
            Assert.True(command.IsVisible, $"Editor command {name} is hidden: {scenario}");
            var commandBounds = command.TransformToAncestor(header).TransformBounds(new Rect(command.RenderSize));
            Assert.InRange(commandBounds.Left, -1, header.ActualWidth);
            Assert.InRange(commandBounds.Right, 0, header.ActualWidth + 1);
            var overlap = Rect.Intersect(targetBounds, commandBounds);
            Assert.True(overlap.IsEmpty || overlap.Width < 0.5 || overlap.Height < 0.5,
                $"Saved target overlaps {name}: {scenario}");
        }
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Differ.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "src", "Differ.App", "App.xaml"));
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

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisuals<T>(child)) yield return descendant;
        }
    }

    private static void Wait(Task task, Window window)
    {
        PumpUntil(() => task.IsCompleted, window);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> completed, Window window)
    {
        var watch = Stopwatch.StartNew();
        while (!completed() && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            FlushLayout(window);
            Thread.Sleep(5);
        }
        Assert.True(completed(), "Saved-target layout did not become ready within 15 seconds.");
        FlushLayout(window);
    }

    private static void FlushLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
}
