using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Formats.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Differ.App.Tests;

[Collection("UI performance")]
public sealed class TextMergeRuntimeTests
{
    private static readonly int[] InteractionConflictIndexes = [2, 8, 14];
    [Fact]
    public void RealEditorResolvesConflictsFlushesEditsUndoesAndSavesOnlyOutput()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var oldContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var directory = Path.Combine(Path.GetTempPath(), "Differ.Merge.Runtime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var basePath = Path.Combine(directory, "共同基础.json");
            var minePath = Path.Combine(directory, "我的配置.json");
            var theirsPath = Path.Combine(directory, "对方配置.json");
            var output = Path.Combine(directory, "合并结果.json");
            var lines = Enumerable.Range(0, 500).Select(i => $"  \"setting_{i:0000}\": \"中文上下文 {i}\",").ToArray();
            lines[0] = "{"; lines[^1] = "}";
            lines[5] = "  \"title\": \"议事厅\",";
            lines[9] = "  \"level\": 10,";
            lines[15] = "  \"description\": \"" + new string('长', 300) + "\",";
            var mine = lines.ToArray(); var theirs = lines.ToArray();
            mine[5] = "  \"title\": \"议事厅 · 我的修改\",";
            theirs[5] = "  \"title\": \"议事厅 · 对方修改\",";
            mine[9] = "  \"level\": 20,"; theirs[9] = "  \"level\": 30,";
            mine[10] = "  \"speed\": 120,"; theirs[10] = "  \"speed\": 140,";
            mine[11] = "  \"attack\": 25,"; theirs[11] = "  \"attack\": 35,";
            mine[20] = "  \"mine_only\": true,"; theirs[30] = "  \"theirs_only\": true,";
            var basisText = string.Join('\n', lines) + "\n";
            var mineText = string.Join('\n', mine) + "\n";
            var theirsText = string.Join('\n', theirs) + "\n";
            File.WriteAllText(basePath, basisText, new UTF8Encoding(false));
            File.WriteAllText(minePath, mineText, new UTF8Encoding(false));
            File.WriteAllText(theirsPath, theirsText, new UTF8Encoding(false));
            var session = Wait(TextMergeSession.OpenAsync(basePath, minePath, theirsPath, output));
            var owner = new TextMergeViewModel(session);
            var resources = new ResourceDictionary();
            using var theme = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Light));
            theme.InitializeFromSystem();
            var preferencesPath = Path.Combine(directory, "preferences.json");
            using var preferences = new UserPreferencesService(preferencesPath);
            var window = new MergeWindow(owner, theme, preferences) { Width = 1500, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Resources.MergedDictionaries.Add(resources);
            try
            {
                window.Show();
                var browser = (WebView2)window.FindName("Browser");
                PumpUntil(() => window.IsEditorReady && Script(browser, "!!state && !!models.result") == "true");
                Settle(browser);
                Assert.Equal(2, session.UnresolvedCount);
                Assert.Equal("false", Script(browser, "state.autoAdvance"));
                Assert.Equal("true", Script(browser, "['mine','base','theirs'].every(k=>editors[k].getOption(monaco.editor.EditorOption.readOnly))"));
                Assert.Contains("mine_only", session.ResultText, StringComparison.Ordinal);
                Assert.Contains("theirs_only", session.ResultText, StringComparison.Ordinal);
                Assert.False(File.Exists(output));
                AssertSourceColumns(browser, false);
                Capture(browser, "merge-default-hidden-light-wide");
                ToggleBase(browser, true);
                AssertSourceColumns(browser, true);
                Capture(browser, "merge-show-base-light-wide");
                // A rejected version must preserve the editor draft and release the IPC queue.
                Script(browser, "version=-1;editors.result.executeEdits('runtime',[{range:new monaco.Range(1,1,1,1),text:'// 未确认草稿\\n'}])");
                Idle(browser);
                Assert.Equal("true", Script(browser, "rejectedDraft && models.result.getValue().startsWith('// 未确认草稿')"));
                Assert.DoesNotContain("未确认草稿", session.ResultText, StringComparison.Ordinal);
                Assert.Throws<InvalidOperationException>(() => Wait(window.FlushAsync()));
                Script(browser, "editors.result.executeEdits('runtime',[{range:new monaco.Range(1,1,2,1),text:'// 修复后的草稿\\n'}])");
                Idle(browser);
                Assert.Equal("false", Script(browser, "rejectedDraft"));
                Assert.StartsWith("// 修复后的草稿\n", session.ResultText);
                Script(browser, "requestCommand('undo')"); Idle(browser);
                Assert.Equal(2, session.UnresolvedCount);
                Capture(browser, "merge-before-light-wide");
                Script(browser, "requestCommand('next')"); Idle(browser); Settle(browser);
                AssertContinuousBlockChrome(browser);
                Capture(browser, "merge-multiline-light-wide");
                Script(browser, "requestCommand('previous')"); Idle(browser);
                AssertButtonsInsideViewport(browser);
                theme.Apply(AppTheme.Dark); window.SendTheme(); Settle(browser);
                Capture(browser, "merge-before-dark-wide");
                ToggleBase(browser, false);
                AssertSourceColumns(browser, false);
                Capture(browser, "merge-default-hidden-dark-wide");
                ToggleBase(browser, true);
                Script(browser, "requestCommand('next')"); Idle(browser); Settle(browser);
                AssertContinuousBlockChrome(browser);
                Capture(browser, "merge-multiline-dark-wide");
                Script(browser, "requestCommand('previous')"); Idle(browser);
                window.Width = 720; window.UpdateLayout(); Settle(browser);
                AssertButtonsInsideViewport(browser);
                Assert.Equal("true", Script(browser, "getComputedStyle(document.getElementById('sourceTabs')).display==='flex'"));
                Script(browser, "showSource('base');editors.base.focus()"); Settle(browser);
                ToggleBase(browser, false);
                Assert.Equal("true", Script(browser, "activeSource==='mine'&&lastFocused==='mine'&&!isEditorVisible('base')&&getComputedStyle(document.querySelector('[data-source=base]')).display==='none'"));
                Script(browser, "window.visibleMineTop=editors.mine.getScrollTop();editors.base.setScrollTop(1000)"); Settle(browser);
                Assert.Equal("true", Script(browser, "editors.mine.getScrollTop()===visibleMineTop"));
                Script(browser, "setScale(1.5)"); Settle(browser);
                Capture(browser, "merge-default-hidden-dark-narrow-150-reading");
                AssertButtonsInsideViewport(browser);
                theme.Apply(AppTheme.Light); window.SendTheme(); Settle(browser);
                Capture(browser, "merge-default-hidden-light-narrow-150-reading");
                theme.Apply(AppTheme.Dark); window.SendTheme();
                ToggleBase(browser, true);
                Script(browser, "showSource('theirs')");
                Assert.Equal("true", Script(browser, "document.querySelector('[data-pane=theirs]').classList.contains('active')"));
                Script(browser, "setScale(1.5)"); Settle(browser);
                Capture(browser, "merge-before-dark-narrow-150-reading");
                AssertButtonsInsideViewport(browser);
                Script(browser, "setScale(1)"); window.Width = 1500; window.UpdateLayout();
                theme.Apply(AppTheme.Light); window.SendTheme(); Settle(browser);
                Script(browser, "requestCommand('mine')"); Idle(browser);
                Assert.Equal(1, session.UnresolvedCount);
                Assert.Contains("我的修改", session.ResultText, StringComparison.Ordinal);
                AssertTogglePreservesResult(browser, owner);
                Script(browser, "requestCommand('undo')"); Idle(browser);
                Assert.Equal(2, session.UnresolvedCount);
                Script(browser, "requestCommand('redo')"); Idle(browser);
                Assert.Equal(1, session.UnresolvedCount);
                Script(browser, "requestCommand('next')"); Idle(browser);
                // Same JavaScript turn edits and confirms; the action must wait for its text acknowledgement.
                Script(browser, "{ const b=state.blocks.find(b=>b.index===state.selected);const p=models.result.getPositionAt(b.offset);const q=models.result.getPositionAt(b.offset+b.length);editors.result.executeEdits('runtime',[{range:new monaco.Range(p.lineNumber,p.column,q.lineNumber,q.column),text:'  \\\"level\\\": 25,\\n'}]);requestCommand('confirm'); }");
                Idle(browser);
                Assert.Equal(0, session.UnresolvedCount);
                Assert.Contains("25", session.ResultText, StringComparison.Ordinal);
                AssertTogglePreservesResult(browser, owner);
                Script(browser, "requestCommand('undo')"); Idle(browser);
                Assert.Equal(1, session.UnresolvedCount);
                Script(browser, "requestCommand('redo')"); Idle(browser);
                Assert.Equal(0, session.UnresolvedCount);
                Capture(browser, "merge-resolved-light-wide");
                // A pending edit outside conflicts must be included by the direct host save path too.
                Script(browser, "editors.result.executeEdits('runtime',[{range:new monaco.Range(1,1,1,1),text:'// 保存前即时输入\\n'}])");
                Assert.True(Wait(window.SaveResultAsync(false)));
                Assert.StartsWith("// 保存前即时输入\n", File.ReadAllText(output));
                Assert.Equal(basisText, File.ReadAllText(basePath));
                Assert.Equal(mineText, File.ReadAllText(minePath));
                Assert.Equal(theirsText, File.ReadAllText(theirsPath));
                Assert.False(window.CompletionSucceeded);
                Assert.False(session.IsDirty);
                using (var preferenceLock = new FileStream(preferencesPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    Script(browser, "el('showBase').click()");
                    var finishing = window.SaveResultAsync(true);
                    PumpUntil(() => Script(browser, "locked") == "true");
                    Assert.False(finishing.IsCompleted);
                    Script(browser, "setShowBase(true,true)");
                    Assert.Equal("false", Script(browser, "showBase"));
                    preferenceLock.Dispose();
                    Assert.True(Wait(finishing));
                }
                Assert.True(window.CompletionSucceeded);
                AssertReopenedPreference(session, theme, resources, preferencesPath, false);
                Wait(preferences.SetMergeShowBaseAsync(true));
                AssertReopenedPreference(session, theme, resources, preferencesPath, true);
                AssertPreferenceFailureIsIndependent(session, theme, resources, directory);
            }
            finally
            {
                if (window.IsVisible) window.CloseForTest();
                Directory.Delete(directory, true);
                SynchronizationContext.SetSynchronizationContext(oldContext);
            }
        });
    }

    private static void AssertButtonsInsideViewport(WebView2 browser)
    {
        Assert.Equal("true", Script(browser, "['previous','next','autoAdvance','showBase','mineChoice','baseChoice','theirsChoice','confirm','save','finish','undo','redo'].every(id=>{const r=document.getElementById(id).getBoundingClientRect();return r.width>0&&r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight})"));
        Assert.Equal("true", Script(browser, "{const ids=['previous','selectionStatus','next','mineChoice','baseChoice','theirsChoice','confirm','autoAdvance'];const row=el('resolveBar').getBoundingClientRect();const rects=ids.map(id=>el(id).getBoundingClientRect());rects.every((r,i)=>r.top>=row.top&&r.bottom<=row.bottom&&(i===0||r.left>=rects[i-1].right))&&document.querySelectorAll('#previous').length===1&&!el('toolbar').contains(el('next'))&&el('counter').parentElement.classList.contains('resultHeader')}"));
    }

    [Fact]
    public void DecisionRowAutoAdvanceAndResultClicksPreserveTheIntendedConflict()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var oldContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var lines = Enumerable.Range(0, 100).Select(i => $"context_{i}").ToArray();
            var mine = lines.ToArray(); var theirs = lines.ToArray();
            foreach (var index in InteractionConflictIndexes) { mine[index] = $"mine_{index}"; theirs[index] = $"theirs_{index}"; }
            try
            {
                using (var fixture = new InteractionFixture(string.Join('\n', lines), string.Join('\n', mine), string.Join('\n', theirs)))
                {
                    var browser = fixture.Browser;
                    var session = fixture.Session;
                    var owner = fixture.Window.ViewModel;
                    Assert.Equal(3, session.UnresolvedCount);
                    AssertButtonsInsideViewport(browser);
                    var actionPositions = Script(browser, "['previous','next','mineChoice','baseChoice','theirsChoice','confirm'].map(id=>el(id).getBoundingClientRect().x)");
                    Script(browser, "el('mineChoice').dispatchEvent(new MouseEvent('click',{detail:1}))"); Idle(browser);
                    Assert.Equal(0, owner.SelectedBlock);
                    Assert.Equal(2, session.UnresolvedCount);
                    Script(browser, "requestCommand('next')"); Idle(browser);
                    Script(browser, "el('autoAdvance').click()"); Idle(browser);
                    Assert.True(owner.AutoAdvance);
                    Script(browser, "el('theirsChoice').dispatchEvent(new MouseEvent('click',{detail:1}))"); Idle(browser);
                    Assert.Equal(2, owner.SelectedBlock);
                    Assert.Equal(1, session.UnresolvedCount);
                    var beforeDoubleClick = session.Version;
                    Script(browser, "el('theirsChoice').dispatchEvent(new MouseEvent('click',{detail:2}))"); Settle(browser);
                    Assert.Equal(beforeDoubleClick, session.Version);
                    Assert.Equal(1, session.UnresolvedCount);
                    Assert.Equal("true", Script(browser, "{const repeat=new KeyboardEvent('keydown',{bubbles:true,cancelable:true,key:'Enter',repeat:true});el('theirsChoice').dispatchEvent(repeat);repeat.defaultPrevented}"));
                    Assert.Equal(beforeDoubleClick, session.Version);
                    Script(browser, "requestCommand('undo')"); Idle(browser);
                    Assert.Equal(1, owner.SelectedBlock);
                    Assert.Equal(2, session.UnresolvedCount);
                    Script(browser, "requestCommand('redo')"); Idle(browser);
                    Assert.Equal(1, owner.SelectedBlock);
                    Assert.Equal(1, session.UnresolvedCount);
                    Script(browser, "requestCommand('next')"); Idle(browser);
                    Script(browser, "el('baseChoice').click()"); Idle(browser);
                    Assert.Equal(2, owner.SelectedBlock);
                    Assert.Equal(0, session.UnresolvedCount);
                    Assert.Equal(actionPositions, Script(browser, "['previous','next','mineChoice','baseChoice','theirsChoice','confirm'].map(id=>el(id).getBoundingClientRect().x)"));
                    // An edit followed immediately by a result click drains its text first.
                    Script(browser, "editors.result.executeEdits('runtime',[{range:new monaco.Range(1,1,1,1),text:'prefix\\n'}]);const p=models.result.getPositionAt(models.result.getValue().indexOf('theirs_8'));editors.result.setPosition(p);editors.result.setScrollTop(0);window.clickCaret=JSON.stringify(editors.result.getSelection());window.clickScroll=editors.result.getScrollTop();resultPointerDown={x:20,y:20};handleResultMouseUp({target:{position:p},event:{browserEvent:{button:0,detail:1,clientX:20,clientY:20}}})"); Idle(browser);
                    Assert.StartsWith("prefix\n", session.ResultText);
                    Assert.Equal(1, owner.SelectedBlock);
                    Assert.Equal("true", Script(browser, "clickCaret===JSON.stringify(editors.result.getSelection())&&clickScroll===editors.result.getScrollTop()"));
                    // Text drag/copy and a collapsed click in normal context keep the current target.
                    Script(browser, "editors.result.setSelection(new monaco.Range(1,1,4,3));resultPointerDown={x:20,y:20};handleResultMouseUp({target:{position:{lineNumber:4,column:1}},event:{browserEvent:{button:0,detail:1,clientX:20,clientY:20}}})"); Settle(browser);
                    Assert.Equal(1, owner.SelectedBlock);
                    Assert.Equal("false", Script(browser, "editors.result.getSelection().isEmpty()"));
                    Script(browser, "editors.result.setPosition({lineNumber:2,column:1});resultPointerDown={x:20,y:20};handleResultMouseUp({target:{position:{lineNumber:2,column:1}},event:{browserEvent:{button:0,detail:1,clientX:20,clientY:20}}})"); Idle(browser);
                    Assert.Equal(1, owner.SelectedBlock);
                    Assert.Equal("true", Script(browser, "!state.error&&el('selectionStatus').textContent.startsWith('当前')"));
                    // A resolved target can be changed without advancing to another block.
                    Script(browser, "el('mineChoice').click()"); Idle(browser);
                    Assert.Equal(1, owner.SelectedBlock);
                    Capture(browser, "interaction-mixed-choices-wide");
                    fixture.Window.Width = 720; fixture.Window.UpdateLayout(); Settle(browser);
                    Script(browser, "setScale(1.5)"); Settle(browser);
                    AssertButtonsInsideViewport(browser);
                    Capture(browser, "interaction-mixed-choices-narrow-150-reading");
                }
                // Pure insert conflicts have an explicit zero-length anchor in the result.
                var middleContext = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"unchanged_{i}"));
                using (var fixture = new InteractionFixture($"prefix\n{middleContext}\nmiddle\nsuffix\n", $"mineA\nprefix\n{middleContext}\nmiddle\nmineB\nsuffix\n", $"theirsA\nprefix\n{middleContext}\nmiddle\ntheirsB\nsuffix\n"))
                {
                    var browser = fixture.Browser;
                    Assert.Equal(2, fixture.Session.UnresolvedCount);
                    Assert.All(fixture.Session.Blocks, block => Assert.Equal(0, block.ResultLength));
                    Script(browser, "{const p=models.result.getPositionAt(state.blocks[1].offset);editors.result.setScrollTop(editors.result.getTopForLineNumber(p.lineNumber)-100)}"); Settle(browser);
                    Script(browser, "{const rail=document.querySelector('#result .merge-gutter-conflict');const r=rail.getBoundingClientRect();const x=r.left+5,y=r.top+r.height/2;const node=document.elementFromPoint(x,y);node.dispatchEvent(new MouseEvent('mousedown',{bubbles:true,cancelable:true,view:window,button:0,buttons:1,clientX:x,clientY:y,detail:1}));node.dispatchEvent(new MouseEvent('mouseup',{bubbles:true,cancelable:true,view:window,button:0,buttons:0,clientX:x,clientY:y,detail:1}));}"); Idle(browser);
                    Assert.Equal(1, fixture.Window.ViewModel.SelectedBlock);
                    Script(browser, "el('autoAdvance').click()"); Idle(browser);
                    Script(browser, "el('mineChoice').click()"); Idle(browser);
                    Assert.Equal(0, fixture.Window.ViewModel.SelectedBlock);
                    ToggleBase(browser, true);
                    Script(browser, "requestCommand('next')"); Idle(browser); Settle(browser);
                    Assert.Equal("true", Script(browser, "editors.mine.getScrollTop()>0&&Math.abs(editors.mine.getScrollTop()-editors.base.getScrollTop())<1&&Math.abs(editors.mine.getScrollTop()-editors.theirs.getScrollTop())<1"));
                    Capture(browser, "interaction-empty-anchors-base-shown");
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
        });
    }

    private sealed class InteractionFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.Merge.Interaction", Guid.NewGuid().ToString("N"));
        private readonly ThemeService _theme;
        public InteractionFixture(string basis, string mine, string theirs)
        {
            Directory.CreateDirectory(_directory);
            var basePath = Path.Combine(_directory, "base.txt");
            var minePath = Path.Combine(_directory, "mine.txt");
            var theirsPath = Path.Combine(_directory, "theirs.txt");
            File.WriteAllText(basePath, basis); File.WriteAllText(minePath, mine); File.WriteAllText(theirsPath, theirs);
            Session = Wait(TextMergeSession.OpenAsync(basePath, minePath, theirsPath, Path.Combine(_directory, "output.txt")));
            var resources = new ResourceDictionary();
            _theme = ThemeTestSupport.CreateService(resources, new FakeSystemThemeMonitor(AppTheme.Dark));
            _theme.InitializeFromSystem();
            Window = new MergeWindow(new TextMergeViewModel(Session, new string('我', 80), new string('基', 80), new string('对', 80)), _theme)
            { Width = 1500, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Resources.MergedDictionaries.Add(resources);
            Window.Show(); Browser = (WebView2)Window.FindName("Browser");
            PumpUntil(() => Window.IsEditorReady && Script(Browser, "!!state&&!sourceLayoutChanging") == "true");
            Settle(Browser);
        }
        public TextMergeSession Session { get; }
        public MergeWindow Window { get; }
        public WebView2 Browser { get; }
        public void Dispose() { Window.CloseForTest(); _theme.Dispose(); Directory.Delete(_directory, true); }
    }

    private static void AssertSourceColumns(WebView2 browser, bool shown)
    {
        var value = shown ? "true" : "false";
        Assert.Equal("true", Script(browser, $"showBase==={value}&&isEditorVisible('base')==={value}&&el('showBase').getAttribute('aria-pressed')==='{value}'&&Math.abs(el('mine').getBoundingClientRect().width-el('theirs').getBoundingClientRect().width)<2"));
        Assert.Equal("true", Script(browser, "!el('baseChoice').disabled"));
    }

    private static void ToggleBase(WebView2 browser, bool shown)
    {
        Script(browser, $"if(showBase!=={(shown ? "true" : "false")})el('showBase').click()");
        PumpUntil(() => Script(browser, "preferenceAcknowledgement===preferenceSequence&&!sourceLayoutChanging") == "true");
        Settle(browser);
    }

    private static void AssertTogglePreservesResult(WebView2 browser, TextMergeViewModel owner)
    {
        var version = owner.Session.Version;
        var text = owner.Session.ResultText;
        var selected = owner.SelectedBlock;
        var dirty = owner.Session.IsDirty;
        var canUndo = owner.Session.CanUndo;
        Script(browser, "editors.mine.setScrollLeft(200)"); Settle(browser);
        Script(browser, "window.mergeToggleBefore={editor:editors.result,model:models.result,alternative:models.result.getAlternativeVersionId(),selection:JSON.stringify(editors.result.getSelection()),top:editors.result.getScrollTop(),left:editors.result.getScrollLeft()}");
        var sourcePositions = Script(browser, "['mine','theirs'].map(side=>[editors[side].getScrollTop(),editors[side].getScrollLeft()])");
        ToggleBase(browser, false);
        ToggleBase(browser, true);
        Assert.Equal(version, owner.Session.Version);
        Assert.Equal(text, owner.Session.ResultText);
        Assert.Equal(selected, owner.SelectedBlock);
        Assert.Equal(dirty, owner.Session.IsDirty);
        Assert.Equal(canUndo, owner.Session.CanUndo);
        Assert.Equal(sourcePositions, Script(browser, "['mine','theirs'].map(side=>[editors[side].getScrollTop(),editors[side].getScrollLeft()])"));
        Assert.Equal("true", Script(browser, "mergeToggleBefore.editor===editors.result&&mergeToggleBefore.model===models.result&&mergeToggleBefore.alternative===models.result.getAlternativeVersionId()&&mergeToggleBefore.selection===JSON.stringify(editors.result.getSelection())&&mergeToggleBefore.top===editors.result.getScrollTop()&&mergeToggleBefore.left===editors.result.getScrollLeft()"));
        Script(browser, "editors.mine.setScrollLeft(0)");
    }

    private static void AssertReopenedPreference(TextMergeSession session, IThemeService theme, ResourceDictionary resources, string path, bool shown)
    {
        using var preferences = new UserPreferencesService(path);
        var window = new MergeWindow(new TextMergeViewModel(session), theme, preferences)
        { Width = 1500, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Resources.MergedDictionaries.Add(resources);
        try
        {
            window.Show();
            var browser = (WebView2)window.FindName("Browser");
            PumpUntil(() => window.IsEditorReady && Script(browser, "!!state&&!sourceLayoutChanging") == "true");
            AssertSourceColumns(browser, shown);
        }
        finally { window.CloseForTest(); }
    }

    private static void AssertPreferenceFailureIsIndependent(TextMergeSession session, IThemeService theme, ResourceDictionary resources, string directory)
    {
        var path = Path.Combine(directory, "preferences-is-a-directory");
        Directory.CreateDirectory(path);
        using var preferences = new UserPreferencesService(path);
        var window = new MergeWindow(new TextMergeViewModel(session), theme, preferences)
        { Width = 1500, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Resources.MergedDictionaries.Add(resources);
        try
        {
            window.Show();
            var browser = (WebView2)window.FindName("Browser");
            PumpUntil(() => window.IsEditorReady && Script(browser, "!!state") == "true");
            var version = session.Version;
            ToggleBase(browser, true);
            Assert.Equal("true", Script(browser, "showBase&&!!preferenceWarning&&!rejectedDraft&&!inFlight&&!locked"));
            Assert.Equal(version, session.Version);
            Wait(window.FlushAsync());
        }
        finally { window.CloseForTest(); }
    }

    private static void AssertContinuousBlockChrome(WebView2 browser)
    {
        const string script = """
            ['mine','base','theirs','result'].every(side=>{
                const byTop=(a,b)=>a.getBoundingClientRect().top-b.getBoundingClientRect().top;
                const rows=[...document.querySelectorAll('#'+side+' .merge-current')].sort(byTop);
                const rails=[...document.querySelectorAll('#'+side+' .merge-gutter-current')].sort(byTop);
                return rows.length===3&&rails.length===3&&rows.every((row,i)=>{
                    const s=getComputedStyle(row);
                    return (i===0?parseFloat(s.borderTopWidth)>0:s.borderTopWidth==='0px')
                        &&(i===2?parseFloat(s.borderBottomWidth)>0:s.borderBottomWidth==='0px')
                        &&s.borderTopLeftRadius===(i===0?'4px':'0px')
                        &&s.borderBottomLeftRadius===(i===2?'4px':'0px');
                })&&rails.every((rail,i)=>{
                    const s=getComputedStyle(rail,'::before');
                    return s.width==='5px'&&s.top===(i===0?'1px':'0px')&&s.bottom===(i===2?'1px':'0px')
                        &&s.borderTopLeftRadius===(i===0?'2.5px':'0px')
                        &&s.borderBottomLeftRadius===(i===2?'2.5px':'0px')
                        &&(i===0||Math.abs(rail.getBoundingClientRect().top-rails[i-1].getBoundingClientRect().bottom)<.1);
                });
            })
            """;
        Assert.Equal("true", Script(browser, script));
    }
    private static void Capture(WebView2 browser, string name)
    {
        var output = Environment.GetEnvironmentVariable("DIFFER_MERGE_EVIDENCE");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        Wait(browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream));
        File.WriteAllText(Path.Combine(output, name + ".json"), Script(browser,
            "({width:innerWidth,height:innerHeight,readingScale,devicePixelRatio,showBase,version:state.version,unresolved:state.unresolved,outputSaved:!state.dirty,sourceReadOnly:['mine','base','theirs'].every(k=>editors[k].getOption(monaco.editor.EditorOption.readOnly))})"));
        File.WriteAllText(Path.Combine(output, "METHOD.txt"), "Real offline Monaco/WebView2 CapturePreview from the production MergeWindow; programmatic input and offscreen WPF window. These are runtime surface captures, not desktop screenshots. 150% denotes reading zoom, not Windows monitor DPI. Native IME and actual per-monitor DPI not covered.");
    }

    private static void Idle(WebView2 browser) => PumpUntil(() => Script(browser, "!inFlight&&!locked&&!queuedCommand&&pendingText===null") == "true");
    private static void Settle(WebView2 browser)
    {
        Script(browser, "window.mergeFramesSettled=false;requestAnimationFrame(()=>requestAnimationFrame(()=>window.mergeFramesSettled=true))");
        PumpUntil(() => Script(browser, "window.mergeFramesSettled") == "true");
    }
    private static string Script(WebView2 browser, string script) => Wait(browser.ExecuteScriptAsync(script));
    private static T Wait<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException("Merge runtime condition timed out.");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
