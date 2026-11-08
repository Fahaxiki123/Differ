using System.Text;

namespace Differ.Formats.Text.Tests;

public sealed class TextMergeSessionTests : IDisposable
{
    private static readonly string[] FileNames = ["base.txt", "mine.txt", "theirs.txt", "result.txt"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.Merge.Tests", Guid.NewGuid().ToString("N"));
    public TextMergeSessionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task DecisionsUndoRedoAndSavedStateRemainConsistentAsync()
    {
        var session = await OpenAsync("a\nb\nc\n", "a\nM\nc\n", "a\nT\nc\n");
        Assert.True(session.IsDirty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync());
        Assert.False(File.Exists(session.OutputPath));
        Assert.False(session.Choose(0, MergeChoice.Mine, session.Version - 1));
        Assert.True(session.Choose(0, MergeChoice.Theirs, session.Version));
        Assert.Equal("a\nT\nc\n", session.ResultText);
        Assert.Equal(0, session.UnresolvedCount);
        Assert.True(session.Undo(session.Version));
        Assert.Equal(1, session.UnresolvedCount);
        Assert.Equal("a\nb\nc\n", session.ResultText);
        Assert.True(session.Redo(session.Version));
        await session.SaveAsync();
        Assert.True(session.HasSaved);
        Assert.False(session.IsDirty);
        Assert.Equal("a\nT\nc\n", await File.ReadAllTextAsync(session.OutputPath));
        Assert.Equal("a\nM\nc\n", await File.ReadAllTextAsync(session.MinePath));
        Assert.True(session.Undo(session.Version));
        Assert.True(session.IsDirty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync());
    }

    [Fact]
    public async Task ManualEditingInvalidatesTouchedResolutionAndRemapsLaterConflictAsync()
    {
        var session = await OpenAsync("a\nb\nc\nd\ne\n", "a\nM\nc\nN\ne\n", "a\nT\nc\nU\ne\n");
        session.Choose(0, MergeChoice.Mine, session.Version);
        session.UpdateResult("a\nlonger\nc\nd\ne\n", session.Version);
        Assert.Equal(2, session.UnresolvedCount);
        Assert.Equal("d\n", session.ResultText.Substring(session.Blocks[1].ResultOffset, session.Blocks[1].ResultLength));
        Assert.True(session.Confirm(0, session.Version));
        Assert.True(session.Choose(1, MergeChoice.Theirs, session.Version));
        Assert.Equal("a\nlonger\nc\nU\ne\n", session.ResultText);
        await session.SaveAsync();
    }

    [Fact]
    public async Task EditsAcrossBlocksCannotReplaceUnrelatedManualContentAsync()
    {
        var session = await OpenAsync("a\nb\nc\nd\ne\n", "a\nM\nc\nN\ne\n", "a\nT\nc\nU\ne\n");
        Assert.True(session.UpdateResult("a\n手工合并整个区域\ne\n", session.Version));
        Assert.All(session.Blocks, block => Assert.False(block.CanChoose));
        Assert.False(session.Choose(0, MergeChoice.Mine, session.Version));
        foreach (var block in session.Blocks) Assert.True(session.Confirm(block.Index, session.Version));
        await session.SaveAsync();
        Assert.Equal("a\n手工合并整个区域\ne\n", await File.ReadAllTextAsync(session.OutputPath));
        Assert.True(session.Undo(session.Version));
        Assert.True(session.UnresolvedCount > 0);
    }

    [Fact]
    public async Task ContextEditsShiftConflictWithoutDroppingItsContentsAsync()
    {
        var session = await OpenAsync("a\nb\nc\n", "a\nM\nc\n", "a\nT\nc\n");
        session.UpdateResult("prefix\na\nb\nc\n", session.Version);
        Assert.True(session.Blocks[0].CanChoose);
        session.Choose(0, MergeChoice.Mine, session.Version);
        Assert.Equal("prefix\na\nM\nc\n", session.ResultText);
    }

    [Fact]
    public async Task ExistingOrNewOutputChangedExternallyIsNeverOverwrittenAsync()
    {
        var session = await OpenAsync("a", "b", "a");
        await File.WriteAllTextAsync(session.OutputPath, "external");
        await Assert.ThrowsAsync<IOException>(() => session.SaveAsync());
        Assert.Equal("external", await File.ReadAllTextAsync(session.OutputPath));
        Assert.True(session.IsDirty);
        var loaded = await TextMergeSession.OpenAsync(session.BasePath, session.MinePath, session.TheirsPath, session.OutputPath);
        await File.WriteAllTextAsync(session.OutputPath, "changed again");
        await Assert.ThrowsAsync<IOException>(() => loaded.SaveAsync());
        Assert.Empty(Directory.GetFiles(_directory, ".differ-merge-*.tmp"));
    }

    [Fact]
    public async Task SaveProtectsAllInputsAndSupportsIndependentSaveAsAsync()
    {
        var session = await OpenAsync("a", "b", "a");
        foreach (var source in new[] { session.BasePath, session.MinePath, session.TheirsPath })
            await Assert.ThrowsAsync<IOException>(() => session.SaveAsync(source));
        var target = Path.Combine(_directory, "另存 结果.txt");
        await session.SaveAsync(target);
        Assert.Equal(target, session.OutputPath);
        Assert.Equal("b", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task SaveAsNeverForgetsOriginalOutputBaselineAsync()
    {
        var session = await OpenAsync("a", "b", "a");
        var requiredOutput = session.InitialOutputPath;
        await session.SaveAsync(Path.Combine(_directory, "alternate.txt"));
        await File.WriteAllTextAsync(requiredOutput, "external change");
        await Assert.ThrowsAsync<IOException>(() => session.SaveAsync(requiredOutput));
        Assert.Equal("external change", await File.ReadAllTextAsync(requiredOutput));
    }

    [Fact]
    public async Task RepeatedSaveDoesNotRewriteIdenticalOutputAsync()
    {
        var session = await OpenAsync("a", "b", "a");
        await session.SaveAsync();
        var stamp = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(session.OutputPath, stamp);
        var actualStamp = File.GetLastWriteTimeUtc(session.OutputPath);
        await session.SaveAsync();
        Assert.Equal(actualStamp, File.GetLastWriteTimeUtc(session.OutputPath));
    }

    [Fact]
    public async Task UnencodableTextDoesNotCreateOutputOrLoseMemoryAsync()
    {
        var session = await OpenAsync("a", "b", "a");
        session.UpdateResult("invalid \ud800", session.Version);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => session.SaveAsync());
        Assert.Equal("invalid \ud800", session.ResultText);
        Assert.True(session.IsDirty);
        Assert.False(File.Exists(session.OutputPath));
    }

    [Fact]
    public async Task BomCrLfAndNoFinalNewlineArePreservedAsync()
    {
        var session = await OpenAsync("a\r\nb\r\nc", "A\r\nb\r\nc", "a\r\nb\r\nC", new UTF8Encoding(true));
        await session.SaveAsync();
        var expected = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("A\r\nb\r\nC")).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(session.OutputPath));
    }

    [Fact]
    public async Task MixedLineEndingsRejectChangesButExactMineUsesOriginalBytesAsync()
    {
        var session = await OpenAsync("a\r\nb\nc", "a\r\nb\nc", "a\r\nB\nc");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync());
        Assert.False(File.Exists(session.OutputPath));
        session.UpdateResult(session.MineText, session.Version);
        await session.SaveAsync();
        Assert.Equal(await File.ReadAllBytesAsync(session.MinePath), await File.ReadAllBytesAsync(session.OutputPath));
    }

    [Fact]
    public async Task UnsupportedInputMissingFileAndCancellationAreRejectedAsync()
    {
        var session = await OpenAsync("a", "b", "a");
        var csv = Path.Combine(_directory, "input.csv");
        await File.WriteAllTextAsync(csv, "a,b");
        await Assert.ThrowsAsync<InvalidDataException>(() => TextMergeSession.OpenAsync(session.BasePath, csv, session.TheirsPath, session.OutputPath));
        var binary = Path.Combine(_directory, "binary.dat");
        await File.WriteAllBytesAsync(binary, [0, 1, 2, 3, 4, 5]);
        await Assert.ThrowsAsync<InvalidDataException>(() => TextMergeSession.OpenAsync(session.BasePath, binary, session.TheirsPath, session.OutputPath));
        await Assert.ThrowsAsync<FileNotFoundException>(() => TextMergeSession.OpenAsync(session.BasePath, Path.Combine(_directory, "missing.txt"), session.TheirsPath, session.OutputPath));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.SaveAsync(cancellationToken: cancellation.Token));
        Assert.False(File.Exists(session.OutputPath));
    }

    private async Task<TextMergeSession> OpenAsync(string basis, string mine, string theirs, Encoding? encoding = null)
    {
        var paths = FileNames.Select(name => Path.Combine(_directory, name)).ToArray();
        await File.WriteAllTextAsync(paths[0], basis, encoding ?? new UTF8Encoding(false));
        await File.WriteAllTextAsync(paths[1], mine, encoding ?? new UTF8Encoding(false));
        await File.WriteAllTextAsync(paths[2], theirs, encoding ?? new UTF8Encoding(false));
        return await TextMergeSession.OpenAsync(paths[0], paths[1], paths[2], paths[3]);
    }
}
