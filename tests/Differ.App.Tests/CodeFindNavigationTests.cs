using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Differ.Formats.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Wpf;

namespace Differ.App.Tests;

[Collection("UI performance")]
public sealed class CodeFindNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaseOnlyLongLineHitUsesUtf16RangeAndCanBeRevealedAgainWithoutMovingFocusOrSelection(bool shortCurrentSide)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Lines();
            var after = before.ToArray();
            before[7] = "-- 😀 e\u0301 " + new string('x', 240) + " NEEDLE tail";
            after[7] = shortCurrentSide ? "-- current short line" : "-- 😀 e\u0301 " + new string('x', 240) + " AFTERX tail";
            using var fixture = new Fixture(before, after);
            fixture.Script("left.setSelection(new monaco.Range(2,2,2,5));right.setSelection(new monaco.Range(3,2,3,6));window.findOriginalSelection=JSON.stringify([left.getSelection(),right.getSelection()]);window.findOriginalHistory=modified.getAlternativeVersionId();");
            fixture.FocusFindInput();
            fixture.Owner.FindText = "NEEDLE";
            fixture.FindNext();
            var column = before[7].IndexOf("NEEDLE", StringComparison.Ordinal) + 1;
            fixture.WaitForMatch(0, 8, column, 6);
            Assert.NotNull(fixture.Owner.SelectedTextRow?.OldIndex);
            Assert.NotNull(fixture.Owner.SelectedTextRow?.NewIndex);
            PumpUntil(() => fixture.Script("!pairedScrollFrame&&left.getScrollLeft()>0&&Math.abs(left.getScrollLeft()-right.getScrollLeft())<1") == "true");
            Assert.True(fixture.Script("left.getScrollLeft()>0&&Math.abs(left.getScrollLeft()-right.getScrollLeft())<1") == "true",
                fixture.Script("JSON.stringify({left:left.getScrollLeft(),right:right.getScrollLeft(),leftWidth:left.getScrollWidth(),rightWidth:right.getScrollWidth(),leftLayout:left.getLayoutInfo(),rightLayout:right.getLayoutInfo()})"));
            Assert.Equal(1, fixture.Owner.FindMatchCount);
            fixture.AssertFindInputFocus();

            fixture.Script("right.revealLineInCenter(105);right.setScrollLeft(0);");
            PumpUntil(() => fixture.Script("left.getScrollLeft()===0&&right.getVisibleRanges()[0].startLineNumber>8") == "true");
            fixture.FindNext();
            fixture.WaitForMatch(0, 8, column, 6);
            PumpUntil(() => fixture.Script("left.getScrollLeft()>0&&left.getVisibleRanges().some(r=>r.startLineNumber<=8&&r.endLineNumber>=8)") == "true");
            fixture.AssertFindInputFocus();
            Assert.Equal("true", fixture.Script("window.findOriginalSelection===JSON.stringify([left.getSelection(),right.getSelection()])&&window.findOriginalHistory===modified.getAlternativeVersionId()"));
            var initialScrollWidths = fixture.Script("[left.getScrollWidth(),right.getScrollWidth()]");
            for (var cycle = 0; cycle < 3; cycle++)
            {
                fixture.Resize(720, 620);
                fixture.FindNext();
                fixture.WaitForMatch(0, 8, column, 6);
                fixture.Resize(1200, 700);
                fixture.FindNext();
                fixture.WaitForMatch(0, 8, column, 6);
                fixture.SettleRenderer();
                Assert.Equal(initialScrollWidths, fixture.Script("[left.getScrollWidth(),right.getScrollWidth()]"));
            }
            fixture.SetReadingScale(1.5);
            fixture.FindNext();
            fixture.WaitForMatch(0, 8, column, 6);
            fixture.SetReadingScale(1);
            fixture.FindNext();
            fixture.WaitForMatch(0, 8, column, 6);
            fixture.SettleRenderer();
            var beforeScaleWidths = JsonSerializer.Deserialize<int[]>(initialScrollWidths)!;
            var restoredWidths = JsonSerializer.Deserialize<int[]>(fixture.Script("[left.getScrollWidth(),right.getScrollWidth()]"))!;
            Assert.Equal(restoredWidths[0], restoredWidths[1]);
            // Font remeasurement may discard a conservative native width; returning to the original scale must not inflate it.
            Assert.InRange(restoredWidths[0], 1, beforeScaleWidths[0]);
            for (var cycle = 0; cycle < 2; cycle++)
            {
                fixture.SetReadingScale(1.5);
                fixture.FindNext();
                fixture.WaitForMatch(0, 8, column, 6);
                fixture.SetReadingScale(1);
                fixture.FindNext();
                fixture.WaitForMatch(0, 8, column, 6);
                fixture.SettleRenderer();
                Assert.Equal(restoredWidths, JsonSerializer.Deserialize<int[]>(fixture.Script("[left.getScrollWidth(),right.getScrollWidth()]")));
            }
            Assert.Equal(string.Join('\n', after), fixture.Owner.EditorSession!.Text);
            Assert.False(fixture.Owner.IsEditorDirty);
        });
    }

    [Fact]
    public void FindPreservesLogicalRowCountingAndClearsObsoleteQueryAndClosedPanelFeedback()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Lines();
            var after = before.ToArray();
            before[7] = "-- NEEDLE baseline NEEDLE";
            after[7] = "-- current NEEDLE and NEEDLE";
            using var fixture = new Fixture(before, after);
            fixture.Owner.FindText = "NEEDLE";
            fixture.FindNext();
            fixture.WaitForMatch(1, 8, after[7].IndexOf("NEEDLE", StringComparison.Ordinal) + 1, 6);
            // This merge preserves the original one-hit-per-logical-row search contract.
            Assert.Equal(1, fixture.Owner.FindMatchCount);
            var stale = fixture.LastRevealFind!;
            fixture.Owner.FindText = "absent";
            fixture.FindNext();
            fixture.WaitForNoMatch();
            fixture.Owner.SendEditorCommand(stale);
            fixture.SettleRenderer();
            fixture.WaitForNoMatch();
            Assert.Equal(0, fixture.Owner.FindMatchCount);

            fixture.Owner.FindText = "NEEDLE";
            fixture.FindNext();
            fixture.WaitForMatch(1, 8, after[7].IndexOf("NEEDLE", StringComparison.Ordinal) + 1, 6);
            fixture.Owner.IsFindOpen = false;
            fixture.WaitForNoMatch();
            Assert.Equal("true", fixture.Script("original.getValue().includes('baseline')&&modified.getValue().includes('current')"));
        });
    }

    [Fact]
    public void FindRevealsFoldedContextAndRejectsWrongSessionOrVersionAndPendingEdits()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Lines();
            before[89] = "-- hidden NEEDLE";
            var after = before.ToArray();
            after[7] = "local row8 = -8";
            using var fixture = new Fixture(before, after, 720, 620, 1.5);
            fixture.Owner.DisplayMode = DiffDisplayMode.Context;
            PumpUntil(() => fixture.Script("displayMode===2&&foldRanges[1].length>0") == "true");
            fixture.Owner.FindText = "NEEDLE";
            fixture.FindNext();
            fixture.WaitForMatch(1, 90, 11, 6);
            PumpUntil(() => fixture.Script("right.getVisibleRanges().some(r=>r.startLineNumber<=90&&r.endLineNumber>=90)") == "true");

            fixture.Owner.FindDifferencesOnly = true;
            fixture.FindNext();
            fixture.WaitForNoMatch();
            Assert.Equal(0, fixture.Owner.FindMatchCount);
            Assert.Equal("true", fixture.Script("(()=>{revealFind({session:'old-session',version,findVersion:999,mine:true,line:90,column:11,length:6});revealFind({session,version:version-1,findVersion:999,mine:true,line:90,column:11,length:6});return findDecorations.every(a=>a.length===0);})()"));

            fixture.Owner.FindDifferencesOnly = false;
            fixture.FindNext();
            fixture.WaitForMatch(1, 90, 11, 6);
            Wait(fixture.Owner.SetEditModeAsync(true));
            fixture.Script("right.executeEdits('find-test',[{range:new monaco.Range(90,1,90,1),text:'-- edit '}]);");
            fixture.WaitForNoMatch();
            PumpUntil(() => fixture.Owner.EditorResultsCurrent && fixture.Owner.IsEditorDirty);
            fixture.WaitForNoMatch();
        });
    }

    [Fact]
    public void ClearingTheQueryCancelsAFindRevealWaitingForUnfold()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Lines();
            var after = before.ToArray();
            before[7] = "-- BASE NEEDLE";
            after[7] = "-- current result";
            using var fixture = new Fixture(before, after);
            fixture.Script("right.revealLineInCenter(100);window.savedFindGetAction=left.getAction.bind(left);window.pendingFindUnfolds=[];left.getAction=id=>id==='editor.unfoldAll'?{run:()=>new Promise(resolve=>pendingFindUnfolds.push(resolve))}:savedFindGetAction(id);");
            fixture.Owner.FindText = "NEEDLE";
            fixture.FindNext();
            PumpUntil(() => fixture.Script("pendingFindUnfolds.length>0") == "true");
            fixture.Owner.FindText = string.Empty;
            fixture.WaitForNoMatch();
            fixture.Script("left.getAction=window.savedFindGetAction;for(const resolve of pendingFindUnfolds)resolve();");
            // Await a renderer frame after releasing the asynchronous unfold continuations.
            fixture.Script("window.findContinuationFinished=false;requestAnimationFrame(()=>window.findContinuationFinished=true);");
            PumpUntil(() => fixture.Script("window.findContinuationFinished") == "true");
            Assert.Equal("true", fixture.Script("right.getVisibleRanges()[0].startLineNumber>8&&findDecorations.every(a=>a.length===0)"));
        });
    }

    [Fact]
    public void LoadingShortDocumentsDropsTheSharedWidthAndRejectsThePreviousSessionFind()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Lines();
            before[7] = "-- " + new string('x', 240) + " NEEDLE";
            var after = before.ToList();
            after[7] = "-- current short line";
            after.Insert(0, "-- inserted line");
            using var fixture = new Fixture(before, after.ToArray());
            fixture.Owner.FindText = "NEEDLE";
            fixture.FindNext();
            fixture.WaitForMatch(0, 8, before[7].IndexOf("NEEDLE", StringComparison.Ordinal) + 1, 6);
            var previousFind = fixture.LastRevealFind!;
            fixture.Owner.SendEditorCommand(new
            {
                type = "load",
                session = Guid.NewGuid().ToString("N"),
                version = 1,
                original = "short base",
                modified = "short result",
                editMode = false,
                language = "plaintext",
                dark = false
            });
            PumpUntil(() => fixture.Script("original.getValue()==='short base'&&modified.getValue()==='short result'&&left.getScrollLeft()===0&&right.getScrollLeft()===0&&sharedContentWidth===0&&horizontalExtentZones.every(z=>z===null)") == "true");
            fixture.Owner.SendEditorCommand(previousFind);
            fixture.SettleRenderer();
            fixture.WaitForNoMatch();
            Assert.Equal("true", fixture.Script("left.getScrollWidth()===left.getLayoutInfo().contentWidth&&right.getScrollWidth()===right.getLayoutInfo().contentWidth"));
        });
    }

    private static string[] Lines() => Enumerable.Range(1, 140).Select(i => $"local row{i} = {i}").ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.Find.Tests", Guid.NewGuid().ToString("N"));
        private readonly ComparisonLoadScheduler _scheduler = new();
        private readonly ExternalDiffSessionStore _store;
        private readonly ExternalDiffSession _session;
        private readonly Window _window;
        private readonly TextBox _findInput = new() { Name = "FindInput", Text = "NEEDLE", Height = 30 };
        private readonly CodeEditorView _view;

        public Fixture(string[] before, string[] after, double width = 1200, double height = 700, double scale = 1)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Directory.CreateDirectory(_directory);
            var basePath = Path.Combine(_directory, "base.lua");
            var minePath = Path.Combine(_directory, "mine.lua");
            File.WriteAllText(basePath, string.Join('\n', before));
            File.WriteAllText(minePath, string.Join('\n', after));
            _store = new ExternalDiffSessionStore(Path.Combine(_directory, "sessions"));
            _session = Wait(_store.CreateAsync(new ExternalDiffRequest { BaseFilePath = basePath, MineFilePath = minePath }, CancellationToken.None));
            Owner = new DiffWindowViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]), _scheduler,
                _session, NullLogger<DiffWindowViewModel>.Instance, _ => { });
            Owner.EnableDocumentEditor();
            Wait(Owner.LoadAsync());
            _view = new CodeEditorView { DataContext = Owner, ReadingScale = scale };
            var panel = new DockPanel();
            DockPanel.SetDock(_findInput, Dock.Top);
            panel.Children.Add(_findInput);
            panel.Children.Add(_view);
            _window = new Window
            {
                Content = panel,
                Width = width,
                Height = height,
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
            Browser = (WebView2)_view.FindName("Browser");
            Owner.EditorCommandRequested += (_, command) =>
            {
                if (command.GetType().GetProperty("type")?.GetValue(command) is "revealFind") LastRevealFind = command;
            };
            Owner.IsFindOpen = true;
        }

        public DiffWindowViewModel Owner { get; }
        public WebView2 Browser { get; }
        public object? LastRevealFind { get; private set; }
        public string Script(string script) => Wait(Browser.ExecuteScriptAsync(script));
        public void FindNext() => Wait(((IAsyncRelayCommand)Owner.FindNextCommand).ExecuteAsync(null));
        public void FocusFindInput() => FocusManager.SetFocusedElement(_window, _findInput);
        public void AssertFindInputFocus() => Assert.Same(_findInput, FocusManager.GetFocusedElement(_window));
        public void Resize(double width, double height) { _window.Width = width; _window.Height = height; _window.UpdateLayout(); SettleRenderer(); }
        public void SetReadingScale(double scale) { _view.ReadingScale = scale; SettleRenderer(); }
        public void WaitForNoMatch() => PumpUntil(() => Script("findDecorations.every(a=>a.length===0)") == "true");
        public void SettleRenderer()
        {
            Script("window.findTestFrame=false;requestAnimationFrame(()=>window.findTestFrame=true);");
            PumpUntil(() => Script("window.findTestFrame") == "true");
        }
        public void WaitForMatch(int side, int line, int column, int length)
        {
            PumpUntil(() => Script($"(()=>{{const r={(side == 0 ? "original" : "modified")}.getDecorationRange(findDecorations[{side}][0]);return findDecorations[{side}].length===1&&findDecorations[{1 - side}].length===0&&r?.startLineNumber==={line}&&r.endLineNumber==={line}&&r.startColumn==={column}&&r.endColumn==={column + length};}})()") == "true");
            using var range = JsonDocument.Parse(Script($"{(side == 0 ? "original" : "modified")}.getDecorationRange(findDecorations[{side}][0])"));
            Assert.Equal(line, range.RootElement.GetProperty("startLineNumber").GetInt32());
            Assert.Equal(line, range.RootElement.GetProperty("endLineNumber").GetInt32());
            Assert.Equal(column, range.RootElement.GetProperty("startColumn").GetInt32());
            Assert.Equal(column + length, range.RootElement.GetProperty("endColumn").GetInt32());
            try
            {
                PumpUntil(() => Script($"(()=>{{const editor={(side == 0 ? "left" : "right")},p=editor.getScrolledVisiblePosition({{lineNumber:{line},column:{column}}}),layout=editor.getLayoutInfo();return !!p&&p.top>=0&&p.top<layout.height&&p.left>=layout.contentLeft&&p.left<layout.width;}})()") == "true");
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(Script($"JSON.stringify({{target:pairedScrollTarget,width:sharedContentWidth,zones:horizontalExtentZones,left:[left.getScrollLeft(),left.getScrollTop(),left.getScrollWidth()],right:[right.getScrollLeft(),right.getScrollTop(),right.getScrollWidth()],position:left.getScrolledVisiblePosition({{lineNumber:{line},column:{column}}}),layout:left.getLayoutInfo()}})"), exception);
            }
        }

        public void Dispose()
        {
            Owner.Dispose();
            _window.Close();
            _store.TryDelete(_session);
            _scheduler.Dispose();
            Directory.Delete(_directory, true);
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
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Find navigation did not reach the expected state.");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
