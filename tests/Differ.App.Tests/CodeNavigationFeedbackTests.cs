using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
public sealed class CodeNavigationFeedbackTests
{
    private static readonly int[] RailPixelSampleOffsets = [12, 23, 24, 25, 47, 48, 49, 60];
    private static readonly int[] MarkdownChangedIndexes = [2, 6, 10, 14, 15, 16];

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void OrdinaryMarkdownRailsHaveRoundedEndpointsBeforeAndAfterSelection(AppTheme theme)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Enumerable.Range(1, 30).Select(i => $"- 配置项 {i:00}：当前值 = 120").ToArray();
            var after = before.ToArray();
            foreach (var index in MarkdownChangedIndexes) after[index] = $"- 配置项 {index + 1:00}：当前值 = 240";
            using var fixture = new Fixture(string.Join('\n', before), string.Join('\n', after), theme, ".md");
            Assert.Equal(4, fixture.Owner.EditorSession!.Hunks.Count);
            Assert.Equal("-1", fixture.Script("activeHunk"));
            PumpUntil(() => fixture.Script("document.querySelectorAll('.diff-gutter.modified').length===12") == "true");
            var json = fixture.Script("['base','mine'].map(id=>[...document.querySelectorAll('#'+id+' .diff-gutter.modified')].map(node=>{const b=node.getBoundingClientRect(),p=getComputedStyle(node,'::before');return {x:b.x+parseFloat(p.left),y:b.y+parseFloat(p.top),width:parseFloat(p.width),height:parseFloat(p.height),start:node.classList.contains('rail-start'),end:node.classList.contains('rail-end'),topRadius:p.borderTopLeftRadius,bottomRadius:p.borderBottomLeftRadius};}).sort((a,b)=>a.y-b.y))");
            using var geometry = JsonDocument.Parse(json);
            using var capture = new MemoryStream();
            Wait(fixture.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, capture));
            var evidence = Environment.GetEnvironmentVariable("DIFFER_NAV_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                File.WriteAllBytes(Path.Combine(evidence, $"ordinary-rails-{theme}.png"), capture.ToArray());
                File.WriteAllText(Path.Combine(evidence, $"ordinary-rails-{theme}.json"), json);
            }
            capture.Position = 0;
            var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(capture, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            var scale = double.Parse(fixture.Script("window.devicePixelRatio"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(2, geometry.RootElement.GetArrayLength());
            for (var sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                var side = geometry.RootElement[sideIndex];
                var markerKey = sideIndex == 0 ? "Diff.Row.Deleted.Border" : "Diff.Row.Added.Border";
                var modifiedMarker = ((SolidColorBrush)fixture.Browser.FindResource(markerKey)).Color;
                Assert.Equal(6, side.GetArrayLength());
                for (var index = 0; index < 6; index++)
                {
                    var rail = side[index];
                    var start = index < 4;
                    var end = index < 3 || index == 5;
                    Assert.Equal(start, rail.GetProperty("start").GetBoolean());
                    Assert.Equal(end, rail.GetProperty("end").GetBoolean());
                    Assert.Equal(start ? "2.5px" : "0px", rail.GetProperty("topRadius").GetString());
                    Assert.Equal(end ? "2.5px" : "0px", rail.GetProperty("bottomRadius").GetString());
                    Assert.Equal(5, rail.GetProperty("width").GetDouble());
                    Assert.Equal(24 - (start ? 1 : 0) - (end ? 1 : 0), rail.GetProperty("height").GetDouble());
                    var x = rail.GetProperty("x").GetDouble();
                    var y = rail.GetProperty("y").GetDouble();
                    var height = rail.GetProperty("height").GetDouble();
                    var left = (int)Math.Floor(x * scale);
                    var right = (int)Math.Ceiling((x + 5) * scale) - 1;
                    if (start)
                    {
                        AssertRoundedCorner(bitmap, left, (int)Math.Floor(y * scale), modifiedMarker);
                        AssertRoundedCorner(bitmap, right, (int)Math.Floor(y * scale), modifiedMarker);
                    }
                    if (end)
                    {
                        AssertRoundedCorner(bitmap, left, (int)Math.Ceiling((y + height) * scale) - 1, modifiedMarker);
                        AssertRoundedCorner(bitmap, right, (int)Math.Ceiling((y + height) * scale) - 1, modifiedMarker);
                    }
                    AssertAccentPixel(bitmap, (int)Math.Floor((x + 2.5) * scale), (int)Math.Floor((y + height / 2) * scale), modifiedMarker);
                    if (index >= 4)
                    {
                        var previous = side[index - 1];
                        Assert.InRange(Math.Abs(y - previous.GetProperty("y").GetDouble() - previous.GetProperty("height").GetDouble()), 0, 0.1);
                        AssertAccentPixel(bitmap, (int)Math.Floor((x + 2.5) * scale), (int)Math.Floor(y * scale), modifiedMarker);
                        AssertAccentPixel(bitmap, (int)Math.Floor((x + 2.5) * scale), (int)Math.Floor(y * scale) - 1, modifiedMarker);
                    }
                }
            }
            var accent = ((SolidColorBrush)fixture.Browser.FindResource("Dui.Focus")).Color;
            fixture.Owner.MoveEditorDifference(1);
            fixture.WaitForNavigation();
            PumpUntil(() => fixture.Script("!foldSync") == "true");
            fixture.Script("requestAnimationFrame(()=>requestAnimationFrame(()=>window.markdownSelectionSettled=true))");
            PumpUntil(() => fixture.Script("window.markdownSelectionSettled===true") == "true");
            Assert.Equal("true", fixture.Script("feedbackFrames.every(frame=>getComputedStyle(frame).borderTopLeftRadius==='4px')&&feedbackRails.every(rail=>getComputedStyle(rail).borderTopLeftRadius==='2.5px')"));
            using var selected = new MemoryStream();
            Wait(fixture.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, selected));
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                File.WriteAllBytes(Path.Combine(evidence, $"ordinary-rails-selected-{theme}.png"), selected.ToArray());
            }
            selected.Position = 0;
            var selectedBitmap = new FormatConvertedBitmap(BitmapFrame.Create(selected, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            var selectedJson = fixture.Script("[...feedbackFrames,...feedbackRails].map(node=>{const r=node.getBoundingClientRect();return {left:r.left,top:r.top,right:r.right,bottom:r.bottom};})");
            if (!string.IsNullOrWhiteSpace(evidence)) File.WriteAllText(Path.Combine(evidence, $"ordinary-rails-selected-{theme}.json"), selectedJson);
            using var selectedGeometry = JsonDocument.Parse(selectedJson);
            foreach (var rectangle in selectedGeometry.RootElement.EnumerateArray())
            {
                var left = (int)Math.Floor(rectangle.GetProperty("left").GetDouble() * scale);
                var right = (int)Math.Ceiling(rectangle.GetProperty("right").GetDouble() * scale) - 1;
                var top = (int)Math.Floor(rectangle.GetProperty("top").GetDouble() * scale);
                var bottom = (int)Math.Ceiling(rectangle.GetProperty("bottom").GetDouble() * scale) - 1;
                AssertRoundedCorner(selectedBitmap, left, top, accent);
                AssertRoundedCorner(selectedBitmap, right, top, accent);
                AssertRoundedCorner(selectedBitmap, left, bottom, accent);
                AssertRoundedCorner(selectedBitmap, right, bottom, accent);
            }
        });
    }

    private static void AssertAccentPixel(BitmapSource bitmap, int x, int y, Color accent)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Assert.InRange(Math.Abs(pixel[0] - accent.B), 0, 8);
        Assert.InRange(Math.Abs(pixel[1] - accent.G), 0, 8);
        Assert.InRange(Math.Abs(pixel[2] - accent.R), 0, 8);
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void MultilineSelectionUsesOneContinuousRoundedRailPerSide(AppTheme theme)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Enumerable.Range(1, 80).Select(i => $"local item{i} = {i}").ToArray();
            var after = before.ToArray();
            for (var index = 9; index <= 11; index++) after[index] = $"local item{index + 1} = 999";
            using var fixture = new Fixture(string.Join('\n', before), string.Join('\n', after), theme);
            Assert.Single(fixture.Owner.EditorSession!.Hunks);
            fixture.Owner.MoveEditorDifference(1);
            fixture.WaitForNavigation();
            PumpUntil(() => fixture.Script("feedbackRails.length===2&&feedbackRails.every(rail=>!rail.hidden)") == "true");
            using var geometry = JsonDocument.Parse(fixture.Script("[...document.querySelectorAll('.current-hunk-rail')].map((rail,side)=>{const box=rail.getBoundingClientRect(),frame=feedbackFrames[side].getBoundingClientRect(),style=getComputedStyle(rail);return {x:box.x,y:box.y,width:box.width,height:box.height,frameTop:frame.top,frameHeight:frame.height,topRadius:style.borderTopLeftRadius,bottomRadius:style.borderBottomLeftRadius,pointerEvents:style.pointerEvents};})"));
            Assert.Equal(2, geometry.RootElement.GetArrayLength());
            Assert.All(geometry.RootElement.EnumerateArray(), rail =>
            {
                Assert.Equal(5, rail.GetProperty("width").GetDouble());
                Assert.Equal(70, rail.GetProperty("height").GetDouble());
                Assert.Equal(rail.GetProperty("frameTop").GetDouble() + 1, rail.GetProperty("y").GetDouble());
                Assert.Equal(rail.GetProperty("frameHeight").GetDouble() - 2, rail.GetProperty("height").GetDouble());
                Assert.Equal("2.5px", rail.GetProperty("topRadius").GetString());
                Assert.Equal("2.5px", rail.GetProperty("bottomRadius").GetString());
                Assert.Equal("none", rail.GetProperty("pointerEvents").GetString());
            });

            using var capture = new MemoryStream();
            Wait(fixture.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, capture));
            var evidence = Environment.GetEnvironmentVariable("DIFFER_NAV_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                File.WriteAllBytes(Path.Combine(evidence, $"round-rail-{theme}.png"), capture.ToArray());
                File.WriteAllText(Path.Combine(evidence, $"round-rail-layers-{theme}.json"), fixture.Script("[...document.querySelectorAll('[class*=gutter],.current-hunk-rail')].map(node=>{const box=node.getBoundingClientRect(),style=getComputedStyle(node);return {html:node.outerHTML,x:box.x,y:box.y,width:box.width,height:box.height,borderLeft:style.borderLeft,background:style.backgroundColor,radius:style.borderRadius,zIndex:style.zIndex};})"));
            }
            capture.Position = 0;
            var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(capture, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            var scale = double.Parse(fixture.Script("window.devicePixelRatio"), System.Globalization.CultureInfo.InvariantCulture);
            var expected = ((SolidColorBrush)fixture.Browser.FindResource("Dui.Focus")).Color;
            foreach (var rail in geometry.RootElement.EnumerateArray())
            {
                foreach (var offset in RailPixelSampleOffsets)
                {
                    // Check both line junctions as well as their centers: rounded
                    // per-line beads must not introduce gaps inside the block.
                    var pixels = new byte[4];
                    var x = (int)Math.Floor((rail.GetProperty("x").GetDouble() + 2) * scale);
                    var y = (int)Math.Floor((rail.GetProperty("y").GetDouble() + offset) * scale);
                    bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
                    Assert.InRange(Math.Abs(pixels[0] - expected.B), 0, 8);
                    Assert.InRange(Math.Abs(pixels[1] - expected.G), 0, 8);
                    Assert.InRange(Math.Abs(pixels[2] - expected.R), 0, 8);
                }
                var left = (int)Math.Floor(rail.GetProperty("x").GetDouble() * scale);
                var right = (int)Math.Ceiling((rail.GetProperty("x").GetDouble() + rail.GetProperty("width").GetDouble()) * scale) - 1;
                var top = (int)Math.Floor(rail.GetProperty("y").GetDouble() * scale);
                var bottom = (int)Math.Ceiling((rail.GetProperty("y").GetDouble() + rail.GetProperty("height").GetDouble()) * scale) - 1;
                AssertRoundedCorner(bitmap, left, top, expected);
                AssertRoundedCorner(bitmap, right, top, expected);
                AssertRoundedCorner(bitmap, left, bottom, expected);
                AssertRoundedCorner(bitmap, right, bottom, expected);
            }

            PumpUntil(() => fixture.Script("!foldSync") == "true");
            fixture.Script("requestAnimationFrame(()=>requestAnimationFrame(()=>window.railRevealSettled=true))");
            PumpUntil(() => fixture.Script("window.railRevealSettled===true") == "true");
            fixture.Script("left.setScrollTop(left.getTopForLineNumber(10)+12)");
            PumpUntil(() => fixture.Script("feedbackRails.every(rail=>!rail.hidden&&getComputedStyle(rail).borderTopLeftRadius==='0px')") == "true");
            var clippedJson = fixture.Script("feedbackRails.map((rail,side)=>{const editor=side===0?left:right,box=rail.getBoundingClientRect(),style=getComputedStyle(rail);return {height:box.height,top:box.top,scrollTop:editor.getScrollTop(),lineTop:editor.getTopForLineNumber(10),lineBottom:editor.getBottomForLineNumber(12),topRadius:style.borderTopLeftRadius,bottomRadius:style.borderBottomLeftRadius};})");
            if (!string.IsNullOrWhiteSpace(evidence)) File.WriteAllText(Path.Combine(evidence, $"round-rail-clipped-{theme}.json"), clippedJson);
            using var clipped = JsonDocument.Parse(clippedJson);
            Assert.All(clipped.RootElement.EnumerateArray(), rail =>
            {
                // DOM rectangles can be fractional CSS pixels at Windows DPI scales.
                Assert.True(Math.Abs(rail.GetProperty("height").GetDouble() - 59) <= 1, clippedJson);
                Assert.Equal("0px", rail.GetProperty("topRadius").GetString());
                Assert.Equal("2.5px", rail.GetProperty("bottomRadius").GetString());
            });
        });
    }

    private static void AssertRoundedCorner(BitmapSource bitmap, int x, int y, Color accent)
    {
        Assert.True(x >= 0 && y >= 0 && x < bitmap.PixelWidth && y < bitmap.PixelHeight,
            $"Corner ({x},{y}) is outside capture {bitmap.PixelWidth}x{bitmap.PixelHeight}.");
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        var difference = Math.Max(Math.Abs(pixel[0] - accent.B), Math.Max(Math.Abs(pixel[1] - accent.G), Math.Abs(pixel[2] - accent.R)));
        Assert.True(difference >= 16,
            $"Rounded rail corner at ({x}, {y}) is still solid accent: actual RGB {pixel[2]},{pixel[1]},{pixel[0]}; accent {accent.R},{accent.G},{accent.B}.");
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void SingleLineNavigationHasVisiblePaintedBoundariesAndExplicitRanges(AppTheme theme)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture("local count = 3\nreturn count", "local count = 5\nreturn count", theme);
            fixture.Owner.MoveEditorDifference(1);
            fixture.WaitForNavigation();
            var evidence = Environment.GetEnvironmentVariable("DIFFER_NAV_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, $"navigation-{theme}-geometry.json"), fixture.Script("[...document.querySelectorAll('.current-hunk-frame,.diff-line')].map(node=>({html:node.outerHTML,width:node.getBoundingClientRect().width,height:node.getBoundingClientRect().height,border:getComputedStyle(node).border,zIndex:getComputedStyle(node).zIndex}))"));
                using var stream = File.Create(Path.Combine(evidence, $"navigation-{theme}.png"));
                Wait(fixture.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream));
            }
            using var geometry = JsonDocument.Parse(fixture.Script("[...document.querySelectorAll('.current-hunk-frame')].map(node=>({x:node.getBoundingClientRect().x,y:node.getBoundingClientRect().y,width:node.getBoundingClientRect().width,height:node.getBoundingClientRect().height,border:getComputedStyle(node).borderTopWidth,color:getComputedStyle(node).borderTopColor}))"));
            Assert.Equal(2, geometry.RootElement.GetArrayLength());
            Assert.All(geometry.RootElement.EnumerateArray(), item =>
            {
                Assert.True(item.GetProperty("width").GetDouble() > 100);
                Assert.True(item.GetProperty("height").GetDouble() >= 20);
                Assert.Equal("1px", item.GetProperty("border").GetString());
            });
            var caption = JsonSerializer.Deserialize<string>(fixture.Script("document.getElementById('current-location').textContent"));
            Assert.Equal("第 1 / 1 块 · 修改 · BASE 第 1 行 ↔ 结果 第 1 行", caption);
            // A DOM box alone previously passed while the later semantic
            // background painted over the outline. Assert actual bitmap ink.
            using var capture = new MemoryStream();
            Wait(fixture.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, capture));
            capture.Position = 0;
            var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(capture, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            var scale = double.Parse(fixture.Script("window.devicePixelRatio"), System.Globalization.CultureInfo.InvariantCulture);
            var expected = ((SolidColorBrush)fixture.Browser.FindResource("Dui.Focus")).Color;
            foreach (var frame in geometry.RootElement.EnumerateArray())
            {
                var pixels = new byte[4];
                var x = (int)Math.Floor((frame.GetProperty("x").GetDouble() + 0.5) * scale);
                var y = (int)Math.Floor((frame.GetProperty("y").GetDouble() + 12) * scale);
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
                Assert.InRange(Math.Abs(pixels[0] - expected.B), 0, 8);
                Assert.InRange(Math.Abs(pixels[1] - expected.G), 0, 8);
                Assert.InRange(Math.Abs(pixels[2] - expected.R), 0, 8);
            }
        });
    }

    [Fact]
    public void NavigationMarksBothSidesIncludingEmptyRangesAndRejectsLateFeedback()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Enumerable.Range(1, 160).Select(i => $"local item{i} = {i}").ToArray();
            var after = before.Where((_, index) => index is not (49 or 50)).ToList();
            after[9] = "local item10 = 999";
            after.InsertRange(90, ["local addedA = 1", "local addedB = 2"]);
            using var fixture = new Fixture(string.Join('\n', before), string.Join('\n', after));
            Assert.Equal(3, fixture.Owner.EditorSession!.Hunks.Count);
            var text = fixture.Owner.EditorSession.Text;
            var undoVersion = fixture.Script("modified.getAlternativeVersionId()");
            for (var index = 0; index < 3; index++)
            {
                fixture.Owner.MoveEditorDifference(1);
                fixture.WaitForNavigation(index);
                using var frames = JsonDocument.Parse(fixture.Script("feedbackFrames.map(frame=>({hidden:frame.hidden,top:frame.getBoundingClientRect().top,height:frame.getBoundingClientRect().height,block:frame.dataset.block}))"));
                Assert.All(frames.RootElement.EnumerateArray(), frame =>
                {
                    Assert.False(frame.GetProperty("hidden").GetBoolean());
                    Assert.Equal((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), frame.GetProperty("block").GetString());
                    Assert.True(frame.GetProperty("height").GetDouble() >= 20);
                });
                Assert.InRange(Math.Abs(frames.RootElement[0].GetProperty("top").GetDouble() - frames.RootElement[1].GetProperty("top").GetDouble()), 0, 1);
                Assert.InRange(Math.Abs(frames.RootElement[0].GetProperty("height").GetDouble() - frames.RootElement[1].GetProperty("height").GetDouble()), 0, 1);
                var label = JsonSerializer.Deserialize<string>(fixture.Script("document.getElementById('current-location').textContent"))!;
                Assert.Contains($"第 {index + 1} / 3 块", label, StringComparison.Ordinal);
                if (index > 0) Assert.Contains("无对应行", label, StringComparison.Ordinal);
            }
            for (var i = 0; i < 7; i++) fixture.Owner.MoveEditorDifference(1);
            fixture.WaitForNavigation(0);
            Assert.Equal(text, fixture.Owner.EditorSession.Text);
            Assert.Equal(undoVersion, fixture.Script("modified.getAlternativeVersionId()"));
            Assert.False(fixture.Owner.IsEditorDirty);
            Wait(fixture.Owner.SetEditModeAsync(true));
            fixture.Script("right.executeEdits('test',[{range:new monaco.Range(1,1,1,1),text:'-- edit\\n'}])");
            Assert.Equal("true", fixture.Script("feedbackFrames.every(frame=>frame.hidden)"));
            Assert.Equal("true", fixture.Script("feedbackRails.every(rail=>rail.hidden)"));
            Assert.DoesNotContain("BASE 第", JsonSerializer.Deserialize<string>(fixture.Script("document.getElementById('current-location').textContent")), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ContextSearchNavigationRevealsBothSidesAndClearsFeedbackForUnchangedTargets()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Enumerable.Range(1, 160).Select(i => $"local item{i} = {i}").ToArray();
            var after = before.ToArray();
            after[9] = "local item10 = 999";
            after[119] = "local item120 = 999";
            using var fixture = new Fixture(string.Join('\n', before), string.Join('\n', after));
            fixture.Owner.DisplayMode = DiffDisplayMode.Context;
            fixture.Owner.SendEditorCommand(new { type = "reveal", mine = false, line = 120, version = fixture.Owner.EditorSession!.Version });
            fixture.WaitForNavigation(1);
            Assert.Equal(1, fixture.Owner.CurrentHunk);
            var originalHeight = fixture.Script("right.getLayoutInfo().height");
            fixture.Resize(860, 460);
            PumpUntil(() => fixture.Script("right.getLayoutInfo().height") != originalHeight);
            using var geometry = JsonDocument.Parse(fixture.Script("feedbackFrames.map(frame=>({top:frame.getBoundingClientRect().top,bottom:frame.getBoundingClientRect().bottom,limit:right.getLayoutInfo().height}))"));
            Assert.All(geometry.RootElement.EnumerateArray(), frame =>
            {
                Assert.True(frame.GetProperty("top").GetDouble() >= 0);
                Assert.True(frame.GetProperty("bottom").GetDouble() <= frame.GetProperty("limit").GetDouble());
            });
            fixture.Owner.SendEditorCommand(new { type = "reveal", mine = true, line = 60, version = fixture.Owner.EditorSession.Version });
            PumpUntil(() => fixture.Owner.CurrentHunk == -1 && fixture.Script("feedbackFrames.every(frame=>frame.hidden)") == "true");
            Assert.Equal("true", fixture.Script("feedbackRails.every(rail=>rail.hidden)"));
            Assert.Equal("结果 第 60 行 · 此处无差异", JsonSerializer.Deserialize<string>(fixture.Script("document.getElementById('current-location').textContent")));
            Assert.Contains("—", fixture.Owner.DifferencePositionText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void DifferenceRailUsesTheBodyScrollGeometryAndNavigatesBothEditors()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Enumerable.Range(1, 160).Select(i => $"local item{i} = {i}").ToArray();
            before[19] = "local longValue = \"" + new string('x', 260) + "\"";
            before[42] = "local item43 = 430";
            var after = before.ToList();
            after[42] = "local item43 = 987";
            after[119] = "local item120 = 555";
            after.RemoveAt(98);
            after.Insert(12, "local inserted = 1");
            using var fixture = new Fixture(string.Join('\n', before), string.Join('\n', after));

            const string geometry = """
                (() => {
                    const rail = document.getElementById('difference-rail');
                    const band = document.getElementById('rail-viewport');
                    const railBounds = rail.getBoundingClientRect();
                    const bandBounds = band.getBoundingClientRect();
                    const body = right.getDomNode().getBoundingClientRect();
                    const layout = right.getLayoutInfo();
                    const horizontal = right.getScrollWidth() > layout.contentWidth ? layout.horizontalScrollbarHeight : 0;
                    const scrollHeight = right.getScrollHeight();
                    const viewportHeight = layout.height - horizontal;
                    const extent = Math.max(viewportHeight, scrollHeight - horizontal);
                    return document.querySelectorAll('#difference-rail[role="scrollbar"]').length === 1
                        && [left, right].every(pane => pane.getOption(monaco.editor.EditorOption.scrollbar).verticalScrollbarSize === 0
                            && [...pane.getDomNode().querySelectorAll('.scrollbar.vertical')].every(bar => bar.getBoundingClientRect().width < 1))
                        && Math.abs(railBounds.width - 16) <= 1
                        && Math.abs(railBounds.top - body.top) <= 1
                        && Math.abs(railBounds.height - viewportHeight) <= 1
                        && Math.abs(bandBounds.top - railBounds.top - right.getScrollTop() * railBounds.height / extent) <= 1.5
                        && Math.abs(bandBounds.height - viewportHeight * railBounds.height / extent) <= 1.5
                        && Math.abs(Number(rail.getAttribute('aria-valuenow')) - right.getScrollTop()) <= 1
                        && Math.abs(Number(rail.getAttribute('aria-valuemax')) - Math.max(0, scrollHeight - layout.height)) <= 1;
                })()
                """;

            PumpUntil(() => fixture.Script(geometry) == "true");
            fixture.Script("right.setScrollLeft(180)");
            PumpUntil(() => fixture.Script("right.getScrollLeft()>=170&&left.getScrollLeft()===right.getScrollLeft()") == "true");
            fixture.Script("right.setScrollLeft(0);right.setScrollTop(280)");
            PumpUntil(() => fixture.Script("right.getScrollTop()===280&&left.getScrollTop()===280") == "true");
            PumpUntil(() => fixture.Script(geometry) == "true");
            fixture.Script("document.getElementById('difference-rail').dispatchEvent(new WheelEvent('wheel', { deltaY: 48, cancelable: true }))");
            PumpUntil(() => fixture.Script("right.getScrollTop()===328&&left.getScrollTop()===328") == "true");

            PumpUntil(() => fixture.Script("railMarks.some(mark => mark.index === 1)") == "true");
            fixture.Script("""
                (() => {
                    const rail = document.getElementById('difference-rail');
                    const bounds = rail.getBoundingClientRect();
                    const marker = railMarks.find(mark => mark.index === 1);
                    window.testRailPointer = (kind, y, buttons) => rail.dispatchEvent(new PointerEvent(kind, {
                        pointerId: 41, pointerType: 'mouse', isPrimary: true, bubbles: true,
                        cancelable: true, button: 0, buttons, clientX: bounds.left + 8, clientY: y
                    }));
                    window.testRailMarkerY = bounds.top + marker.y;
                    testRailPointer('pointermove', testRailMarkerY, 0);
                })()
                """);
            PumpUntil(() => fixture.Script("""
                (() => {
                    const preview = document.getElementById('rail-preview');
                    return !preview.hidden && preview.textContent.includes('local item43 = 430')
                        && preview.textContent.includes('local item43 = 987');
                })()
                """) == "true");
            fixture.Script("testRailPointer('pointerdown', testRailMarkerY, 1);testRailPointer('pointerup', testRailMarkerY, 0)");
            fixture.WaitForNavigation(1);
            PumpUntil(() => fixture.Owner.CurrentHunk == 1);

            fixture.Script("""
                (() => {
                    const band = document.getElementById('rail-viewport').getBoundingClientRect();
                    const y = band.top + band.height / 2;
                    window.testRailStart = right.getScrollTop();
                    window.testRailEndY = y + 65;
                    testRailPointer('pointerdown', y, 1);
                    testRailPointer('pointermove', y + 28, 1);
                })()
                """);
            PumpUntil(() => fixture.Script("right.getScrollTop()>testRailStart&&left.getScrollTop()===right.getScrollTop()") == "true");
            fixture.Script("window.testRailIntermediate=right.getScrollTop();testRailPointer('pointermove',testRailEndY,1)");
            PumpUntil(() => fixture.Script("right.getScrollTop()>testRailIntermediate&&left.getScrollTop()===right.getScrollTop()") == "true");
            fixture.Script("testRailPointer('pointerup',testRailEndY,0)");
            Assert.Equal(1, fixture.Owner.CurrentHunk);
            Assert.False(fixture.Owner.IsEditorDirty);
            fixture.Script("right.setScrollTop(right.getScrollHeight())");
            PumpUntil(() => fixture.Script("""
                (() => {
                    const rail = document.getElementById('difference-rail').getBoundingClientRect();
                    const band = document.getElementById('rail-viewport').getBoundingClientRect();
                    return Math.abs(rail.bottom - band.bottom) <= 1 && left.getScrollTop() === right.getScrollTop();
                })()
                """) == "true");

            var unfoldedHeight = fixture.Script("right.getScrollHeight()");
            fixture.Owner.DisplayMode = DiffDisplayMode.Context;
            PumpUntil(() => fixture.Script("right.getScrollHeight()") != unfoldedHeight);
            PumpUntil(() => fixture.Script(geometry) == "true");
            fixture.Resize(720, 460);
            fixture.SetReadingScale(1.5);
            PumpUntil(() => fixture.Script("right.getOption(monaco.editor.EditorOption.lineHeight)===36") == "true");
            PumpUntil(() => fixture.Script(geometry) == "true");
        });
    }

    [Theory]
    [InlineData("", "新增第一行\n新增第二行")]
    [InlineData("删除第一行\n删除第二行", "")]
    public void EmptyFileSideHasFeedbackOnItsActualMissingRegion(string before, string after)
    {
        ThemeTestSupport.RunSta(() =>
        {
            using var fixture = new Fixture(before, after);
            fixture.Owner.MoveEditorDifference(1);
            fixture.WaitForNavigation();
            Assert.Contains("文件开头无对应行", JsonSerializer.Deserialize<string>(fixture.Script("document.getElementById('current-location').textContent")), StringComparison.Ordinal);
            Assert.Equal("true", fixture.Script("feedbackFrames.every(frame=>!frame.hidden&&frame.getBoundingClientRect().height>=48)"));
            Assert.Equal("true", fixture.Script("feedbackRails.length===2&&feedbackRails.every((rail,side)=>!rail.hidden&&Math.abs(rail.getBoundingClientRect().height-(feedbackFrames[side].getBoundingClientRect().height-2))<1&&Math.abs(rail.getBoundingClientRect().top-(feedbackFrames[side].getBoundingClientRect().top+1))<1)"));
            Assert.Equal("true", fixture.Script("getComputedStyle(document.querySelector('.missing-zone')).backgroundImage.startsWith('repeating-linear-gradient')"));
            Assert.Equal("true", fixture.Script("document.getElementById('adopt-current').hidden&&document.getElementById('adopt-current').disabled"));
            Wait(fixture.Owner.SetEditModeAsync(true));
            PumpUntil(() => fixture.Script("!document.getElementById('adopt-current').hidden") == "true");
            using var arrow = JsonDocument.Parse(fixture.Script("(()=>{const button=document.getElementById('adopt-current'),rect=button.getBoundingClientRect();return {hidden:button.hidden,x:rect.x,width:rect.width,height:rect.height,middle:right.getDomNode().getBoundingClientRect().left}})()"));
            Assert.False(arrow.RootElement.GetProperty("hidden").GetBoolean());
            Assert.InRange(Math.Abs(arrow.RootElement.GetProperty("x").GetDouble() + arrow.RootElement.GetProperty("width").GetDouble() / 2 - arrow.RootElement.GetProperty("middle").GetDouble()), 0, 1);
            Assert.InRange(arrow.RootElement.GetProperty("width").GetDouble(), 16, 24);
            Assert.True(arrow.RootElement.GetProperty("height").GetDouble() >= 20);
            fixture.Script("document.getElementById('adopt-current').click()");
            PumpUntil(() => fixture.Owner.EditorResultsCurrent && fixture.Owner.EditorSession!.Text == before);
            Assert.Equal(0, fixture.Owner.DifferenceCount);
            Assert.Equal("true", fixture.Script("document.getElementById('adopt-current').hidden||document.getElementById('adopt-current').disabled"));
            PumpUntil(() => fixture.Owner.CanEditorUndo);
            fixture.Owner.UndoEditor();
            PumpUntil(() => fixture.Owner.EditorResultsCurrent && fixture.Owner.EditorSession!.Text == after);
            Assert.Equal(1, fixture.Owner.DifferenceCount);
        });
    }

    [Fact]
    public void SameViewportNavigationChangesThePaintedTargetAndLateRevealCannotOverrideNewSelection()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Enumerable.Range(1, 16).Select(i => $"local item{i} = {i}").ToArray();
            var after = before.ToArray();
            after[2] = "local item3 = 99";
            after[10] = "local item11 = 99";
            using var fixture = new Fixture(string.Join('\n', before), string.Join('\n', after));
            fixture.Owner.MoveEditorDifference(1);
            fixture.WaitForNavigation();
            var firstTop = fixture.Script("feedbackFrames[1].style.top");
            var initialScroll = fixture.Script("right.getScrollTop()");
            fixture.Owner.MoveEditorDifference(1);
            fixture.WaitForNavigation(1);
            Assert.NotEqual(firstTop, fixture.Script("feedbackFrames[1].style.top"));
            Assert.Equal(initialScroll, fixture.Script("right.getScrollTop()"));
            Assert.Contains("第 2 / 2 块", JsonSerializer.Deserialize<string>(fixture.Script("document.getElementById('current-location').textContent")), StringComparison.Ordinal);

            fixture.Script("window.savedGetAction=left.getAction.bind(left);left.getAction=id=>id==='editor.unfoldAll'?{run:()=>new Promise(resolve=>window.releaseOldReveal=resolve)}:savedGetAction(id);selectHunk(0,true);selectHunk(1,false);window.releaseOldReveal();left.getAction=window.savedGetAction;");
            fixture.WaitForNavigation(1);
            // Wait through another browser frame so the older asynchronous
            // unfold/reveal continuation has had a chance to run.
            fixture.Script("requestAnimationFrame(()=>window.navigationFrameChecked=true)");
            PumpUntil(() => fixture.Script("window.navigationFrameChecked===true") == "true");
            Assert.Equal("1", fixture.Script("activeHunk"));
            Assert.Equal("2", fixture.Script("feedbackFrames[1].dataset.block").Trim('"'));
            Assert.Equal(initialScroll, fixture.Script("right.getScrollTop()"));
        });
    }

    [Fact]
    public void ThemeSwapRefreshesSameBackgroundEditorWithoutReplacingModelOrUndoHistory()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var before = Enumerable.Range(1, 120).Select(i => $"local item{i} = {i}").ToArray();
            var after = before.ToArray();
            after[10] = "local item11 = 777";
            using var fixture = new Fixture(string.Join('\n', before), string.Join('\n', after));
            Wait(fixture.Owner.SetEditModeAsync(true));
            fixture.Script("right.executeEdits('test',[{range:new monaco.Range(2,1,2,1),text:'-- edited\\n'}])");
            PumpUntil(() => fixture.Owner.IsEditorDirty && fixture.Owner.CanEditorUndo && fixture.Owner.EditorResultsCurrent);
            fixture.SetReadingScale(1.3);
            PumpUntil(() => fixture.Script("right.getOption(monaco.editor.EditorOption.lineHeight)>24&&railMarks.length>0&&!railFrame") == "true");
            fixture.Script("right.setSelection(new monaco.Range(45,2,45,6));right.revealLineInCenter(45);window.themeTestModel=modified;");
            PumpUntil(() => fixture.Script("!railFrame") == "true");
            fixture.Script("""
                window.themeProbe = {definitions: 0, optionUpdates: 0, railMarks};
                const originalDefineTheme = monaco.editor.defineTheme;
                monaco.editor.defineTheme = function (...args) {
                    window.themeProbe.definitions++;
                    return originalDefineTheme.apply(this, args);
                };
                for (const editor of [left, right]) {
                    const originalUpdateOptions = editor.updateOptions;
                    editor.updateOptions = function (...args) {
                        window.themeProbe.optionUpdates++;
                        return originalUpdateOptions.apply(this, args);
                    };
                }
                """);
            const string state = "[modified.getValue(),modified.getAlternativeVersionId(),right.getSelection(),right.getScrollTop()]";
            var priorState = fixture.Script(state);
            var priorVersion = fixture.Owner.EditorSession!.Version;
            var priorBackground = fixture.Script("document.documentElement.style.getPropertyValue('--background')");
            var priorRail = fixture.Script("railCanvas.toDataURL()");
            const string tokenClasses = "[...right.getDomNode().querySelectorAll('.view-line span span')].map(s=>s.className).join(' ')";
            fixture.Script("window.themeProbe.tokenNode=right.getDomNode().querySelector('.view-line span span[class*=mtk]:not(.mtk1)');window.themeProbe.tokenColor=getComputedStyle(window.themeProbe.tokenNode).color;");
            var priorTokenClasses = fixture.Script(tokenClasses);
            Assert.Contains("mtk", priorTokenClasses);
            Assert.Equal("\"#FFFFFF\"", priorBackground);
            fixture.ApplyThemeWithFixedContentBrush(AppTheme.Dark);

            PumpUntil(() => fixture.Script("document.body.classList.contains('dark')&&!railFrame") == "true");
            // One rule list for both themes keeps each token's class, so Monaco reuses the rendered line.
            Assert.Equal(priorTokenClasses, fixture.Script(tokenClasses));
            Assert.Equal("true", fixture.Script("window.themeProbe.tokenNode.isConnected"));
            Assert.Equal("true", fixture.Script("right.getDomNode().classList.contains('vs-dark')"));
            Assert.Equal("true", fixture.Script("getComputedStyle(window.themeProbe.tokenNode).color!==window.themeProbe.tokenColor"));
            Assert.Equal("\"#F5F5F7\"", fixture.Script("document.documentElement.style.getPropertyValue('--focus')"));
            Assert.Equal(priorBackground, fixture.Script("document.documentElement.style.getPropertyValue('--background')"));
            Assert.NotEqual(priorRail, fixture.Script("railCanvas.toDataURL()"));
            Assert.Equal("true", fixture.Script("window.themeProbe.railMarks===railMarks"));
            Assert.Equal("[1,0]", fixture.Script("[window.themeProbe.definitions,window.themeProbe.optionUpdates]"));
            fixture.ApplyThemeWithFixedContentBrush(AppTheme.Light);
            PumpUntil(() => fixture.Script("!document.body.classList.contains('dark')&&!railFrame") == "true");
            fixture.ApplyThemeWithFixedContentBrush(AppTheme.Dark);
            PumpUntil(() => fixture.Script("document.body.classList.contains('dark')&&!railFrame") == "true");
            Assert.Equal("[1,0]", fixture.Script("[window.themeProbe.definitions,window.themeProbe.optionUpdates]"));
            Assert.Equal("true", fixture.Script("window.themeTestModel===modified"));
            Assert.Equal(priorVersion, fixture.Owner.EditorSession.Version);
            Assert.Equal(priorState, fixture.Script(state));
            Assert.True(fixture.Owner.IsEditorDirty);
            Assert.True(fixture.Owner.CanEditorUndo);

            // A previously opened tab remains attached to the persistent host while collapsed.
            fixture.SetEditorVisibility(Visibility.Collapsed);
            fixture.ApplyThemeWithFixedContentBrush(AppTheme.Light);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            PumpUntil(() => fixture.Script("!document.body.classList.contains('dark')") == "true");
            fixture.SetEditorVisibility(Visibility.Visible);
            Assert.Equal("true", fixture.Script("window.themeTestModel===modified"));
            Assert.Equal(priorState, fixture.Script(state));
            Assert.True(fixture.Owner.IsEditorDirty);
            fixture.Owner.UndoEditor();
            PumpUntil(() => fixture.Owner.EditorResultsCurrent && fixture.Owner.EditorSession.Text == string.Join('\n', after));
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.Navigation.Tests", Guid.NewGuid().ToString("N"));
        private readonly ComparisonLoadScheduler _scheduler = new();
        private readonly ExternalDiffSessionStore _store;
        private readonly ExternalDiffSession _session;
        private readonly Window _window;
        private readonly CodeEditorView _view;

        public Fixture(string before, string after, AppTheme theme = AppTheme.Light, string extension = ".lua")
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Directory.CreateDirectory(_directory);
            var basePath = Path.Combine(_directory, "base" + extension);
            var minePath = Path.Combine(_directory, "mine" + extension);
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
                Width = 1200,
                Height = 700,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            _window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load($"Themes/{theme}.xaml"));
            _window.Resources.MergedDictionaries.Add(ThemeTestSupport.Load("Themes/Metrics.xaml"));
            _window.Show();
            PumpUntil(() => _view.IsEditorReady && Owner.EditorResultsCurrent && _view.RenderedVersion == Owner.EditorSession!.Version);
            Browser = (WebView2)_view.FindName("Browser");
        }

        public DiffWindowViewModel Owner { get; }
        public WebView2 Browser { get; }
        public string Script(string script) => Wait(Browser.ExecuteScriptAsync(script));
        public void ApplyThemeWithFixedContentBrush(AppTheme theme)
        {
            _window.Resources["Dui.Surface.Content"] = _window.FindResource("Dui.Surface.Content");
            using var themes = ThemeTestSupport.CreateService(_window.Resources, new FakeSystemThemeMonitor(AppTheme.Light));
            themes.Apply(theme);
        }

        public void SetEditorVisibility(Visibility visibility) => _view.Visibility = visibility;

        public void SetReadingScale(double scale) => _view.ReadingScale = scale;

        public void Resize(double width, double height) { _window.Width = width; _window.Height = height; _window.UpdateLayout(); }
        public void WaitForNavigation(int index = 0) => PumpUntil(() => Script($"activeHunk==={index}&&feedbackFrames.length===2&&feedbackFrames.every(frame=>!frame.hidden&&frame.dataset.block==='{index + 1}')") == "true");
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
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Navigation feedback did not become visible.");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
