using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
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
public sealed class EditorVisualStabilityTests
{
    private static readonly JsonSerializerOptions EvidenceOptions = new() { WriteIndented = true };

    [Theory]
    [InlineData(DiffDisplayMode.All, 25)]
    [InlineData(DiffDisplayMode.Context, 25)]
    [InlineData(DiffDisplayMode.Context, 220)]
    public void ContinuousTypingKeepsUneditedAddedLinesPaintedThroughEveryFrame(DiffDisplayMode mode, int intervalMilliseconds)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var source = mode == DiffDisplayMode.Context && intervalMilliseconds == 220
                ? Environment.GetEnvironmentVariable("DIFFER_FLICKER_SOURCE") : null;
            var text = string.IsNullOrWhiteSpace(source) ? CreateLines(160) : File.ReadAllText(source);
            var anchor = string.IsNullOrWhiteSpace(source) ? "Item012" : "Item000003";
            using var fixture = new Fixture(string.Empty, text);
            text = fixture.Owner.EditorSession!.Text;
            fixture.Owner.DisplayMode = mode;
            Wait(fixture.Owner.SetEditModeAsync(true));
            fixture.Script("right.setPosition({lineNumber:3,column:1});right.pushUndoStop();");
            fixture.WaitForPaint();
            var offset = JsonSerializer.Deserialize<int>(fixture.Script("modified.getOffsetAt({lineNumber:3,column:1})"));
            fixture.StartFrames(anchor);
            fixture.Script("window.__inputDone=false;(async()=>{for(const text of ['a','b','中','c']){right.trigger('keyboard','type',{text});await new Promise(resolve=>setTimeout(resolve," + intervalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "));}right.pushUndoStop();window.__inputDone=true;})();");
            PumpUntil(() => fixture.Script("window.__inputDone===true") == "true" && fixture.IsCurrent);
            fixture.WaitForPaint();
            using var frames = fixture.StopFrames("typing-" + mode + "-" + intervalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AssertStablePaint(frames.RootElement);
            Assert.Equal(text.Insert(offset, "ab中c"), fixture.Owner.EditorSession!.Text);
            Assert.True(fixture.Owner.CanEditorUndo);
            Assert.Equal("true", fixture.Script("modified===window.__observedModel&&monaco.editor.getModels().length===2"));
        });
    }

    [Fact]
    public void NewlineUndoAndRedoKeepTheSameDocumentAndUneditedLinePaint()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var text = CreateLines(160);
            using var fixture = new Fixture(string.Empty, text);
            fixture.Owner.DisplayMode = DiffDisplayMode.Context;
            Wait(fixture.Owner.SetEditModeAsync(true));
            fixture.Script("right.setPosition({lineNumber:3,column:1});right.pushUndoStop();");
            fixture.WaitForPaint();
            fixture.StartFrames("Item012");
            fixture.Script("right.trigger('keyboard','type',{text:'\\n'});right.pushUndoStop();");
            PumpUntil(() => fixture.IsCurrent && fixture.Owner.EditorSession!.Text != text);
            fixture.WaitForPaint();
            using (var frames = fixture.StopFrames("newline")) AssertStablePaint(frames.RootElement, permittedLineShift: 24);
            var edited = fixture.Owner.EditorSession!.Text;
            Assert.Equal("true", fixture.Script("modified===window.__observedModel&&modified.canUndo()"));
            PumpUntil(() => fixture.Owner.CanEditorUndo);

            fixture.StartFrames("Item012");
            fixture.Owner.UndoEditor();
            PumpUntil(() => fixture.IsCurrent && fixture.Owner.EditorSession.Text == text);
            fixture.WaitForPaint();
            using (var frames = fixture.StopFrames("undo")) AssertStablePaint(frames.RootElement, permittedLineShift: 24);
            Assert.True(fixture.Owner.CanEditorRedo);

            fixture.StartFrames("Item012");
            fixture.Owner.RedoEditor();
            PumpUntil(() => fixture.IsCurrent && fixture.Owner.EditorSession.Text == edited);
            fixture.WaitForPaint();
            using (var frames = fixture.StopFrames("redo")) AssertStablePaint(frames.RootElement, permittedLineShift: 24);
            Assert.Equal("true", fixture.Script("modified===window.__observedModel&&monaco.editor.getModels().length===2"));
        });
    }

    [Fact]
    public void EditingModifiedLineRetainsOtherInlinePaintAndContextFolds()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = CreateLines(320);
            var after = "// added one\n// added two\n// added three\n// added four\n// added five\n" + before
                .Replace("Value => 3;", "Value => 303;", StringComparison.Ordinal)
                .Replace("Value => 12;", "Value => 1212;", StringComparison.Ordinal);
            using var fixture = new Fixture(before, after);
            fixture.Owner.DisplayMode = DiffDisplayMode.Context;
            Wait(fixture.Owner.SetEditModeAsync(true));
            fixture.WaitForPaint();
            fixture.Script("right.setPosition({lineNumber:8,column:1});right.pushUndoStop();");
            fixture.WaitForPaint();
            fixture.StartFrames("Item012");
            fixture.Script("right.trigger('keyboard','type',{text:'updated'});right.pushUndoStop();");
            PumpUntil(() => fixture.IsCurrent && fixture.Owner.EditorSession!.Text != after);
            fixture.WaitForPaint();
            using var frames = fixture.StopFrames("modified-context");
            AssertStablePaint(frames.RootElement);
            var expectedLine = "public class Item012 { public int Value => 1212; }";
            Assert.Contains(expectedLine, fixture.Owner.EditorSession!.Text, StringComparison.Ordinal);
            var currentRow = Assert.Single(fixture.Owner.CurrentDocument!.Nodes, node => node.OldIndex == 3);
            var inline = EditorInlineDiffer.Compare(currentRow.OldValue, currentRow.NewValue, false, false, CancellationToken.None);
            var expectedChangedText = string.Concat(inline.NewSegments.Where(segment => segment.IsChanged).Select(segment => segment.Text));
            var painted = fixture.Script("(()=>{const line=[...right.getDomNode().querySelectorAll('.view-lines .view-line')].find(node=>node.textContent.includes('updated'));return {text:line?.textContent,paintedChangedText:[...line.querySelectorAll('.changed-new')].map(node=>node.textContent.replace(/\\u00a0/g,' ')).join(''),modelChangedText:modified.getLineDecorations(8).filter(d=>d.options.inlineClassName==='changed-new').sort((a,b)=>a.range.startColumn-b.range.startColumn).map(d=>modified.getValueInRange(d.range)).join('')};})()");
            Fixture.SaveEvidence("modified-inline", painted);
            using var paintedResult = JsonDocument.Parse(painted);
            Assert.Equal(expectedChangedText, paintedResult.RootElement.GetProperty("paintedChangedText").GetString());
            Assert.Equal(expectedChangedText, paintedResult.RootElement.GetProperty("modelChangedText").GetString());
            Assert.Contains("u", expectedChangedText, StringComparison.Ordinal);
            Assert.True(fixture.Owner.CanEditorUndo);
            fixture.Owner.UndoEditor();
            PumpUntil(() => fixture.IsCurrent && fixture.Owner.EditorSession.Text == after);
        });
    }

    [Fact]
    public void AddedLineBeforeDeletionKeepsRightPlaceholderVisibleAndReusesItsDomNode()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var lines = CreateLines(320).Split('\n');
            var before = string.Join('\n', lines);
            var after = string.Join('\n', lines.Where((_, index) => index is < 5 or > 9))
                .Replace("Value => 12;", "Value => 1212;", StringComparison.Ordinal);
            using var fixture = new Fixture(before, after);
            fixture.Owner.DisplayMode = DiffDisplayMode.Context;
            Wait(fixture.Owner.SetEditModeAsync(true));
            fixture.WaitForPaint();
            fixture.Script("right.setPosition({lineNumber:3,column:1});right.pushUndoStop();");
            fixture.WaitForPaint();
            fixture.StartFrames("Item012", zoneSide: 1);
            fixture.Script("right.trigger('keyboard','type',{text:'\\n'});right.pushUndoStop();");
            PumpUntil(() => fixture.IsCurrent && fixture.Owner.EditorSession!.Text != after);
            fixture.WaitForPaint();
            using var frames = fixture.StopFrames("right-zone-move");
            AssertStablePaint(frames.RootElement, permittedLineShift: 24, requireSameZoneNode: true);
        });
    }

    [Fact]
    public void InsertingLinesBeforeTwoDeletionsKeepsBothGapsAtTheirNewAnchors()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var lines = CreateLines(80).Split('\n');
            var before = string.Join('\n', lines);
            var after = string.Join('\n', lines.Where((_, index) => index is not (10 or 11 or 22 or 23)));
            using var fixture = new Fixture(before, after);
            fixture.Resize(1250, 1200);
            Wait(fixture.Owner.SetEditModeAsync(true));
            fixture.WaitForPaint();
            Assert.Equal(2, fixture.Owner.EditorSession!.Hunks.Count);
            Assert.Equal("true", fixture.Script("Math.abs(left.getTopForLineNumber(13)-right.getTopForLineNumber(11))<1&&Math.abs(left.getTopForLineNumber(25)-right.getTopForLineNumber(21))<1"));
            var inserted = string.Join('\n', Enumerable.Range(1, 10).Select(index => "// inserted " + index.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "\n";
            fixture.Script("right.setPosition({lineNumber:1,column:1});right.pushUndoStop();right.trigger('keyboard','paste',{text:" + JsonSerializer.Serialize(inserted) + ",pasteOnNewLine:false});right.pushUndoStop();");
            PumpUntil(() => fixture.IsCurrent && fixture.Owner.EditorSession.Text.StartsWith(inserted, StringComparison.Ordinal));
            fixture.WaitForPaint();
            var geometry = fixture.Script("(()=>{const root=right.getDomNode().getBoundingClientRect();return {pairs:[[13,21],[25,31],[35,41]].map(([oldLine,newLine])=>({oldLine,newLine,oldTop:left.getTopForLineNumber(oldLine),newTop:right.getTopForLineNumber(newLine)})),expectedGapTops:[left.getTopForLineNumber(11),left.getTopForLineNumber(23)],gaps:[...right.getDomNode().querySelectorAll('.missing-zone')].map(node=>{const box=node.getBoundingClientRect();return {top:box.top-root.top+right.getScrollTop(),height:box.height};}).sort((a,b)=>a.top-b.top)};})()");
            Fixture.SaveEvidence("two-gap-anchor-collision", geometry);
            using var result = JsonDocument.Parse(geometry);
            foreach (var pair in result.RootElement.GetProperty("pairs").EnumerateArray())
                Assert.InRange(Math.Abs(pair.GetProperty("oldTop").GetDouble() - pair.GetProperty("newTop").GetDouble()), 0, 1);
            var gaps = result.RootElement.GetProperty("gaps");
            var expectedTops = result.RootElement.GetProperty("expectedGapTops");
            Assert.Equal(2, gaps.GetArrayLength());
            for (var index = 0; index < 2; index++)
            {
                Assert.InRange(Math.Abs(expectedTops[index].GetDouble() - gaps[index].GetProperty("top").GetDouble()), 0, 1);
                Assert.Equal(48, gaps[index].GetProperty("height").GetDouble());
            }
        });
    }

    private static string CreateLines(int count) => string.Join('\n', Enumerable.Range(1, count)
        .Select(i => $"public class Item{i:D3} {{ public int Value => {i}; }}"));

    private static void AssertStablePaint(JsonElement report, double permittedLineShift = 0, bool requireSameZoneNode = false)
    {
        var baseline = report.GetProperty("baseline");
        var frames = report.GetProperty("frames").EnumerateArray().ToArray();
        Assert.True(frames.Length >= 5, "The check must observe intermediate animation frames, not only settled endpoints.");
        Assert.Contains(frames, frame => frame.GetProperty("pending").GetBoolean() || !frame.GetProperty("current").GetBoolean());
        foreach (var frame in frames)
        {
            Assert.True(frame.GetProperty("lineVisible").GetBoolean(), "The unedited anchor line disappeared during recomputation.");
            Assert.Equal(baseline.GetProperty("background").GetString(), frame.GetProperty("background").GetString());
            Assert.Equal(baseline.GetProperty("textPaint").GetString(), frame.GetProperty("textPaint").GetString());
            Assert.True(frame.GetProperty("sameModel").GetBoolean());
            Assert.InRange(Math.Abs(frame.GetProperty("top").GetDouble() - baseline.GetProperty("top").GetDouble()), 0, permittedLineShift + 1);
            Assert.InRange(Math.Abs(frame.GetProperty("scrollTop").GetDouble() - baseline.GetProperty("scrollTop").GetDouble()), 0, 1);
            Assert.InRange(Math.Abs(frame.GetProperty("scrollHeight").GetDouble() - baseline.GetProperty("scrollHeight").GetDouble()), 0, permittedLineShift + 1);
            Assert.True(frame.GetProperty("missingZoneHeight").GetDouble() > 0, "The BASE placeholder must not disappear between projections.");
            if (requireSameZoneNode) Assert.True(frame.GetProperty("sameZoneNode").GetBoolean(), "Moving an existing missing range must retain its visible DOM node.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SynchronizationContext? _oldContext = SynchronizationContext.Current;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.Editor.Stability", Guid.NewGuid().ToString("N"));
        private readonly ComparisonLoadScheduler _scheduler = new();
        private readonly ExternalDiffSessionStore _store;
        private readonly ExternalDiffSession _session;
        private readonly CodeEditorView _view;
        private readonly Window _window;

        public Fixture(string before, string after)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Directory.CreateDirectory(_directory);
            var basePath = Path.Combine(_directory, "base.cs");
            var minePath = Path.Combine(_directory, "mine.cs");
            File.WriteAllText(basePath, before);
            File.WriteAllText(minePath, after);
            _store = new ExternalDiffSessionStore(Path.Combine(_directory, "sessions"));
            _session = Wait(_store.CreateAsync(new ExternalDiffRequest { BaseFilePath = basePath, MineFilePath = minePath }, CancellationToken.None));
            Owner = new DiffWindowViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]), _scheduler,
                _session, NullLogger<DiffWindowViewModel>.Instance, _ => { });
            Owner.EnableDocumentEditor();
            Wait(Owner.LoadAsync());
            _view = new CodeEditorView { DataContext = Owner };
            _window = new Window
            {
                Content = _view,
                Width = 1250,
                Height = 720,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            _window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Light.xaml"));
            _window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Metrics.xaml"));
            _window.Show();
            PumpUntil(() => _view.IsEditorReady && IsCurrent);
            Browser = (WebView2)_view.FindName("Browser");
        }

        public DiffWindowViewModel Owner { get; }
        public WebView2 Browser { get; }
        public bool IsCurrent => Owner.EditorResultsCurrent && _view.RenderedVersion == Owner.EditorSession!.Version;
        public string Script(string script) => Wait(Browser.ExecuteScriptAsync(script));
        public void Resize(double width, double height) { _window.Width = width; _window.Height = height; _window.UpdateLayout(); }
        public void WaitForPaint()
        {
            Script("window.__paintWait=false;setTimeout(()=>requestAnimationFrame(()=>window.__paintWait=true),300)");
            PumpUntil(() => Script("window.__paintWait===true") == "true");
        }

        public void StartFrames(string anchor, int zoneSide = 0)
        {
            Script("window.__anchor=" + JsonSerializer.Serialize(anchor) + ";window.__zoneEditor=" + (zoneSide == 0 ? "left" : "right") + ";");
            Script("""
                window.__observedModel=modified;
                window.__observedZone=window.__zoneEditor.getDomNode().querySelector('.missing-zone');
                window.__readPaint=()=>{
                  const lines=[...right.getDomNode().querySelectorAll('.view-lines .view-line')];
                  const line=lines.find(node=>node.textContent.includes(window.__anchor));
                  const box=line?.getBoundingClientRect(),root=right.getDomNode().getBoundingClientRect();
                  const backgrounds=[...right.getDomNode().querySelectorAll('.view-overlays .diff-line')];
                  const background=box?backgrounds.find(node=>{const r=node.getBoundingClientRect();return r.top<=box.top+2&&r.bottom>=box.top+2;}):null;
                  const paints=line?[...line.querySelectorAll('span')].filter(node=>!node.children.length&&node.textContent.trim()).map(node=>{const s=getComputedStyle(node);return [node.textContent,s.color,s.fontWeight].join('|');}):[];
                  const zone=[...window.__zoneEditor.getDomNode().querySelectorAll('.missing-zone')].reduce((sum,node)=>sum+node.getBoundingClientRect().height,0);
                  return {t:performance.now(),pending:hasPendingText,current:projectionVersion===version&&!hasPendingText,
                    lineVisible:!!line,top:box?box.top-root.top:-1,scrollTop:right.getScrollTop(),scrollHeight:right.getScrollHeight(),background:background?getComputedStyle(background).backgroundColor:'none',
                    textPaint:paints.join(';'),sameModel:modified===window.__observedModel,missingZoneHeight:zone,
                    sameZoneNode:window.__zoneEditor.getDomNode().querySelector('.missing-zone')===window.__observedZone};
                };
                window.__baselinePaint=window.__readPaint();window.__paintFrames=[];window.__captureFrames=true;
                window.__capturePaint=()=>{if(!window.__captureFrames)return;window.__paintFrames.push(window.__readPaint());requestAnimationFrame(window.__capturePaint);};
                requestAnimationFrame(window.__capturePaint);
                """);
        }

        public JsonDocument StopFrames(string name)
        {
            var json = Script("window.__captureFrames=false;({baseline:window.__baselinePaint,frames:window.__paintFrames})");
            SaveEvidence(name, json);
            return JsonDocument.Parse(json);
        }

        public static void SaveEvidence(string name, string json)
        {
            var output = Environment.GetEnvironmentVariable("DIFFER_FLICKER_EVIDENCE") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Differ", "WorkspaceMaintenance", "editor-flicker-20261026");
            Directory.CreateDirectory(output);
            using var document = JsonDocument.Parse(json);
            File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(document.RootElement, EvidenceOptions));
        }

        public void Dispose()
        {
            Owner.Dispose();
            _window.Close();
            _store.TryDelete(_session);
            _scheduler.Dispose();
            Directory.Delete(_directory, true);
            SynchronizationContext.SetSynchronizationContext(_oldContext);
        }
    }

    private static T Wait<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Editor stability probe did not reach the expected state.");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
