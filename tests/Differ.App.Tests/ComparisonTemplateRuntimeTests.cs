using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Differ.App.Controls;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.Abstractions;
using Differ.Core.External;
using Differ.Core.Models;
using Differ.Core.Services;
using Differ.Formats.Binary;
using Differ.Formats.Excel;
using Differ.Formats.Text;
using Differ.Formats.Word;
using Differ.UI.Controls;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging.Abstractions;
using Spreadsheet = DocumentFormat.OpenXml.Spreadsheet;
using Word = DocumentFormat.OpenXml.Wordprocessing;

namespace Differ.App.Tests;

[Collection("UI performance")]
public sealed class ComparisonTemplateRuntimeTests
{
    private static readonly string[] ModeButtonNames = ["DisplayAllButton", "DisplayDifferencesButton", "DisplayContextButton"];
    private static readonly string[] EditorActionNames = ["SaveButton", "SaveMenuButton", "UndoButton", "RedoButton", "EditModeToggle"];
    private static readonly DiffDisplayMode[] Modes = [DiffDisplayMode.All, DiffDisplayMode.DifferencesOnly, DiffDisplayMode.Context];
    [Theory]
    [InlineData(".cs", AppTheme.Light)]
    [InlineData(".cs", AppTheme.Dark)]
    [InlineData(".xlsx", AppTheme.Light)]
    [InlineData(".xlsx", AppTheme.Dark)]
    [InlineData(".docx", AppTheme.Light)]
    [InlineData(".docx", AppTheme.Dark)]
    public void ReadingLayoutsKeepContentAccessibleAcrossWindowSizesAndTabs(string extension, AppTheme theme)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var dispatcherContext = new DispatcherContextScope();
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Differ.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var basePath = Path.Combine(temporaryDirectory, "BASE" + extension);
            var minePath = Path.Combine(temporaryDirectory, "当前工作副本" + extension);
            WriteReadingFixture(basePath, extension, changed: false);
            WriteReadingFixture(minePath, extension, changed: true);
            IFileDiffAdapter adapter = extension switch
            {
                ".xlsx" => new ExcelDiffAdapter(),
                ".docx" => new WordDiffAdapter(),
                _ => new TextDiffAdapter(new TextFileReader())
            };
            using var preferences = new UserPreferencesService(Path.Combine(temporaryDirectory, "preferences.json"));
            using var scheduler = new ComparisonLoadScheduler();
            using var workspace = new ComparisonWorkspaceViewModel(new DiffCoordinator([adapter]), scheduler,
                new ExternalDiffSessionStore(Path.Combine(temporaryDirectory, "sessions")),
                NullLoggerFactory.Instance, preferences);
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
            ExternalDiffSession CreateSession(string name) => new()
            {
                SessionId = Guid.NewGuid(),
                BaseSnapshotPath = basePath,
                MineSnapshotPath = minePath,
                Request = new ExternalDiffRequest
                {
                    BaseFilePath = basePath,
                    MineFilePath = minePath,
                    OriginalFileName = name + extension,
                    BaseDisplayName = name + "（BASE）",
                    MineDisplayName = name + "（当前工作副本）",
                    BaseRevision = "123456"
                }
            };
            var viewModel = workspace.AddSession(CreateSession("提交前审阅_中文长文件名称_完整内容与双栏对齐验证"));
            try
            {
                window.Show();
                PumpUntil(() => !viewModel.IsBusy && viewModel.DifferenceCount > 0, window);
                Assert.False(viewModel.HasError, viewModel.ErrorText);
                var root = Assert.IsType<Grid>(window.Content);
                AssertToolbarFits(root, viewModel);
                var content = FindActiveFormatView(window);
                Assert.Equal(theme.ToString(), content.TryFindResource("Theme.Name"));
                AssertReadingContent(window, content, viewModel);
                AssertAdvancedRulesAccessible(window, viewModel);
                if (content is CodeEditorView)
                {
                    viewModel.MoveEditorDifference(1);
                    PumpUntil(() => viewModel.EditorLocationText.Contains("BASE", StringComparison.Ordinal), window);
                }
                RenderSimulatedDpiEvidence(root, "reading-" + extension.TrimStart('.'), theme, [1.0]);

                window.Width = 960;
                window.Height = 680;
                FlushLayout(window);
                AssertToolbarFits(root, viewModel);
                AssertReadingContent(window, content, viewModel);
                RenderSimulatedDpiEvidence(root, "reading-" + extension.TrimStart('.'), theme);

                window.Width = 720;
                FlushLayout(window);
                AssertToolbarFits(root, viewModel);
                AssertReadingContent(window, content, viewModel);
                RenderSimulatedDpiEvidence(root, "reading-" + extension.TrimStart('.'), theme, [1.0]);
                if (content is CodeEditorView codeEditor)
                {
                    AssertLocalPanelsStayOutsideBrowser(window, codeEditor, viewModel);
                    preferences.ReadingScale = 1.5;
                    FlushLayout(window);
                    AssertToolbarFits(root, viewModel);
                    AssertReadingContent(window, content, viewModel);
                    AssertEditorScale(window, codeEditor, 22.5, 36);
                    preferences.ReadingScale = 1;
                    FlushLayout(window);
                    AssertEditorScale(window, codeEditor, 15, 24);
                }
                window.Width = 960;
                FlushLayout(window);

                if (content is not (WordDiffView or CodeEditorView))
                {
                    AssertAccessibleDetails(window, content, viewModel);
                }
                if (viewModel.IsSpreadsheetView)
                {
                    var worksheet = viewModel.SelectedWorksheet;
                    Assert.Equal(2, viewModel.WorksheetTabs.Count);
                    viewModel.SelectedWorksheet = viewModel.WorksheetTabs[1];
                    FlushLayout(window);
                    Assert.NotSame(content, FindActiveFormatView(window));
                    Assert.False(Assert.IsType<ToggleButton>(FindActiveFormatView(window)
                        .FindName("DetailsToggle")).IsChecked);
                    viewModel.SelectedWorksheet = worksheet;
                    FlushLayout(window);
                    Assert.Same(content, FindActiveFormatView(window));
                }

                if (viewModel.IsEditableText)
                {
                    Assert.False(viewModel.IsEditMode);
                    var enabling = viewModel.SetEditModeAsync(true);
                    PumpUntil(() => enabling.IsCompleted, window);
                    enabling.GetAwaiter().GetResult();
                    RenderSimulatedDpiEvidence(root, "editing-cs", theme, [1.0]);
                }
                var second = workspace.AddSession(CreateSession("另一个中文长文件名称_文件标签切换验证"));
                PumpUntil(() => !second.IsBusy && second.DifferenceCount > 0, window);
                Assert.Equal(2, workspace.Tabs.Count);
                if (viewModel.IsEditableText)
                {
                    Assert.False(second.IsEditMode);
                    Assert.True(viewModel.IsEditMode);
                }
                AssertToolbarFits(root, second);
                workspace.SelectedTab = viewModel;
                FlushLayout(window);
                Assert.Same(content, FindActiveFormatView(window));
                AssertToolbarFits(root, viewModel);
                if (viewModel.IsEditableText) Assert.True(viewModel.IsEditMode);
                if (content is not (WordDiffView or CodeEditorView))
                {
                    Assert.False(Assert.IsType<ToggleButton>(content.FindName("DetailsToggle")).IsChecked);
                    Assert.False(Assert.IsType<Grid>(content.FindName("DetailsContent")).IsVisible);
                }
            }
            finally
            {
                window.Close();
                PumpUntil(() => !window.IsVisible, window);
                workspace.Dispose();
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        });
    }

    [Theory]
    [InlineData(".xlsx", AppTheme.Light, true)]
    [InlineData(".xlsx", AppTheme.Dark, true)]
    [InlineData(".docx", AppTheme.Light, true)]
    [InlineData(".docx", AppTheme.Dark, true)]
    [InlineData(".xlsx", AppTheme.Light, false)]
    [InlineData(".xlsx", AppTheme.Dark, false)]
    [InlineData(".docx", AppTheme.Light, false)]
    [InlineData(".docx", AppTheme.Dark, false)]
    public void OfficeFallbackChangesTemplateOnSameViewModelAndPreservesTheme(
        string extension,
        AppTheme initialTheme,
        bool hasZipHeader)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var dispatcherContext = new DispatcherContextScope();
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Differ.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var originalDirectory = Path.Combine(temporaryDirectory, "inputs");
            Directory.CreateDirectory(originalDirectory);
            var basePath = Path.Combine(originalDirectory, "BASE" + extension);
            var minePath = Path.Combine(originalDirectory, "当前文件" + extension);
            // Invalid ZIP payloads exercise the real Office adapters and the
            // coordinator's binary fallback, without touching user fixtures.
            File.WriteAllBytes(basePath, hasZipHeader
                ? [0x50, 0x4b, 0x03, 0x04, 0x41, 0x42, 0x43, 0x00]
                : [0x41, 0x42, 0x43, 0x00, 0x01, 0x02, 0x03, 0x04]);
            File.WriteAllBytes(minePath, hasZipHeader
                ? [0x50, 0x4b, 0x03, 0x04, 0x41, 0x46, 0x43, 0x00]
                : [0x41, 0x46, 0x43, 0x00, 0x01, 0x02, 0x03, 0x04]);

            IFileDiffAdapter office = extension == ".xlsx" ? new ExcelDiffAdapter() : new WordDiffAdapter();
            var gated = new GatedAdapter(office);
            using var scheduler = new ComparisonLoadScheduler();
            using var workspace = new ComparisonWorkspaceViewModel(
                new DiffCoordinator([gated, new BinaryDiffAdapter()]), scheduler,
                new ExternalDiffSessionStore(Path.Combine(temporaryDirectory, "sessions")),
                NullLoggerFactory.Instance, new UserPreferencesService(Path.Combine(temporaryDirectory, "preferences.json")));
            var themeResources = LoadApplicationResources();
            using var themes = ThemeTestSupport.CreateService(themeResources, new FakeSystemThemeMonitor(initialTheme));
            themes.InitializeFromSystem();
            var window = new DiffWindow(workspace, themes)
            {
                Width = 980,
                Height = 680,
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
            window.Resources.MergedDictionaries.Add(themeResources);
            var viewModel = workspace.AddSession(new ExternalDiffSession
            {
                SessionId = Guid.NewGuid(),
                BaseSnapshotPath = basePath,
                MineSnapshotPath = minePath,
                Request = new ExternalDiffRequest
                {
                    BaseFilePath = basePath,
                    MineFilePath = minePath,
                    OriginalFileName = "格式回退与窄窗口验证" + extension
                }
            });

            try
            {
                window.Show();
                FlushLayout(window);
                Assert.True(viewModel.IsBusy);
                var root = Assert.IsType<Grid>(window.Content);
                AssertToolbarFits(root, viewModel);
                if (extension == ".xlsx")
                {
                    Assert.Single(FindVisuals<SpreadsheetDiffView>(window));
                }
                else
                {
                    Assert.Single(FindVisuals<WordDiffView>(window));
                }

                gated.Release.TrySetResult();
                PumpUntil(() => !viewModel.IsBusy, window);

                Assert.Same(viewModel, workspace.SelectedTab);
                Assert.False(viewModel.HasError, viewModel.ErrorText);
                Assert.True(viewModel.IsTextView);
                Assert.NotEmpty(viewModel.TextRows);
                Assert.NotEmpty(viewModel.Warnings);
                Assert.Empty(FindVisuals<SpreadsheetDiffView>(window));
                Assert.Empty(FindVisuals<WordDiffView>(window));
                var textView = Assert.Single(FindVisuals<TextDiffView>(window));
                Assert.Same(viewModel, textView.DataContext);
                Assert.Equal(initialTheme.ToString(), textView.TryFindResource("Theme.Name"));
                var expectedBackground = ThemeTestSupport.ResolveColour(themeResources, "PanelBrush");
                Assert.Equal(expectedBackground, Assert.IsType<SolidColorBrush>(textView.Background).Color);
                Assert.All(FindVisuals<DataGrid>(textView), grid =>
                    Assert.Equal(expectedBackground, Assert.IsType<SolidColorBrush>(grid.Background).Color));

                AssertToolbarFits(root, viewModel);
                if (hasZipHeader)
                {
                    RenderSimulatedDpiEvidence(root, "fallback-" + extension.TrimStart('.'), initialTheme);
                }

                themes.Toggle();
                FlushLayout(window);
                Assert.Same(textView, Assert.Single(FindVisuals<TextDiffView>(window)));
                Assert.Equal(themes.CurrentTheme.ToString(), textView.TryFindResource("Theme.Name"));
                var changedBackground = ThemeTestSupport.ResolveColour(themeResources, "PanelBrush");
                Assert.NotEqual(expectedBackground, changedBackground);
                Assert.Equal(changedBackground, Assert.IsType<SolidColorBrush>(textView.Background).Color);
                Assert.All(FindVisuals<DataGrid>(textView), grid =>
                    Assert.Equal(changedBackground, Assert.IsType<SolidColorBrush>(grid.Background).Color));
                AssertToolbarFits(root, viewModel);
                AssertLocalPanelsAndZoom(window, root, workspace, viewModel, textView);
            }
            finally
            {
                gated.Release.TrySetResult();
                window.Close();
                workspace.Dispose();
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        });
    }

    private static void AssertToolbarFits(Grid root, DiffWindowViewModel viewModel)
    {
        AssertPixelRoundedSize(40, root.RowDefinitions[0].ActualHeight, root);
        AssertPixelRoundedSize(32, root.RowDefinitions[1].ActualHeight, root);
        var dpi = VisualTreeHelper.GetDpi(root);
        var horizontalTolerance = 1 / dpi.DpiScaleX;
        var verticalTolerance = 1 / dpi.DpiScaleY;
        var buttons = FindVisuals<Button>(root).ToArray();
        FrameworkElement Named(string name) => Assert.Single(FindVisuals<FrameworkElement>(root), element => element.Name == name && element.IsVisible);
        var toolbar = Named("ComparisonToolbar");
        Assert.Empty(FindVisuals<ComboBox>(toolbar));
        Assert.DoesNotContain(buttons, button => button.IsVisible && AutomationProperties.GetName(button) == Strings.ComparisonOptions);
        var modes = ModeButtonNames
            .Select(name => Assert.IsType<RadioButton>(Named(name))).ToArray();
        Assert.Single(modes, button => button.IsChecked == true);
        Assert.All(modes, button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
        var oldMode = viewModel.DisplayMode;
        foreach (var (button, mode) in modes.Zip(Modes))
        {
            var peer = new RadioButtonAutomationPeer(button);
            Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem)).Select();
            Assert.Equal(mode, viewModel.DisplayMode);
            Assert.Single(modes, item => item.IsChecked == true);
        }
        viewModel.DisplayMode = oldMode;
        FlushLayout(Window.GetWindow(root)!);
        Assert.Single(modes, item => item.IsChecked == true);
        var essentials = new List<FrameworkElement>
        {
            Assert.Single(buttons, button => AutomationProperties.GetName(button) == Strings.PreviousDifference),
            Assert.Single(buttons, button => AutomationProperties.GetName(button) == Strings.NextDifference),
            Assert.Single(buttons, button => Equals(button.ToolTip, Strings.ToggleThemeShortcut)),
            Assert.Single(buttons, button => Equals(button.ToolTip, Strings.Find)),
            Assert.Single(buttons, button => Equals(button.ToolTip, Strings.RefreshMine)),
            Assert.Single(buttons, button => Equals(button.ToolTip, Strings.MoreActions)),
            Assert.Single(FindVisuals<TextBlock>(root), text =>
                text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(viewModel.DifferencePositionText))
        };
        essentials.AddRange(modes);
        var statusBar = Named("ComparisonStatusBar");
        Assert.True(statusBar.IsVisible);
        AssertPixelRoundedSize(24, statusBar.ActualHeight, root);
        var bounds = essentials.Select(element =>
        {
            Assert.True(element.IsVisible, $"Toolbar element {element.GetType().Name} is hidden.");
            var rectangle = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            Assert.True(rectangle.Left >= -horizontalTolerance && rectangle.Right <= root.ActualWidth + horizontalTolerance,
                $"Toolbar clips {element.GetType().Name}: {rectangle}; width {root.ActualWidth}.");
            Assert.True(rectangle.Top >= -verticalTolerance
                && rectangle.Bottom <= root.RowDefinitions[0].ActualHeight + verticalTolerance,
                $"Toolbar element leaves its single row: {rectangle}.");
            Assert.True(rectangle.Width > 0 && rectangle.Height > 0,
                $"Toolbar element has no area: {element.GetType().Name}, {rectangle}.");
            return rectangle;
        }).ToArray();
        for (var first = 0; first < bounds.Length; first++)
        {
            for (var second = first + 1; second < bounds.Length; second++)
            {
                var overlap = Rect.Intersect(bounds[first], bounds[second]);
                Assert.True(overlap.IsEmpty || overlap.Width < 0.5 || overlap.Height < 0.5,
                    $"Toolbar elements overlap: {bounds[first]} and {bounds[second]}.");
            }
        }
        if (viewModel.IsEditableText)
        {
            var header = Named("MinePaneHeader");
            var headerBounds = header.TransformToAncestor(root).TransformBounds(new Rect(header.RenderSize));
            var save = Assert.IsType<Button>(Named("SaveButton"));
            var label = Assert.IsType<TextBlock>(save.Content);
            Assert.Equal(((SolidColorBrush)save.Foreground).Color, ((SolidColorBrush)label.Foreground).Color);
            var saveMenu = Assert.IsType<Button>(Named("SaveMenuButton"));
            var icon = Assert.IsType<AppIcon>(saveMenu.Content);
            Assert.Equal(AppIconKind.ChevronDown, icon.Kind);
            Assert.Equal(((SolidColorBrush)saveMenu.Foreground).Color, ((SolidColorBrush)icon.Foreground).Color);
            var actionBounds = EditorActionNames.Select(name =>
            {
                var action = Named(name);
                Assert.NotNull(action.GetBindingExpression(name == "EditModeToggle" ? ToggleButton.IsCheckedProperty : UIElement.IsEnabledProperty));
                var rectangle = action.TransformToAncestor(root).TransformBounds(new Rect(action.RenderSize));
                Assert.True(rectangle.Left >= headerBounds.Left - horizontalTolerance && rectangle.Right <= headerBounds.Right + horizontalTolerance,
                    $"Editor command {name} leaves its pane header: {rectangle}; header {headerBounds}.");
                Assert.True(rectangle.Top >= headerBounds.Top - verticalTolerance && rectangle.Bottom <= headerBounds.Bottom + verticalTolerance);
                return rectangle;
            }).ToArray();
            for (var first = 0; first < actionBounds.Length; first++)
                for (var second = first + 1; second < actionBounds.Length; second++)
                {
                    var overlap = Rect.Intersect(actionBounds[first], actionBounds[second]);
                    Assert.True(overlap.IsEmpty || overlap.Width < 0.5 || overlap.Height < 0.5, "Editor commands overlap.");
                }
        }
    }

    private static void AssertEditorScale(Window window, CodeEditorView editor, double fontSize, double lineHeight)
    {
        var browser = Assert.IsType<Microsoft.Web.WebView2.Wpf.WebView2>(editor.FindName("Browser"));
        var script = FormattableString.Invariant($"[left,right].every(editor=>editor.getOption(monaco.editor.EditorOption.fontSize)==={fontSize}&&editor.getOption(monaco.editor.EditorOption.lineHeight)==={lineHeight})");
        PumpUntil(() =>
        {
            var result = browser.ExecuteScriptAsync(script);
            PumpUntil(() => result.IsCompleted, window);
            return result.GetAwaiter().GetResult() == "true";
        }, window);
    }

    private static void AssertLocalPanelsStayOutsideBrowser(Window window, CodeEditorView editor, DiffWindowViewModel viewModel)
    {
        var browser = Assert.IsType<Microsoft.Web.WebView2.Wpf.WebView2>(editor.FindName("Browser"));
        var fullHeight = browser.ActualHeight;
        foreach (var find in new[] { true, false })
        {
            (find ? viewModel.OpenFindCommand : viewModel.OpenGoToCommand).Execute(null);
            FlushLayout(window);
            var panel = Assert.Single(FindVisuals<Border>(window), item => item.IsVisible && item.Name == (find ? "FindPanel" : "GoToPanel"));
            var panelBounds = panel.TransformToAncestor(window).TransformBounds(new Rect(panel.RenderSize));
            var browserBounds = browser.TransformToAncestor(window).TransformBounds(new Rect(browser.RenderSize));
            Assert.True(panelBounds.Bottom <= browserBounds.Top, "Native WebView2 must not cover local search or location controls.");
            var input = Assert.Single(FindVisuals<TextBox>(panel));
            Assert.True(input.ActualWidth >= 80, "The local input must remain usable at the narrow window size.");
            Assert.True(browser.ActualHeight < fullHeight);
            (find ? viewModel.CloseFindCommand : viewModel.CloseGoToCommand).Execute(null);
            FlushLayout(window);
            AssertPixelRoundedSize(fullHeight, browser.ActualHeight, browser);
        }
    }

    private static void AssertAdvancedRulesAccessible(Window window, DiffWindowViewModel viewModel)
    {
        var more = Assert.Single(FindVisuals<Button>(window), button => button.Name == "MoreActionsButton");
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        FlushLayout(window);
        var menu = Assert.IsType<ContextMenu>(more.ContextMenu);
        Assert.True(menu.IsOpen);
        var rules = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Name == "ComparisonRulesMenu");
        Assert.Equal(ChromeStrings.AdvancedRules, rules.Header);
        rules.IsSubmenuOpen = true;
        FlushLayout(window);
        Assert.Same(viewModel, rules.DataContext);
        Assert.Contains(rules.Items.OfType<MenuItem>(), item => ReferenceEquals(item.Command, viewModel.ApplyOptionsCommand));
        Assert.Contains(rules.Items.OfType<MenuItem>(), item => ReferenceEquals(item.Command, viewModel.SaveRulesCommand));
        if (viewModel.Options.SupportsText)
        {
            Assert.Contains(rules.Items.OfType<MenuItem>(), item => item.IsVisible && Equals(item.Header, Strings.IgnoreWhitespace));
            Assert.Contains(rules.Items.OfType<MenuItem>(), item => item.IsVisible && Equals(item.Header, Strings.IgnoreCase));
        }
        if (viewModel.Options.SupportsEncoding)
        {
            var encoding = Assert.Single(rules.Items.OfType<MenuItem>(), item => Equals(item.Header, viewModel.Options.EncodingLabel));
            Assert.Contains(encoding.Items.OfType<MenuItem>(), item => ReferenceEquals(item.Command, viewModel.Options.SetEncodingCommand));
        }
        if (viewModel.Options.SupportsSpreadsheetRules)
            Assert.Contains(rules.Items.OfType<MenuItem>(), item => item.IsVisible && Equals(item.Header, Strings.SpreadsheetRulesTitle));
        rules.IsSubmenuOpen = false;
        menu.IsOpen = false;
        FlushLayout(window);
    }

    private static void RenderSimulatedDpiEvidence(Grid root, string scenario, AppTheme theme, double[]? scales = null)
    {
        var version = typeof(DiffWindow).Assembly.GetName().Version?.ToString(3) ?? "unversioned";
        var configuredOutput = Environment.GetEnvironmentVariable("DIFFER_VISUAL_EVIDENCE");
        var output = string.IsNullOrWhiteSpace(configuredOutput)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Differ", "WorkspaceMaintenance", "visual-validation", version)
            : Path.GetFullPath(configuredOutput);
        Directory.CreateDirectory(output);
        // WebView2 is a child HWND and is absent from RenderTargetBitmap.
        // Capture the real local WebView surface separately and place that
        // unmodified bitmap at its measured bounds in the WPF screenshot.
        var browser = FindVisuals<Microsoft.Web.WebView2.Wpf.WebView2>(root).SingleOrDefault(view => view.IsVisible);
        BitmapFrame? browserFrame = null;
        var browserBounds = Rect.Empty;
        if (browser?.CoreWebView2 is not null)
        {
            using var stream = new MemoryStream();
            var capture = browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
            PumpUntil(() => capture.IsCompleted, Window.GetWindow(root)!);
            capture.GetAwaiter().GetResult();
            stream.Position = 0;
            browserFrame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            browserBounds = browser.TransformToAncestor(root).TransformBounds(new Rect(browser.RenderSize));
        }
        foreach (var scale in scales ?? [1.0, 1.25, 1.5, 2.0])
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
                (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            if (browserFrame is not null)
            {
                var composition = new DrawingVisual();
                using (var context = composition.RenderOpen())
                {
                    context.DrawImage(bitmap, new Rect(root.RenderSize));
                    context.DrawImage(browserFrame, browserBounds);
                }
                bitmap = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(composition);
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var name = string.Create(CultureInfo.InvariantCulture,
                $"{scenario.TrimStart('.')}-{theme.ToString().ToLowerInvariant()}-{root.ActualWidth:0}x{root.ActualHeight:0}-{scale * 100:0}.png");
            using var stream = File.Create(Path.Combine(output, name));
            encoder.Save(stream);
        }

        File.WriteAllText(Path.Combine(output, "DPI_METHOD.md"),
            "# WPF runtime rendering evidence\n\n"
            + "PNGs render the production DiffWindow using temporary synthetic inputs and isolated preferences. "
            + "reading-cs, reading-xlsx and reading-docx use the real format adapters; fallback cases exercise corrupt Office files switching to the binary text view. "
            + "Windows are positioned offscreen without activating. Runtime tests exercise commands and automation peers without sending desktop input.\n\n"
            + "Code-view images combine the actual WPF RenderTargetBitmap with WebView2 CapturePreview at the measured child-HWND bounds; they are not desktop screenshots or mockups. "
            + "Filenames include the logical client size (720 / 960 / 1500 DIP), theme and output scale. "
            + "The narrow layout is rendered at 96, 120, 144 and 192 DPI using RenderTargetBitmap (100%, 125%, 150%, 200%); the wide layout is rendered at 96 DPI. "
            + "These are simulated raster output DPI settings; Windows display scaling and monitor settings are not changed. "
            + "ReadingScale is tested separately as a content LayoutTransform, not as monitor DPI. "
            + "The evidence validates actual WPF templates, inherited resources, toolbar bounds and raster output, but does not replace native per-monitor DPI testing.\n");
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

    private static UserControl FindActiveFormatView(Window window) => Assert.Single(
        FindVisuals<UserControl>(window), view => view.IsVisible
            && view is TextDiffView or CodeEditorView or SpreadsheetWorksheetDiffView or WordDiffView);

    private static void AssertReadingContent(Window window, UserControl content, DiffWindowViewModel viewModel)
    {
        Assert.True(content.ActualWidth > 600);
        Assert.True(content.ActualHeight > 400);
        var overview = Assert.Single(FindVisuals<DiffOverviewView>(window), view => view.IsVisible);
        AssertPixelRoundedSize(44, overview.ActualWidth, overview, horizontal: true);
        if (content is CodeEditorView editor)
        {
            PumpUntil(() => editor.IsEditorReady && editor.RenderedVersion == viewModel.EditorSession!.Version, window);
            Assert.True(viewModel.IsEditableText);
            Assert.Empty(FindVisuals<DataGrid>(content));
            var browser = Assert.IsType<Microsoft.Web.WebView2.Wpf.WebView2>(editor.FindName("Browser"));
            Assert.True(browser.IsVisible);
            Assert.True(browser.ActualWidth > 600);
            Assert.True(browser.ActualHeight > 300);
            Assert.True(viewModel.EditorSession!.Text.Split('\n').Length >= 80);
            Assert.NotNull(browser.CoreWebView2);
            Assert.Null(editor.FindName("AdoptButton"));
            Assert.NotNull(editor.FindName("UndoButton"));
            var headers = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName("EditorPaneHeaders"));
            var baseHeader = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName("BasePaneHeader"));
            var mineHeader = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName("MinePaneHeader"));
            AssertPixelRoundedSize(36, headers.ActualHeight, editor);
            var baseBounds = baseHeader.TransformToAncestor(editor).TransformBounds(new Rect(baseHeader.RenderSize));
            var mineBounds = mineHeader.TransformToAncestor(editor).TransformBounds(new Rect(mineHeader.RenderSize));
            AssertPixelRoundedSize(baseBounds.Width, mineBounds.Width, editor, horizontal: true);
            AssertPixelRoundedSize(baseBounds.Top, mineBounds.Top, editor);
            Assert.NotNull(baseHeader.ToolTip);
            Assert.NotNull(mineHeader.ToolTip);
            return;
        }
        if (content is WordDiffView)
        {
            Assert.True(viewModel.IsWordView);
            Assert.DoesNotContain(FindVisuals<ToggleButton>(content), button => button.Name == "DetailsToggle");
            Assert.Empty(FindVisuals<DataGrid>(content));
            var rows = Assert.IsType<ListBox>(content.FindName("WordRows"));
            var basePanes = FindVisuals<Border>(rows).Where(pane => pane.Name == "BasePane" && pane.IsVisible).ToArray();
            Assert.NotEmpty(basePanes);
            var tolerance = 1 / VisualTreeHelper.GetDpi(rows).DpiScaleX;
            var wordRoot = Assert.IsType<Grid>(content.Content);
            var footer = Assert.Single(wordRoot.Children.OfType<Grid>(), grid => Grid.GetRow(grid) == 1);
            var baseFooter = Assert.Single(footer.Children.OfType<Border>(), pane => Grid.GetColumn(pane) == 0);
            var footerBounds = baseFooter.TransformToAncestor(content).TransformBounds(new Rect(baseFooter.RenderSize));
            foreach (var basePane in basePanes)
            {
                var row = Assert.IsType<Grid>(basePane.Parent);
                var minePane = Assert.Single(row.Children.OfType<Border>(), pane => pane.Name == "MinePane");
                var baseBounds = basePane.TransformToAncestor(rows).TransformBounds(new Rect(basePane.RenderSize));
                var mineBounds = minePane.TransformToAncestor(rows).TransformBounds(new Rect(minePane.RenderSize));
                AssertPixelRoundedSize(baseBounds.Width, mineBounds.Width, rows, horizontal: true);
                Assert.True(baseBounds.Left >= -tolerance && mineBounds.Right <= rows.ActualWidth + tolerance,
                    $"Word paragraph exceeds its viewport after resize: BASE {baseBounds}, MINE {mineBounds}, viewport {rows.ActualWidth}. {DescribeLayoutChain(basePane, rows)}");
                var paneBounds = basePane.TransformToAncestor(content).TransformBounds(new Rect(basePane.RenderSize));
                AssertPixelRoundedSize(footerBounds.Right, paneBounds.Right, content, horizontal: true);
            }
            return;
        }

        var grids = FindVisuals<DataGrid>(content).Where(grid => grid.IsVisible).ToArray();
        Assert.Equal(2, grids.Length);
        Assert.All(grids, grid =>
        {
            Assert.True(grid.EnableRowVirtualization);
            Assert.True(grid.EnableColumnVirtualization);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(grid));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));
            Assert.Equal(DataGridClipboardCopyMode.None, grid.ClipboardCopyMode);
            Assert.False(ScrollViewer.GetIsDeferredScrollingEnabled(grid));
            Assert.True(grid.ActualHeight > 200);
            Assert.True(grid.Items.Count >= 80);
            Assert.True(FindVisuals<DataGridRow>(grid).Count() < grid.Items.Count,
                "Only the viewport and its virtualization cache should instantiate row containers.");
        });
        AssertPixelRoundedSize(grids[0].ActualWidth, grids[1].ActualWidth, content, horizontal: true);
        AssertPixelRoundedSize(grids[0].ActualHeight, grids[1].ActualHeight, content);
        var toggle = Assert.IsType<ToggleButton>(content.FindName("DetailsToggle"));
        Assert.Equal(content is SpreadsheetWorksheetDiffView, toggle.IsChecked);
    }

    private static void AssertAccessibleDetails(Window window, UserControl content, DiffWindowViewModel viewModel)
    {
        var toggle = Assert.IsType<ToggleButton>(content.FindName("DetailsToggle"));
        var details = Assert.IsType<Grid>(content.FindName("DetailsContent"));
        Assert.True(toggle.IsVisible);
        Assert.True(toggle.Focusable);
        Assert.True(KeyboardNavigation.GetIsTabStop(toggle));
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle)));
        var peer = new ToggleButtonAutomationPeer(toggle);
        var provider = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
        if (toggle.IsChecked != true) provider.Toggle();
        FlushLayout(window);
        Assert.Equal(ToggleState.On, provider.ToggleState);
        Assert.True(details.IsVisible);
        var boxes = FindVisuals<SelectableDiffTextBox>(details).ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.All(boxes, box =>
        {
            Assert.True(box.IsVisible);
            Assert.True(box.IsReadOnly);
            Assert.True(box.Focusable);
            Assert.True(box.ActualHeight > 20);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)));
            var expected = string.Concat(box.Segments!.Select(segment => segment.Text));
            Assert.False(string.IsNullOrEmpty(expected));
            box.SelectAll();
            Assert.Equal(expected, box.Selection.Text.TrimEnd('\r', '\n'));
        });
        var position = viewModel.DifferencePositionText;
        provider.Toggle();
        FlushLayout(window);
        Assert.Equal(ToggleState.Off, provider.ToggleState);
        Assert.False(details.IsVisible);
        Assert.Equal(position, viewModel.DifferencePositionText);
        Assert.True(viewModel.NextDifferenceCommand.CanExecute(null));
        viewModel.NextDifferenceCommand.Execute(null);
        FlushLayout(window);
        Assert.NotEqual(position, viewModel.DifferencePositionText);
        Assert.Equal(ToggleState.Off, provider.ToggleState);
        Assert.False(details.IsVisible);
        Assert.All(boxes, box => Assert.False(box.IsVisible));
    }

    private static void WriteReadingFixture(string path, string extension, bool changed)
    {
        string Content(int row) => changed && row is 3 or 18 or 40 or 70
            ? $"第 {row:00} 项 / 当前内容：完整中文差异用于提交前审阅；数值 240；保留未修改上下文。"
            : $"第 {row:00} 项 / 原始内容：完整中文差异用于提交前审阅；数值 120；保留未修改上下文。";
        if (extension == ".cs")
        {
            File.WriteAllLines(path, Enumerable.Range(1, 80).Select(row =>
                $"    public string Value{row:00} = \"{Content(row)}\";"));
            return;
        }

        if (extension == ".docx")
        {
            using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            var main = document.AddMainDocumentPart();
            main.Document = new Word.Document(new Word.Body(Enumerable.Range(1, 80)
                .Select(row => new Word.Paragraph(new Word.Run(new Word.Text(Content(row)))))));
            main.Document.Save();
            return;
        }

        using var workbook = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = workbook.AddWorkbookPart();
        workbookPart.Workbook = new Spreadsheet.Workbook();
        var sheets = new Spreadsheet.Sheets();
        workbookPart.Workbook.Append(sheets);
        for (var sheetIndex = 1; sheetIndex <= 2; sheetIndex++)
        {
            var part = workbookPart.AddNewPart<WorksheetPart>();
            var data = new Spreadsheet.SheetData();
            for (var row = 1; row <= 80; row++)
            {
                Spreadsheet.Cell Cell(string column, string value) => new()
                {
                    CellReference = column + row.ToString(CultureInfo.InvariantCulture),
                    DataType = Spreadsheet.CellValues.InlineString,
                    InlineString = new Spreadsheet.InlineString(new Spreadsheet.Text(value))
                };
                data.Append(new Spreadsheet.Row(Cell("A", row.ToString(CultureInfo.InvariantCulture)),
                    Cell("B", Content(row)), Cell("C", "未修改的上下文"))
                { RowIndex = (uint)row });
            }
            part.Worksheet = new Spreadsheet.Worksheet(data);
            sheets.Append(new Spreadsheet.Sheet
            {
                Name = "中文工作表" + sheetIndex,
                SheetId = (uint)sheetIndex,
                Id = workbookPart.GetIdOfPart(part)
            });
        }
        workbookPart.Workbook.Save();
    }

    private static void AssertLocalPanelsAndZoom(
        DiffWindow window, Grid root, ComparisonWorkspaceViewModel workspace,
        DiffWindowViewModel viewModel, TextDiffView textView)
    {
        var overview = Assert.Single(FindVisuals<DiffOverviewView>(root));
        var originalOverviewWidth = overview.ActualWidth;
        workspace.Preferences.ReadingScale = 1.5;
        FlushLayout(window);
        var body = Assert.Single(FindVisuals<ContentControl>(root), control =>
            control.GetType() == typeof(ContentControl) && ReferenceEquals(control.Content, viewModel));
        var scale = Assert.IsType<ScaleTransform>(body.LayoutTransform);
        Assert.Equal(1.5, scale.ScaleX);
        Assert.Equal(1.5, scale.ScaleY);
        AssertPixelRoundedSize(originalOverviewWidth, overview.ActualWidth, overview, horizontal: true);
        AssertPixelRoundedSize(44, overview.ActualWidth, overview, horizontal: true);
        AssertToolbarFits(root, viewModel);

        var bodyHeight = textView.ActualHeight;
        viewModel.OpenFindCommand.Execute(null);
        FlushLayout(window);
        var find = Assert.Single(FindVisuals<TextBox>(root), input => input.Name == "FindInput");
        Assert.True(find.IsVisible);
        var findPanel = Assert.Single(FindVisuals<Border>(root), panel => panel.Name == "FindPanel");
        AssertPixelRoundedSize(bodyHeight - findPanel.ActualHeight / scale.ScaleY, textView.ActualHeight, textView);
        var findBounds = findPanel.TransformToAncestor(root).TransformBounds(new Rect(findPanel.RenderSize));
        var contentBounds = textView.TransformToAncestor(root).TransformBounds(new Rect(textView.RenderSize));
        Assert.True(findBounds.Bottom <= contentBounds.Top, "The search strip reserves space above the format view.");
        viewModel.FindText = "43";
        RaiseKey(find, window, Key.Enter);
        Assert.Equal(1, viewModel.FindMatchCount);
        RaiseKey(window, window, Key.Escape);
        FlushLayout(window);
        Assert.False(viewModel.IsFindOpen);
        Assert.True(window.IsVisible);
        AssertPixelRoundedSize(bodyHeight, textView.ActualHeight, textView);

        viewModel.OpenGoToCommand.Execute(null);
        FlushLayout(window);
        var goTo = Assert.Single(FindVisuals<TextBox>(root), input => input.Name == "GoToInput");
        Assert.True(goTo.IsVisible);
        var goToPanel = Assert.Single(FindVisuals<Border>(root), panel => panel.Name == "GoToPanel");
        AssertPixelRoundedSize(bodyHeight - goToPanel.ActualHeight / scale.ScaleY, textView.ActualHeight, textView);
        viewModel.GoToText = "0";
        RaiseKey(goTo, window, Key.Enter);
        Assert.Contains("0", viewModel.LocationStatusText, StringComparison.Ordinal);
        RaiseKey(window, window, Key.Escape);
        FlushLayout(window);
        Assert.False(viewModel.IsGoToOpen);
        Assert.True(window.IsVisible);
        AssertPixelRoundedSize(bodyHeight, textView.ActualHeight, textView);
        workspace.Preferences.ReadingScale = 1;
        FlushLayout(window);
    }

    private static void RaiseKey(UIElement target, Window window, Key key)
    {
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static void AssertPixelRoundedSize(double expected, double actual, Visual visual, bool horizontal = false)
    {
        // UseLayoutRounding aligns to the monitor's physical pixel grid. A
        // 30-DIP row can legitimately occupy 30.4 DIP on a 125% display.
        var dpi = VisualTreeHelper.GetDpi(visual);
        var tolerance = 1 / (horizontal ? dpi.DpiScaleX : dpi.DpiScaleY) + 0.0001;
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
    }

    private static string DescribeLayoutChain(FrameworkElement child, FrameworkElement ancestor)
    {
        var entries = new List<string>();
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement element)
            {
                entries.Add($"{element.GetType().Name}: actual={element.ActualWidth:0.0}, desired={element.DesiredSize.Width:0.0}, slot={LayoutInformation.GetLayoutSlot(element).Width:0.0}");
            }
            if (ReferenceEquals(current, ancestor)) break;
        }
        return string.Join(" / ", entries);
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        // The shared test host already owns Application.Current. Load the
        // production resource subtree instead of creating a second Application
        // or approximating its implicit DataGrid/TextBlock styles.
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
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private sealed class GatedAdapter(IFileDiffAdapter inner) : IFileDiffAdapter
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileKind FileKind => inner.FileKind;
        public bool CanHandle(string path) => inner.CanHandle(path);
        public async Task<DiffDocument> CompareAsync(DiffRequest request, CancellationToken cancellationToken)
        {
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.CompareAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class DispatcherContextScope : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;

        public DispatcherContextScope() => SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(previous);
    }
}
