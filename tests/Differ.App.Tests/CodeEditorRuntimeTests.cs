using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
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
public sealed class CodeEditorRuntimeTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    [Fact]
    public void C01C02C03C07LocalMonacoEditsAdoptsAndPreservesUndoAndVersion()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var oldContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var directory = Path.Combine(Path.GetTempPath(), "Differ.Editor.Runtime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var basePath = Path.Combine(directory, "base.lua");
            var minePath = Path.Combine(directory, "mine.lua");
            var performanceTail = Environment.GetEnvironmentVariable("DIFFER_EDITOR_PERF") == "1"
                ? string.Join('\n', Enumerable.Range(0, 9997).Select(i => $"local record_{i} = {i}"))
                : string.Join('\n', Enumerable.Range(0, 120).Select(i => $"local record_{i} = {i}"));
            var baseText = "local x = 3\nreturn x\n" + performanceTail;
            var mineText = "local x = 5\nreturn x\n" + performanceTail;
            File.WriteAllText(basePath, baseText, new UTF8Encoding(false));
            File.WriteAllText(minePath, mineText, new UTF8Encoding(false));
            using var scheduler = new ComparisonLoadScheduler();
            var store = new ExternalDiffSessionStore(Path.Combine(directory, "sessions"));
            var session = Wait(store.CreateAsync(new ExternalDiffRequest { BaseFilePath = basePath, MineFilePath = minePath }, CancellationToken.None));
            using var owner = new DiffWindowViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]), scheduler,
                session, NullLogger<DiffWindowViewModel>.Instance, _ => { });
            owner.EnableDocumentEditor();
            Wait(owner.LoadAsync());
            var view = new CodeEditorView { DataContext = owner };
            var window = new Window
            {
                Content = view,
                Width = 1250,
                Height = 720,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            if (Environment.GetEnvironmentVariable("DIFFER_EDITOR_VISIBLE") == "1")
            {
                window.Left = 40;
                window.Top = 40;
                window.ShowActivated = true;
            }
            window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Light.xaml"));
            window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Metrics.xaml"));
            try
            {
                window.Show();
                PumpUntil(() => view.IsEditorReady && owner.EditorResultsCurrent && owner.CurrentDocument is not null);
                Assert.Equal(1, owner.DifferenceCount);
                var browser = Assert.IsType<WebView2>(view.FindName("Browser"));
                AssertReadOnlyBoundary(browser, owner, mineText);
                Wait(owner.SetEditModeAsync(true));
                Assert.Equal("false", Wait(browser.ExecuteScriptAsync("right.getOption(monaco.editor.EditorOption.readOnly)")));
                Assert.Equal("true", Wait(browser.ExecuteScriptAsync("left.getOption(monaco.editor.EditorOption.readOnly)")));
                owner.MoveEditorDifference(1);
                PumpUntil(() => owner.CanEditorAdopt);
                owner.AdoptEditorHunk();
                Assert.Same(browser, FocusManager.GetFocusedElement(window));
                PumpUntil(() => owner.EditorSession!.Text.StartsWith("local x = 3", StringComparison.Ordinal) && owner.EditorResultsCurrent && owner.CurrentDocument is not null);
                Assert.True(owner.IsEditorDirty);
                Assert.Equal(0, owner.DifferenceCount);
                PumpUntil(() => owner.CanEditorUndo);
                owner.UndoEditor();
                Assert.Same(browser, FocusManager.GetFocusedElement(window));
                PumpUntil(() => owner.EditorSession!.Text.StartsWith("local x = 5", StringComparison.Ordinal) && owner.EditorResultsCurrent && owner.CurrentDocument is not null);
                Assert.False(owner.IsEditorDirty);
                PumpUntil(() => owner.CanEditorRedo);
                owner.RedoEditor();
                Assert.Same(browser, FocusManager.GetFocusedElement(window));
                PumpUntil(() => owner.EditorSession!.Text.StartsWith("local x = 3", StringComparison.Ordinal) && owner.EditorResultsCurrent && owner.CurrentDocument is not null);
                var beforeEditVersion = owner.EditorSession!.Version;
                Wait(browser.ExecuteScriptAsync("diff.getModifiedEditor().executeEdits('test',[{range:new monaco.Range(1,1,1,1),text:'-- 中文输入\\n\\t-- 第二行\\n'}]);"));
                PumpUntil(() => owner.EditorSession!.Version > beforeEditVersion && owner.EditorResultsCurrent && owner.CurrentDocument is not null);
                Assert.StartsWith("-- 中文输入\n\t-- 第二行\n", owner.EditorSession.Text);
                Assert.Contains(owner.CurrentDocument!.Nodes, node => node.NewValue == "-- 中文输入");
                Assert.True(Wait(browser.ExecuteScriptAsync("modified.canUndo()")) == "true");
                AssertModeOffPreservesDocument(browser, owner);
                var savePath = Path.Combine(directory, "saved.lua");
                Wait(owner.SaveEditorAsync(savePath));
                Assert.False(owner.IsEditorDirty);
                var savedWriteTime = File.GetLastWriteTimeUtc(savePath);
                Assert.False(Wait(view.SaveFromShellAsync(false)));
                Assert.Equal(savedWriteTime, File.GetLastWriteTimeUtc(savePath));
                Assert.Contains("中文输入", File.ReadAllText(savePath), StringComparison.Ordinal);
                Assert.Equal(baseText, File.ReadAllText(basePath));
                Assert.Equal(mineText, File.ReadAllText(minePath));
                Wait(owner.SetEditModeAsync(true));
                AssertPendingInputIsFlushedWhenDisabling(browser, owner);
                AssertZoneScrollPreserved(view, browser, owner);
                AssertTwoSessionsIsolated(window, view, owner, scheduler, store, basePath, minePath);
                if (Environment.GetEnvironmentVariable("DIFFER_EDITOR_PERF") == "1")
                    RunPerformanceProbe(view, browser, owner);
                var evidence = Environment.GetEnvironmentVariable("DIFFER_EDITOR_EVIDENCE");
                if (!string.IsNullOrEmpty(evidence))
                {
                    Directory.CreateDirectory(evidence);
                    using var image = File.Create(Path.Combine(evidence, "code-editor-runtime.png"));
                    Wait(browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image));
                    File.WriteAllText(Path.Combine(evidence, "code-editor-runtime.json"), JsonSerializer.Serialize(new
                    {
                        Runtime = browser.CoreWebView2.Environment.BrowserVersionString,
                        owner.EditorSession.Version,
                        owner.DifferenceCount,
                        owner.EditorResultsCurrent,
                        TextLength = owner.EditorSession.Text.Length,
                        owner.IsEditorDirty,
                        ChineseProgrammaticEdit = true,
                        NativeImeComposition = "not tested by this automated case"
                    }));
                }
            }
            finally
            {
                owner.Dispose();
                window.Close();
                store.TryDelete(session);
                Directory.Delete(directory, true);
                SynchronizationContext.SetSynchronizationContext(oldContext);
            }
        });
    }

    private static T Wait<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    private static void AssertReadOnlyBoundary(WebView2 browser, DiffWindowViewModel owner, string expectedText)
    {
        Assert.False(owner.IsEditMode);
        Assert.False(owner.CanEditorAdopt);
        Assert.False(owner.CanEditorUndo);
        Assert.False(owner.CanEditorRedo);
        Assert.Equal("true", Wait(browser.ExecuteScriptAsync("left.getOption(monaco.editor.EditorOption.readOnly)&&right.getOption(monaco.editor.EditorOption.readOnly)")));
        var version = owner.EditorSession!.Version;
        // Monaco's executeEdits API intentionally bypasses readOnly; these
        // editor commands exercise its input permission instead.
        Wait(browser.ExecuteScriptAsync("right.trigger('keyboard','type',{text:'blocked typing'});right.trigger('keyboard','paste',{text:'blocked paste',pasteOnNewLine:false});left.trigger('keyboard','type',{text:'blocked BASE'});"));
        owner.UpdateEditorText(owner.EditorSession.Id.ToString("N"), version + 1, "blocked host message");
        owner.MoveEditorDifference(1);
        owner.AdoptEditorHunk();
        owner.UndoEditor();
        owner.RedoEditor();
        Assert.Equal(expectedText, JsonSerializer.Deserialize<string>(Wait(browser.ExecuteScriptAsync("modified.getValue()"))));
        Assert.Equal(expectedText, owner.EditorSession.Text);
        Assert.Equal(version, owner.EditorSession.Version);
        Assert.False(owner.IsEditorDirty);
        Assert.Null(owner.EditorSession.SaveTarget);
        Assert.Equal(TextInputOrigin.Unknown, owner.EditorSession.Origin);
    }

    private static void AssertModeOffPreservesDocument(WebView2 browser, DiffWindowViewModel owner)
    {
        Wait(browser.ExecuteScriptAsync("right.setSelection(new monaco.Range(2,2,2,5));window.beforeModeModel=modified;window.beforeModeAlternative=modified.getAlternativeVersionId();window.beforeModeSelection=JSON.stringify(right.getSelection());"));
        var text = owner.EditorSession!.Text;
        var version = owner.EditorSession.Version;
        Wait(owner.SetEditModeAsync(false));
        Assert.False(owner.IsEditMode);
        Assert.True(owner.IsEditorDirty);
        Assert.True(owner.CanEditorSave);
        Assert.False(owner.CanEditorUndo);
        Assert.False(owner.CanEditorRedo);
        Assert.False(owner.CanEditorAdopt);
        Assert.Null(owner.EditorSession.SaveTarget);
        Assert.Equal(TextInputOrigin.Unknown, owner.EditorSession.Origin);
        Assert.Equal("true", Wait(browser.ExecuteScriptAsync("modified===window.beforeModeModel&&modified.canUndo()&&modified.getAlternativeVersionId()===window.beforeModeAlternative&&JSON.stringify(right.getSelection())===window.beforeModeSelection")));
        Wait(browser.ExecuteScriptAsync("right.trigger('keyboard','type',{text:'blocked'});right.trigger('keyboard','paste',{text:'blocked paste'});"));
        owner.SendEditorCommand(new { type = "undo" });
        owner.UpdateEditorText(owner.EditorSession.Id.ToString("N"), version + 1, "blocked late mutation");
        Assert.Equal(text, owner.EditorSession.Text);
        Assert.Equal(text, JsonSerializer.Deserialize<string>(Wait(browser.ExecuteScriptAsync("modified.getValue()"))));
        Assert.Equal(version, owner.EditorSession.Version);
    }

    private static void AssertPendingInputIsFlushedWhenDisabling(WebView2 browser, DiffWindowViewModel owner)
    {
        var text = owner.EditorSession!.Text;
        var version = owner.EditorSession.Version;
        Wait(browser.ExecuteScriptAsync("right.pushUndoStop();right.setPosition({lineNumber:1,column:1});right.trigger('keyboard','paste',{text:'-- pending mode close\\n',pasteOnNewLine:false});right.pushUndoStop();"));
        // Do not wait for the debounced text IPC: disabling must flush the tail
        // while immediately denying subsequent input and keeping the model.
        Wait(owner.SetEditModeAsync(false));
        Assert.False(owner.IsEditMode);
        Assert.True(owner.IsEditorDirty);
        Assert.Equal("-- pending mode close\n" + text, owner.EditorSession.Text);
        Assert.True(owner.EditorSession.Version > version);
        PumpUntil(() => owner.EditorResultsCurrent);
        var acceptedVersion = owner.EditorSession.Version;
        owner.UpdateEditorText(owner.EditorSession.Id.ToString("N"), version, "stale input");
        Assert.Equal(acceptedVersion, owner.EditorSession.Version);
        Assert.Equal("-- pending mode close\n" + text, owner.EditorSession.Text);
        Assert.Equal("true", Wait(browser.ExecuteScriptAsync("(()=>{right.focus();const event=new KeyboardEvent('keydown',{key:'s',code:'KeyS',keyCode:83,which:83,ctrlKey:true,bubbles:true,cancelable:true});right.getDomNode().querySelector('textarea').dispatchEvent(event);return event.defaultPrevented;})()")));
        PumpUntil(() => !owner.IsEditorDirty);
        Assert.False(owner.IsEditorDirty);
        Assert.Equal("-- pending mode close\n" + text, File.ReadAllText(owner.EditorSession.SaveTarget!));
        Wait(owner.SetEditModeAsync(true));
        PumpUntil(() => owner.CanEditorUndo);
        var beforeUndoVersion = owner.EditorSession.Version;
        owner.UndoEditor();
        PumpUntil(() => owner.EditorResultsCurrent && owner.EditorSession.Version > beforeUndoVersion);
        Assert.Equal(text, owner.EditorSession.Text);
    }

    private static void AssertZoneScrollPreserved(CodeEditorView view, WebView2 browser, DiffWindowViewModel owner)
    {
        var previousVersion = owner.EditorSession!.Version;
        Wait(browser.ExecuteScriptAsync("right.pushUndoStop();right.executeEdits('zone-test',[{range:new monaco.Range(1,1,1,1),text:'-- zone 1\\n-- zone 2\\n-- zone 3\\n-- zone 4\\n-- zone 5\\n'}]);right.pushUndoStop();"));
        PumpUntil(() => owner.EditorSession.Version > previousVersion && owner.EditorResultsCurrent && view.RenderedVersion == owner.EditorSession.Version);
        Assert.True(JsonSerializer.Deserialize<bool>(Wait(browser.ExecuteScriptAsync("zones[0].length > 0"))));
        Wait(browser.ExecuteScriptAsync("right.setSelection(new monaco.Range(90,8,90,12));right.revealLineInCenter(90);"));
        for (var i = 0; i < 5; i++)
        {
            previousVersion = owner.EditorSession.Version;
            Wait(browser.ExecuteScriptAsync("right.pushUndoStop();right.executeEdits('typing',[{range:new monaco.Range(90,8,90,8),text:'z'}]);right.pushUndoStop();"));
            using var expected = JsonDocument.Parse(Wait(browser.ExecuteScriptAsync("({position:right.getPosition(),selection:right.getSelection(),top:right.getScrolledVisiblePosition({lineNumber:90,column:1}).top})")));
            PumpUntil(() => owner.EditorSession.Version > previousVersion && owner.EditorResultsCurrent && view.RenderedVersion == owner.EditorSession.Version);
            using var actual = JsonDocument.Parse(Wait(browser.ExecuteScriptAsync("({position:right.getPosition(),selection:right.getSelection(),top:right.getScrolledVisiblePosition({lineNumber:90,column:1}).top})")));
            Assert.Equal(expected.RootElement.GetProperty("position").GetRawText(), actual.RootElement.GetProperty("position").GetRawText());
            Assert.Equal(expected.RootElement.GetProperty("selection").GetRawText(), actual.RootElement.GetProperty("selection").GetRawText());
            Assert.InRange(Math.Abs(expected.RootElement.GetProperty("top").GetDouble() - actual.RootElement.GetProperty("top").GetDouble()), 0, 1);
        }
    }

    private static void AssertTwoSessionsIsolated(Window window, CodeEditorView firstView, DiffWindowViewModel first,
        ComparisonLoadScheduler scheduler, ExternalDiffSessionStore store, string basePath, string minePath)
    {
        var firstText = first.EditorSession!.Text;
        var firstVersion = first.EditorSession.Version;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var session = Wait(store.CreateAsync(new ExternalDiffRequest { BaseFilePath = basePath, MineFilePath = minePath }, CancellationToken.None));
            using var second = new DiffWindowViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]), scheduler,
                session, NullLogger<DiffWindowViewModel>.Instance, _ => { });
            second.EnableDocumentEditor();
            Wait(second.LoadAsync());
            var secondView = new CodeEditorView { DataContext = second };
            window.Content = secondView;
            PumpUntil(() => secondView.IsEditorReady && secondView.RenderedVersion == second.EditorSession!.Version);
            var secondBrowser = Assert.IsType<WebView2>(secondView.FindName("Browser"));
            Assert.False(second.IsEditMode);
            Assert.True(first.IsEditMode);
            Assert.Equal("2", Wait(secondBrowser.ExecuteScriptAsync("monaco.editor.getModels().length")));
            Wait(second.SetEditModeAsync(true));
            Wait(secondBrowser.ExecuteScriptAsync("for(var i=0;i<12;i++)right.executeEdits('fast',[{range:new monaco.Range(1,1,1,1),text:'-- second-only\\n'}]);"));
            window.Content = firstView;
            PumpUntil(() => second.EditorSession!.Version > 1 && second.EditorResultsCurrent);
            first.UpdateEditorText(second.EditorSession!.Id.ToString("N"), firstVersion + 1, "wrong-session");
            Assert.Equal(firstText, first.EditorSession.Text);
            Assert.Equal(firstVersion, first.EditorSession.Version);
            Assert.Contains("second-only", second.EditorSession.Text, StringComparison.Ordinal);
            Assert.True(first.IsEditMode);
            Wait(second.SetEditModeAsync(false));
            Assert.True(second.IsEditorDirty);
            window.Content = secondView;
            PumpUntil(() => secondView.RenderedVersion == second.EditorSession.Version);
            Assert.False(second.IsEditMode);
            Assert.Equal("true", Wait(secondBrowser.ExecuteScriptAsync("right.getOption(monaco.editor.EditorOption.readOnly)")));
            Assert.Equal("2", Wait(secondBrowser.ExecuteScriptAsync("monaco.editor.getModels().length")));
            second.Dispose();
            Assert.True(secondView.IsEditorDisposed);
            window.Content = firstView;
            store.TryDelete(session);
        }
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("The local Monaco editor did not reach the expected state.");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void RunPerformanceProbe(CodeEditorView view, WebView2 browser, DiffWindowViewModel owner)
    {
        var samples = new List<object>();
        var before = ProcessMemory(browser);
        for (var i = 0; i < 100; i++)
        {
            var previousVersion = owner.EditorSession!.Version;
            var watch = Stopwatch.StartNew();
            var text = JsonSerializer.Serialize($"-- performance input {i}\n");
            Wait(browser.ExecuteScriptAsync($"var end=modified.getPositionAt(modified.getValueLength());right.executeEdits('performance',[{{range:new monaco.Range(end.lineNumber,end.column,end.lineNumber,end.column),text:{text}}}]);"));
            PumpUntil(() => owner.EditorSession.Version > previousVersion && owner.EditorResultsCurrent && view.RenderedVersion == owner.EditorSession.Version);
            samples.Add(new
            {
                Operation = "insert",
                Index = i,
                Version = owner.EditorSession.Version,
                InputFrameMs = view.LastInputFrameMilliseconds,
                AuthorityRenderedMs = watch.Elapsed.TotalMilliseconds
            });
            if (i % 10 == 9)
            {
                MeasureCommand("adopt", () => { owner.MoveEditorDifference(1); owner.AdoptEditorHunk(); });
                MeasureCommand("undo", () => owner.SendEditorCommand(new { type = "undo" }));
                MeasureCommand("redo", () => owner.SendEditorCommand(new { type = "redo" }));
                MeasureCommand("undo", () => owner.SendEditorCommand(new { type = "undo" }));
            }
        }
        var evidence = Environment.GetEnvironmentVariable("DIFFER_EDITOR_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "Differ.Editor.Evidence");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "code-editor-interaction-samples.json"), JsonSerializer.Serialize(new
        {
            Method = "100 public executeEdits events plus 40 adopt/undo/redo events; Stopwatch includes 100 ms document debounce, IPC and frame acknowledgement. InputFrameMs comes from browser requestAnimationFrame. This is not physical keyboard or native IME latency.",
            Runtime = browser.CoreWebView2.Environment.BrowserVersionString,
            Before = before,
            After = ProcessMemory(browser),
            Samples = samples
        }, EvidenceJsonOptions));

        void MeasureCommand(string operation, Action command)
        {
            var previousVersion = owner.EditorSession!.Version;
            var watch = Stopwatch.StartNew();
            command();
            PumpUntil(() => owner.EditorSession.Version > previousVersion && owner.EditorResultsCurrent && view.RenderedVersion == owner.EditorSession.Version);
            samples.Add(new
            {
                Operation = operation,
                Index = samples.Count,
                Version = owner.EditorSession.Version,
                InputFrameMs = view.LastInputFrameMilliseconds,
                AuthorityRenderedMs = watch.Elapsed.TotalMilliseconds
            });
        }
    }

    private static object ProcessMemory(WebView2 browser)
    {
        using var host = Process.GetCurrentProcess();
        var children = browser.CoreWebView2.Environment.GetProcessInfos().Select(info =>
        {
            try { using var process = Process.GetProcessById(info.ProcessId); return new { info.ProcessId, Kind = info.Kind.ToString(), WorkingSet = process.WorkingSet64 }; }
            catch (ArgumentException) { return new { info.ProcessId, Kind = info.Kind.ToString(), WorkingSet = 0L }; }
        }).ToArray();
        return new { HostWorkingSet = host.WorkingSet64, WebViewProcesses = children };
    }
}
