using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Differ.Formats.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Wpf;

namespace Differ.App.Tests;

[Collection("UI performance")]
public sealed class ThemeDirectPaletteRuntimeTests
{
    private static readonly int[] SampleMilliseconds = [200, 340];
    private static readonly int[] ParsedHex = [1, 3, 5, 128];
    private static readonly int[] ParsedSrgb = [51, 102, 153, 128];
    private static readonly int[] ToEvenHalfway = [0, 2, 2, 255];
    private static readonly int[] TransparentToOpaque = [0, 100, 200, 128];
    private static readonly int[] UnequalAlpha = [85, 95, 105, 128];
    private static readonly int[] HiddenTransparentColour = [255, 0, 0, 0];
    private static readonly int[] OpaqueTarget = [0, 100, 200, 255];
    private static readonly bool[] EditorSides = [false, true];

    [Fact]
    public void PreparedPaletteUsesWpfRgbaValuesWithoutRootCssTransitions()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            var resources = new ResourceDictionary();
            using var themes = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            themes.InitializeFromSystem();
            var source = fixture.ReadBodyColour();
            themes.PrepareToggle();
            var target = Assert.IsType<ResourceDictionary>(resources[ThemeService.TargetThemeKey]);
            fixture.Prepare(
                ThemeTestSupport.ResolveColour(target, "Dui.Surface.Content"),
                ThemeTestSupport.ResolveColour(target, "Dui.Surface.PaneHeader"),
                ThemeTestSupport.ResolveColour(target, "Dui.Text.Primary"));

            Assert.Equal(source, fixture.ReadBodyColour());
            Assert.Equal("true", fixture.Script("themeColorTimer!==null&&themePaletteTransitions.length>0&&preparedThemeMotion!==null"));
            fixture.AssertNoRootCssTransition();
            fixture.AssertIdentifierFollowsForeground();
            fixture.AssertBusinessColoursKeepPriority();

            foreach (var elapsed in SampleMilliseconds)
            {
                themes.RenderTransitionFrame(elapsed);
                fixture.RenderPaletteAt(elapsed);
                Assert.Equal(Rgba(ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content")), fixture.ReadBodyColour());
                Assert.Equal(Rgba(ThemeTestSupport.ResolveColour(resources, "Dui.Surface.Content")), fixture.ReadRootColour("background"));
                Assert.Equal(Rgba(ThemeTestSupport.ResolveColour(resources, "Dui.Surface.PaneHeader")), fixture.ReadRootColour("chrome"));
                fixture.AssertNoRootCssTransition();
                fixture.AssertIdentifierFollowsForeground();
                fixture.AssertBusinessColoursKeepPriority();
            }

            fixture.Script("finishThemeColors(true)");
            Assert.Equal(Rgba(ThemeTestSupport.ResolveColour(target, "Dui.Surface.Content")), fixture.ReadBodyColour());
            Assert.Equal("true", fixture.Script("themeColorTimer===null&&themePaletteTransitions.length===0&&preparedThemeMotion===null"));
            fixture.AssertIdentifierFollowsForeground();
        });
    }

    [Fact]
    public void BrowserParsesSupportedColourSyntaxAndMatchesPremultipliedToEvenRounding()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            Assert.Equal(ParsedHex, fixture.IntArray("parseThemeColor('#01030580')"));
            Assert.Equal(ParsedHex, fixture.IntArray("parseThemeColor('rgb(1 3 5 / 0.5)')"));
            Assert.Equal(ParsedHex, fixture.IntArray("parseThemeColor('rgba(1, 3, 5, 0.5)')"));
            Assert.Equal(ParsedSrgb, fixture.IntArray("parseThemeColor('color(srgb 0.2 0.4 0.6 / 0.5)')"));
            Assert.Equal(ParsedHex, fixture.IntArray("parseThemeColor('rgb(1 3 5 / 50%)')"));

            Assert.Equal(ToEvenHalfway, fixture.IntArray("interpolateThemeColor([0,1,2,255],[1,2,3,255],0.5)"));
            Assert.Equal(TransparentToOpaque, fixture.IntArray("interpolateThemeColor([255,0,0,0],[0,100,200,255],0.5)"));
            Assert.Equal(UnequalAlpha, fixture.IntArray("interpolateThemeColor([10,20,30,64],[110,120,130,192],0.5)"));
            Assert.Equal(HiddenTransparentColour, fixture.IntArray("interpolateThemeColor([255,0,0,0],[0,100,200,255],0)"));
            Assert.Equal(OpaqueTarget, fixture.IntArray("interpolateThemeColor([255,0,0,0],[0,100,200,255],1)"));
        });
    }

    [Fact]
    public void ReversalKeepsTheDisplayedColourAndPreservesBothReadOnlyModels()
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Script("left.setSelection(new monaco.Range(1,1,1,6));right.setSelection(new monaco.Range(2,1,2,7));window.paletteOriginal=original;window.paletteModified=modified;");
            var originalState = fixture.ModelState();
            var initial = fixture.ReadBodyColour();
            var tokenClasses = fixture.VisibleTokenClasses();
            var initialSyntax = fixture.VisibleSyntaxColour();
            fixture.Prepare(Color.FromRgb(23, 24, 28), Color.FromRgb(26, 28, 33), Color.FromRgb(245, 245, 247));
            Assert.Equal(initialSyntax, fixture.VisibleSyntaxColour());
            Assert.Equal(tokenClasses, fixture.VisibleTokenClasses());
            fixture.RenderPaletteAt(200);
            var displayed = fixture.ReadBodyColour();
            Assert.NotEqual(initial, displayed);
            var syntaxColour = fixture.VisibleSyntaxColour();
            fixture.AssertCurrentClassBridge();
            fixture.AssertRedrawKeepsClassColours(tokenClasses);

            fixture.Script("finishThemeColors(false)");
            Assert.Equal(displayed, fixture.ReadBodyColour());
            Assert.Equal(syntaxColour, fixture.VisibleSyntaxColour());
            fixture.AssertCurrentClassBridge();
            fixture.Script("applyThemeTransition({dark:false,palette:{...window.directPaletteInitial},mist:{waitForStart:true,transitionId:92,startedAt:0,x:0,y:0,width:1,height:1}})");
            Assert.Equal(displayed, fixture.ReadBodyColour());
            fixture.RenderPaletteAt(0);
            Assert.Equal(displayed, fixture.ReadBodyColour());
            Assert.Equal(syntaxColour, fixture.VisibleSyntaxColour());
            fixture.AssertRedrawKeepsClassColours(tokenClasses);
            fixture.RenderPaletteAt(340);

            // Reverse twice before either target completes. Every new preparation/zero frame keeps its current class colour.
            var reversedBody = fixture.ReadBodyColour();
            var reversedSyntax = fixture.VisibleSyntaxColour();
            fixture.Prepare(Color.FromRgb(23, 24, 28), Color.FromRgb(26, 28, 33), Color.FromRgb(245, 245, 247), transitionId: 93);
            Assert.Equal(reversedBody, fixture.ReadBodyColour());
            Assert.Equal(reversedSyntax, fixture.VisibleSyntaxColour());
            fixture.RenderPaletteAt(0);
            Assert.Equal(reversedBody, fixture.ReadBodyColour());
            Assert.Equal(reversedSyntax, fixture.VisibleSyntaxColour());
            fixture.AssertRedrawKeepsClassColours(tokenClasses);
            fixture.RenderPaletteAt(200);
            reversedBody = fixture.ReadBodyColour();
            reversedSyntax = fixture.VisibleSyntaxColour();
            fixture.Script("applyThemeTransition({dark:false,palette:{...window.directPaletteInitial},mist:{waitForStart:true,transitionId:94,startedAt:0,x:0,y:0,width:1,height:1}})");
            fixture.RenderPaletteAt(0);
            Assert.Equal(reversedBody, fixture.ReadBodyColour());
            Assert.Equal(reversedSyntax, fixture.VisibleSyntaxColour());
            fixture.AssertRedrawKeepsClassColours(tokenClasses);
            fixture.RenderPaletteAt(340);
            fixture.Script("window.completedBridgeStyle=themeSyntaxBridge.style");
            fixture.Script("finishThemeColors(true)");

            Assert.Equal(initial, fixture.ReadBodyColour());
            Assert.Equal(initialSyntax, fixture.VisibleSyntaxColour());
            Assert.Equal(tokenClasses, fixture.VisibleTokenClasses());
            Assert.Equal("true", fixture.Script("themeColorTimer===null&&themePaletteTransitions.length===0&&themeSyntaxBridge===null&&preparedThemeMotion===null&&themePaletteTarget===null&&!window.completedBridgeStyle.isConnected"));
            Assert.Equal("true", fixture.Script("original===window.paletteOriginal&&modified===window.paletteModified"));
            Assert.Equal(originalState, fixture.ModelState());
            Assert.Equal("true", fixture.Script("left.getOption(monaco.editor.EditorOption.readOnly)&&right.getOption(monaco.editor.EditorOption.readOnly)"));
            Assert.False(fixture.Owner.IsEditorDirty);
            fixture.AssertNoRootCssTransition();
            fixture.AssertIdentifierFollowsForeground();
            fixture.AssertBusinessColoursKeepPriority();

            fixture.Prepare(Color.FromRgb(23, 24, 28), Color.FromRgb(26, 28, 33), Color.FromRgb(245, 245, 247));
            fixture.RenderPaletteAt(200);
            fixture.Script("finishThemeColors(false)");
            fixture.Script("window.reloadedBridgeStyle=themeSyntaxBridge.style");
            var oldSession = fixture.Owner.EditorSession;
            Wait(fixture.Owner.RefreshMineAsync());
            PumpUntil(() => !ReferenceEquals(oldSession, fixture.Owner.EditorSession)
                && fixture.Owner.EditorResultsCurrent
                && fixture.Script("themeSyntaxBridge===null&&!window.reloadedBridgeStyle.isConnected&&themePaletteTransitions.length===0&&themePaletteTarget===null") == "true");

            fixture.Prepare(Color.FromRgb(23, 24, 28), Color.FromRgb(26, 28, 33), Color.FromRgb(245, 245, 247), transitionId: 95);
            fixture.RenderPaletteAt(200);
            fixture.Script("finishThemeColors(false);window.clearedBridgeStyle=themeSyntaxBridge.style");
            fixture.Owner.SendEditorCommand(new { type = "clear" });
            PumpUntil(() => fixture.Script("original===null&&modified===null&&themeColorTimer===null&&preparedThemeMotion===null&&themePaletteTransitions.length===0&&themeSyntaxBridge===null&&themePaletteTarget===null&&!window.clearedBridgeStyle.isConnected") == "true");
        });
    }

    private static int[] Rgba(Color colour) => [colour.R, colour.G, colour.B, colour.A];
    private static string Hex(Color colour) => colour.A == 255
        ? $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}"
        : $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}{colour.A:X2}";

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.DirectPalette.Tests", Guid.NewGuid().ToString("N"));
        private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;
        private readonly ComparisonLoadScheduler _scheduler = new();
        private readonly ExternalDiffSessionStore _store;
        private readonly ExternalDiffSession _session;
        private readonly UserPreferencesService _preferences;
        private readonly CodeEditorView _view;
        private readonly Window _window;

        public Fixture()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Directory.CreateDirectory(_directory);
            var basePath = Path.Combine(_directory, "base.lua");
            var minePath = Path.Combine(_directory, "mine.lua");
            File.WriteAllText(basePath, "local value = (1)\nreturn value\n-- baseline");
            File.WriteAllText(minePath, "local value = (2)\nreturn alternate\n-- current");
            _store = new ExternalDiffSessionStore(Path.Combine(_directory, "sessions"));
            _preferences = new UserPreferencesService(Path.Combine(_directory, "preferences.json"));
            _session = Wait(_store.CreateAsync(new ExternalDiffRequest { BaseFilePath = basePath, MineFilePath = minePath }, CancellationToken.None));
            Owner = new DiffWindowViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]), _scheduler,
                _session, NullLogger<DiffWindowViewModel>.Instance, _ => { });
            Owner.ConfigureActions(_store, session => _store.TryDelete(session), _preferences, () => []);
            Owner.EnableDocumentEditor();
            Wait(Owner.LoadAsync());
            _view = new CodeEditorView { DataContext = Owner };
            _window = new Window
            {
                Content = _view,
                Width = 1200,
                Height = 700,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            _window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Light.xaml"));
            _window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Metrics.xaml"));
            _window.Show();
            PumpUntil(() => _view.IsEditorReady && Owner.EditorResultsCurrent && _view.RenderedVersion == Owner.EditorSession!.Version);
            Browser = Assert.IsType<WebView2>(_view.FindName("Browser"));
            PumpUntil(() => Script("Array.from(document.querySelectorAll('.view-line span')).filter(element=>element.isConnected&&element.textContent.trim()==='return'&&!element.classList.contains('mtk1')).length>=2") == "true");
            // Canvas decodes the browser's actual computed CSS independently of the interpolation/parser under test.
            Script("window.directPaletteInitial={...appliedPalette};window.readPaletteCss=value=>{const canvas=document.createElement('canvas');canvas.width=canvas.height=1;const context=canvas.getContext('2d');context.fillStyle=value;context.fillRect(0,0,1,1);return Array.from(context.getImageData(0,0,1,1).data);};window.directPaletteToken=(text,mine=false)=>{const editor=mine?right:left;const element=Array.from(editor.getDomNode().querySelectorAll('.view-line span')).find(item=>item.isConnected&&item.textContent.trim()===text&&!item.classList.contains('changed-old')&&!item.classList.contains('changed-new'));if(!element)throw new Error('Visible token is missing: '+text);return element;};window.directPaletteClass=element=>{const name=Array.from(element.classList).find(item=>/^mtk[0-9]+$/.test(item));if(!name)throw new Error('Visible token has no mtk class');return name;};");
            PumpUntil(() => Script("[left,right].every(editor=>editor.getDomNode().querySelector('.view-line .mtk1'))&&document.querySelector('.view-line .mtk1.changed-old')!==null&&document.querySelector('.view-line .mtk1.changed-new')!==null&&document.querySelector('.view-line [class*=bracket-highlighting-]')!==null") == "true");
        }

        public DiffWindowViewModel Owner { get; }
        public WebView2 Browser { get; }
        public string Script(string script) => Wait(Browser.ExecuteScriptAsync(script));
        public int[] IntArray(string expression)
        {
            using var result = JsonDocument.Parse(Script(expression));
            return result.RootElement.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        }

        public int[] ReadBodyColour() => IntArray("window.readPaletteCss(getComputedStyle(document.body).backgroundColor)");
        public int[] VisibleSyntaxColour() => VisibleTokenColour("return", mine: false);
        public int[] VisibleTokenColour(string token, bool mine) => IntArray($"window.readPaletteCss(getComputedStyle(window.directPaletteToken({JsonSerializer.Serialize(token)},{(mine ? "true" : "false")})).color)");
        public string VisibleTokenClasses() => Script("JSON.stringify([false,true].flatMap(mine=>['return','value'].map(text=>({mine,text,name:window.directPaletteClass(window.directPaletteToken(text,mine))}))))");
        public void AssertIdentifierFollowsForeground()
        {
            foreach (var mine in EditorSides) Assert.Equal(ReadRootColour("foreground"), VisibleTokenColour("value", mine));
        }

        public void AssertBusinessColoursKeepPriority()
        {
            Assert.Equal(ReadRootColour("deletedText"), IntArray("window.readPaletteCss(getComputedStyle(document.querySelector('.view-line .mtk1.changed-old')).color)"));
            Assert.Equal(ReadRootColour("addedText"), IntArray("window.readPaletteCss(getComputedStyle(document.querySelector('.view-line .mtk1.changed-new')).color)"));
            // Exercise mtk1 together with the real bracket class; a sibling without mtk supplies its semantic reference.
            Assert.Equal("true", Script("(()=>{const bracket=document.querySelector('.view-line [class*=bracket-highlighting-]:not(.changed-old):not(.changed-new)');if(!bracket)throw new Error('Visible bracket is missing');const originalClass=bracket.className;const reference=document.createElement('span');reference.className=Array.from(bracket.classList).filter(name=>!/^mtk[0-9]+$/.test(name)).join(' ');reference.textContent=bracket.textContent;bracket.parentNode.appendChild(reference);try{bracket.classList.add('mtk1');return getComputedStyle(bracket).color===getComputedStyle(reference).color;}finally{bracket.className=originalClass;reference.remove();}})()"));
        }

        public void AssertCurrentClassBridge()
        {
            Assert.Equal("true", Script("themeSyntaxBridge!==null&&themeSyntaxBridge.style.isConnected"));
            foreach (var mine in EditorSides)
            {
                var side = mine ? "true" : "false";
                Assert.Equal(IntArray($"themeSyntaxBridge.currentColors.get(window.directPaletteClass(window.directPaletteToken('return',{side})))"), VisibleTokenColour("return", mine));
                Assert.Equal(IntArray($"themeSyntaxBridge.currentColors.get(window.directPaletteClass(window.directPaletteToken('value',{side})))"), VisibleTokenColour("value", mine));
            }
            AssertIdentifierFollowsForeground();
            AssertBusinessColoursKeepPriority();
        }

        public void AssertRedrawKeepsClassColours(string expectedClasses)
        {
            var before = VisibleSyntaxColour();
            Script("left.render(true);right.render(true)");
            Assert.Equal(expectedClasses, VisibleTokenClasses());
            Assert.Equal(before, VisibleSyntaxColour());
            AssertCurrentClassBridge();
        }
        public int[] ReadRootColour(string name) => IntArray($"window.readPaletteCss(getComputedStyle(document.documentElement).getPropertyValue('--{name}'))");
        public void RenderPaletteAt(int milliseconds) => Script(FormattableString.Invariant($"themeColorStart=0;renderThemeColors({milliseconds});"));
        public void AssertNoRootCssTransition() => Assert.Equal("0", Script("document.getAnimations().filter(animation=>animation instanceof CSSTransition&&animation.effect.target===document.documentElement).length"));
        public string ModelState() => Script("JSON.stringify({originalUri:original.uri.toString(),modifiedUri:modified.uri.toString(),originalVersion:original.getVersionId(),modifiedVersion:modified.getVersionId(),historyVersion:modified.getAlternativeVersionId(),originalSelection:left.getSelection(),modifiedSelection:right.getSelection(),originalText:original.getValue(),modifiedText:modified.getValue(),canUndo:modified.canUndo(),canRedo:modified.canRedo()})");

        public void Prepare(Color background, Color chrome, Color foreground, long transitionId = 91)
        {
            var values = JsonSerializer.Serialize(new { background = Hex(background), chrome = Hex(chrome), foreground = Hex(foreground) });
            Script(FormattableString.Invariant($"applyThemeTransition({{dark:true,palette:{{...appliedPalette,...{values}}},mist:{{waitForStart:true,transitionId:{transitionId},startedAt:0,x:0,y:0,width:1,height:1}}}})"));
        }

        public void Dispose()
        {
            Owner.Dispose();
            _window.Close();
            _store.TryDelete(_session);
            _store.TryDelete(Owner.Session);
            _scheduler.Dispose();
            _preferences.Dispose();
            Directory.Delete(_directory, recursive: true);
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }
    }

    private static T Wait<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("The real WebView2 palette fixture did not become ready.");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
