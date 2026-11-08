using System.Text;
using Differ.Core.Models;

namespace Differ.Formats.Text.Tests;

public sealed class EditableTextSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.Editor.Tests", Guid.NewGuid().ToString("N"));
    public EditableTextSessionTests() { Directory.CreateDirectory(_directory); Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("", "新增", 1, 1, 1, 1)]
    [InlineData("原文", "", 1, 1, 1, 1)]
    [InlineData("a", "a\nb", 1, 0, 2, 2)]
    [InlineData("a\nb", "a", 2, 2, 1, 0)]
    [InlineData("a\nb\nc", "a\nx\nc", 2, 2, 2, 2)]
    [InlineData("a\nb", "x\na\nb", 0, 0, 1, 1)]
    [InlineData("x\na\nb", "a\nb", 1, 1, 0, 0)]
    [InlineData("a\nb\n", "a\nx\n", 2, 2, 2, 2)]
    public async Task C01C02AdoptRangesReconstructBaseIncludingEmptyAndEofAsync(string before, string after, int os, int oe, int ns, int ne)
    {
        var session = await OpenAsync(before, after);
        Assert.True(session.AcceptChanges(session.Version, [new(os, oe, ns, ne)]));
        var replacement = session.GetLeftReplacement(0, session.Version);
        var adopted = session.Text.Remove(replacement.Offset, replacement.Length).Insert(replacement.Offset, replacement.Text);
        Assert.Equal(before, adopted);
        var oldVersion = session.Version;
        Assert.True(session.Update(adopted, oldVersion));
        Assert.Equal(after != before, session.IsDirty);
        Assert.Throws<InvalidOperationException>(() => session.GetLeftReplacement(0, oldVersion));
    }

    [Fact]
    public async Task C07OnlyCurrentVersionResultsCanBeAppliedAsync()
    {
        var session = await OpenAsync("old", "new");
        Assert.True(session.AcceptChanges(1, [new(1, 1, 1, 1)]));
        Assert.True(session.Update("中文\n多行\t粘贴", 1));
        Assert.False(session.ResultsCurrent);
        Assert.False(session.AcceptChanges(1, []));
        Assert.Empty(session.Hunks);
        Assert.False(session.Update("stale", 1));
        Assert.Equal("中文\n多行\t粘贴", session.Text);
        Assert.True(session.AcceptChanges(2, [new(1, 1, 1, 2)]));
        Assert.Equal(2, session.ResultVersion);
        var document = session.CreateDocument("fixture.lua", "123");
        Assert.Contains(document.Nodes, node => node.NewValue == "多行\t粘贴");
        Assert.All(document.Nodes, node => Assert.StartsWith("editor:2:", node.StableId));
    }

    [Fact]
    public async Task C04UnknownRequiresSaveAsAndBaseSnapshotsRemainReadOnlyAsync()
    {
        var session = await OpenAsync("BASE", "MINE");
        Assert.Equal(TextInputOrigin.Unknown, session.Origin);
        Assert.Null(session.SaveTarget);
        Assert.True(session.Update("修改", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync());
        foreach (var name in new[] { "base.txt", "mine.txt" })
            await Assert.ThrowsAsync<IOException>(() => session.SaveAsync(Path.Combine(_directory, name)));
        var original = Path.Combine(_directory, "original.txt");
        await File.WriteAllTextAsync(original, "original");
        session.ProtectPath(original);
        await Assert.ThrowsAsync<IOException>(() => session.SaveAsync(original));
        var result = Path.Combine(_directory, "result.txt");
        await session.SaveAsync(result);
        Assert.False(session.IsDirty);
        Assert.Equal("修改", await File.ReadAllTextAsync(result));
        Assert.Equal("BASE", await File.ReadAllTextAsync(Path.Combine(_directory, "base.txt")));
        Assert.Equal("MINE", await File.ReadAllTextAsync(Path.Combine(_directory, "mine.txt")));
    }

    [Theory]
    [InlineData("utf-8", false, "\r\n", true)]
    [InlineData("utf-8", true, "\n", false)]
    [InlineData("utf-16", true, "\r\n", false)]
    [InlineData("utf-16BE", true, "\n", true)]
    [InlineData("gb18030", false, "\r\n", true)]
    [InlineData("gb2312", false, "\n", false)]
    public async Task C05EncodingBomLineEndingAndFinalNewlineSurviveSaveAsync(string encodingName, bool bom, string newline, bool final)
    {
        var text = "中文0012" + newline + "第二行" + (final ? newline : "");
        var encoding = Encoding.GetEncoding(encodingName);
        var bytes = (bom ? encoding.GetPreamble() : []).Concat(encoding.GetBytes(text)).ToArray();
        var basePath = Path.Combine(_directory, "base.txt");
        var snapshot = Path.Combine(_directory, "mine.txt");
        var target = Path.Combine(_directory, "working.txt");
        await File.WriteAllBytesAsync(basePath, bytes);
        await File.WriteAllBytesAsync(snapshot, bytes);
        await File.WriteAllBytesAsync(target, bytes);
        var session = (await EditableTextSession.OpenAsync(basePath, snapshot, null, encodingName))!;
        session.ConfirmSaveTarget(target, TextInputOrigin.LocalFile);
        var stamp = File.GetLastWriteTimeUtc(target);
        await session.SaveAsync();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(target));
        session.Update(session.Text.Replace("0012", "0013", StringComparison.Ordinal), session.Version);
        await session.SaveAsync();
        var expected = (bom ? encoding.GetPreamble() : []).Concat(encoding.GetBytes(text.Replace("0012", "0013", StringComparison.Ordinal))).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(target));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task C05MixedNewlinesRequireExplicitChoiceAfterEditAsync()
    {
        var session = await OpenAsync("a\r\nb\nc", "a\r\nb\nc");
        var target = Path.Combine(_directory, "mixed.txt");
        session.Update("a\nb\n修改", session.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(target));
        session.ChooseUniformLineEnding("\n");
        await session.SaveAsync(target);
        Assert.Equal("a\nb\n修改", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task C05UnencodableCharacterCannotBecomeQuestionMarkAsync()
    {
        var paths = await WriteAsync("中文", "中文", Encoding.GetEncoding(936));
        var session = (await EditableTextSession.OpenAsync(paths.Before, paths.After, null, "gb2312"))!;
        session.Update("中文😀", 1);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => session.SaveAsync(Path.Combine(_directory, "gbk.txt")));
        Assert.True(session.IsDirty);
        Assert.False(File.Exists(Path.Combine(_directory, "gbk.txt")));
    }

    [Fact]
    public async Task C06ExternalChangeOrWriteFailureKeepsMemoryAndDirtyFlagAsync()
    {
        var session = await OpenAsync("a", "b");
        var target = Path.Combine(_directory, "working.txt");
        await File.WriteAllTextAsync(target, "b");
        session.ConfirmSaveTarget(target, TextInputOrigin.LocalFile);
        session.Update("memory", 1);
        await File.WriteAllTextAsync(target, "external");
        await Assert.ThrowsAsync<ExternalTextChangeException>(() => session.SaveAsync());
        Assert.Equal("external", await File.ReadAllTextAsync(target));
        Assert.Equal("memory", session.Text);
        Assert.True(session.IsDirty);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => session.SaveAsync(Path.Combine(_directory, "missing", "file.txt")));
        Assert.True(session.IsDirty);
        Assert.Empty(Directory.GetFiles(_directory, ".differ-save-*.tmp"));
    }

    [Fact]
    public async Task C06UnknownSaveAsToOriginalInputStillChecksLoadFingerprintAsync()
    {
        var session = await OpenAsync("base", "initial");
        var original = Path.Combine(_directory, "original-input.txt");
        await File.WriteAllTextAsync(original, "initial");
        session.RememberInputPath(original);
        session.Update("memory edit", session.Version);
        await File.WriteAllTextAsync(original, "external edit after load");
        await Assert.ThrowsAsync<ExternalTextChangeException>(() => session.SaveAsync(original));
        Assert.Equal("external edit after load", await File.ReadAllTextAsync(original));
        Assert.Equal("memory edit", session.Text);
        Assert.True(session.IsDirty);
        Assert.Null(session.SaveTarget);
    }

    [Fact]
    public async Task C05UneditedMixedNewlineSaveAsKeepsExactBytesAsync()
    {
        var session = await OpenAsync("base", "a\r\nb\nc\r");
        var target = Path.Combine(_directory, "copy.txt");
        await session.SaveAsync(target);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_directory, "mine.txt")), await File.ReadAllBytesAsync(target));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task C08MultipleSessionsAndStaleHunksRemainIsolatedAsync()
    {
        var first = await OpenAsync("a", "b");
        var second = await OpenAsync("a", "b");
        Assert.NotEqual(first.Id, second.Id);
        first.AcceptChanges(1, [new(1, 1, 1, 1)]);
        second.AcceptChanges(1, [new(1, 1, 1, 1)]);
        for (var i = 0; i < 100; i++) first.Update("edit" + i, first.Version);
        Assert.Empty(first.Hunks);
        Assert.Equal("b", second.Text);
        Assert.Single(second.Hunks);
        Assert.Equal(101, first.Version);
    }

    [Theory]
    [InlineData("Alpha", "alpha", true, false, false)]
    [InlineData("  中文  ", "中文", false, true, false)]
    [InlineData("a\n", "a", false, false, true)]
    public void C07AuthoritativeEnginePreservesIgnoreOptions(string before, string after, bool ignoreCase, bool ignoreWhitespace, bool ignoreLineEndings)
    {
        var snapshot = new EditorTextSnapshot(before, after, 1);
        var strict = EditorDiffEngine.Compare(snapshot, new DiffOptions());
        Assert.NotEmpty(strict.Hunks);
        var comparison = EditorDiffEngine.Compare(snapshot, new DiffOptions
        { IgnoreCase = ignoreCase, IgnoreWhitespace = ignoreWhitespace, IgnoreLineEndings = ignoreLineEndings });
        Assert.Empty(comparison.Hunks);
        Assert.Equal(0, EditableTextSession.CreateDocument(comparison, "text.lua", "1").Summary.Changed);
    }

    [Fact]
    public async Task C07ImmutableSnapshotCannotBeChangedByLaterInputAsync()
    {
        var session = await OpenAsync("base", "first");
        var snapshot = session.CaptureText();
        session.Update("second", session.Version);
        var comparison = EditorDiffEngine.Compare(snapshot, new DiffOptions());
        var document = EditableTextSession.CreateDocument(comparison, "text.lua", null);
        Assert.Contains(document.Nodes, node => node.NewValue == "first");
        Assert.DoesNotContain(document.Nodes, node => node.NewValue == "second");
        Assert.False(session.AcceptComparison(comparison));
        Assert.Equal("second", session.Text);
    }

    [Theory]
    [InlineData("", "a")]
    [InlineData("a", "")]
    [InlineData("\na", "x\na")]
    [InlineData("a\nb", "a\n\nb")]
    [InlineData("a", "a\n")]
    [InlineData("a\n", "a")]
    public void C01AuthoritativeBlocksRetainEmptyPhysicalLines(string before, string after)
    {
        var comparison = EditorDiffEngine.Compare(new EditorTextSnapshot(before, after, 1), new DiffOptions());
        var restored = after;
        foreach (var hunk in comparison.Hunks.Reverse())
            restored = restored.Remove(hunk.NewOffset, hunk.NewLength).Insert(hunk.NewOffset, before.Substring(hunk.OldOffset, hunk.OldLength));
        Assert.Equal(before, restored);
        var document = EditableTextSession.CreateDocument(comparison, "test.txt", null);
        Assert.All(document.Nodes.Where(node => node.Kind == DiffKind.Unchanged), node => Assert.Equal(node.OldValue, node.NewValue));
    }

    [Fact]
    public void C08LargeRewriteIsBoundedAndReportsCoarseRegions()
    {
        var before = string.Join('\n', Enumerable.Range(0, 50000).Select(i => "before " + i));
        var after = string.Join('\n', Enumerable.Range(0, 50000).Select(i => "after " + i));
        var comparison = EditorDiffEngine.Compare(new EditorTextSnapshot(before, after, 1), new DiffOptions());
        Assert.Single(comparison.Hunks);
        Assert.Equal(1, comparison.ApproximateRegionCount);
        Assert.NotEmpty(EditableTextSession.CreateDocument(comparison, "large.lua", null).Warnings);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => EditorDiffEngine.Compare(new EditorTextSnapshot(before, after, 1), new DiffOptions(), cancellation.Token));
    }

    [Fact]
    public void C08LargeSparseChangesRetainStableAnchors()
    {
        var lines = Enumerable.Range(0, 50000).Select(i => "local record_" + i + " = " + i).ToArray();
        var before = string.Join('\n', lines);
        lines[3] = "local record_3 = 333";
        lines[^4] = "local record_49996 = 444";
        var comparison = EditorDiffEngine.Compare(new EditorTextSnapshot(before, string.Join('\n', lines), 1), new DiffOptions());
        Assert.Equal(2, comparison.Hunks.Count);
        Assert.Equal(0, comparison.ApproximateRegionCount);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5000)]
    public void C07WhitespaceAndCaseRulesAgreeInLocalAndAnchorBranches(int count)
    {
        var oldLines = Enumerable.Range(0, count).Select(i => $" Alpha   value_{i} ").ToArray();
        var newLines = Enumerable.Range(0, count).Select(i => $"alpha value_{i}").ToArray();
        oldLines[0] = "old start"; newLines[0] = "new start";
        oldLines[^1] = "old end"; newLines[^1] = "new end";
        var snapshot = new EditorTextSnapshot(string.Join('\n', oldLines), string.Join('\n', newLines), 1);
        var comparison = EditorDiffEngine.Compare(snapshot, new DiffOptions { IgnoreWhitespace = true, IgnoreCase = true });
        Assert.Equal(2, comparison.Hunks.Count);
        Assert.Equal(2, EditableTextSession.CreateDocument(comparison, "test.lua", null).Summary.Changed);
        Assert.Equal(0, comparison.ApproximateRegionCount);
    }

    [Fact]
    public async Task C05ReaderAndEncodingWarningsReachAuthoritativeResultsAsync()
    {
        var basePath = Path.Combine(_directory, "base.txt");
        var minePath = Path.Combine(_directory, "mine.txt");
        await File.WriteAllTextAsync(basePath, "中文\r\n第二行\r\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(minePath, "中文\n第二行", Encoding.GetEncoding(54936));
        var session = (await EditableTextSession.OpenAsync(basePath, minePath, null, null))!;
        var snapshot = session.CaptureText();
        var result = EditorDiffEngine.Compare(snapshot, new DiffOptions());
        var document = EditableTextSession.CreateDocument(result, "text.lua", null);
        Assert.Contains(document.Warnings, warning => warning.Contains("GB18030", StringComparison.Ordinal));
        Assert.Contains(document.Warnings, warning => warning.Contains("utf-8", StringComparison.Ordinal) && warning.Contains("gb18030", StringComparison.Ordinal));
        Assert.Contains(document.Warnings, warning => warning.Contains("CRLF", StringComparison.Ordinal) && warning.Contains("LF", StringComparison.Ordinal));
        Assert.Contains(Differ.Formats.Text.Resources.TextStrings.AdapterFinalNewlineChanged, document.Warnings);
        var ignored = EditorDiffEngine.Compare(snapshot, new DiffOptions { IgnoreLineEndings = true });
        var ignoredDocument = EditableTextSession.CreateDocument(ignored, "text.lua", null);
        Assert.DoesNotContain(Differ.Formats.Text.Resources.TextStrings.AdapterFinalNewlineChanged, ignoredDocument.Warnings);
        Assert.DoesNotContain(ignoredDocument.Warnings, warning => warning.Contains("CRLF", StringComparison.Ordinal));
        Assert.Contains(ignoredDocument.Warnings, warning => warning.Contains("GB18030", StringComparison.Ordinal));
        Assert.Equal("中文\n第二行", session.Text);
        Assert.True(session.AcceptComparison(ignored));
        Assert.Equal(ignoredDocument.Warnings, session.CreateDocument("text.lua", null).Warnings);
    }

    [Fact]
    public void C08FiftyThousandCharacterLineRetainsUnchangedMiddle()
    {
        var prefix = "local long = \"" + new string('a', 10000);
        var middle = new string('b', 30000);
        var suffix = new string('c', 10000) + "\"";
        var before = prefix + "原" + middle + "3" + suffix;
        var after = prefix + "变" + middle + "5" + suffix;
        Assert.True(before.Length + after.Length > Differ.Core.Algorithms.DiffPerformanceOptions.MaximumEditorDetailedLineCharacters);
        var result = EditorInlineDiffer.Compare(before, after, false, false);
        Assert.Contains(result.OldSegments, segment => !segment.IsChanged && segment.Text.Contains(middle, StringComparison.Ordinal));
        Assert.Equal("原3", string.Concat(result.OldSegments.Where(segment => segment.IsChanged).Select(segment => segment.Text)));
        Assert.Equal("变5", string.Concat(result.NewSegments.Where(segment => segment.IsChanged).Select(segment => segment.Text)));
        Assert.False(result.IsApproximate);
        Assert.Equal(before, string.Concat(result.OldSegments.Select(segment => segment.Text)));
        Assert.Equal(after, string.Concat(result.NewSegments.Select(segment => segment.Text)));
    }

    private async Task<EditableTextSession> OpenAsync(string before, string after)
    {
        var paths = await WriteAsync(before, after, new UTF8Encoding(false));
        return (await EditableTextSession.OpenAsync(paths.Before, paths.After, null, null))!;
    }
    private async Task<(string Before, string After)> WriteAsync(string before, string after, Encoding encoding)
    {
        var oldPath = Path.Combine(_directory, "base.txt");
        var newPath = Path.Combine(_directory, "mine.txt");
        await File.WriteAllTextAsync(oldPath, before, encoding);
        await File.WriteAllTextAsync(newPath, after, encoding);
        return (oldPath, newPath);
    }
}
