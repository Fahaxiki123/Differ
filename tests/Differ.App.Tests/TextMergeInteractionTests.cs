using System.IO;
using System.Text;
using Differ.App.ViewModels;
using Differ.Formats.Text;

namespace Differ.App.Tests;

public sealed class TextMergeInteractionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.Merge.Interaction", Guid.NewGuid().ToString("N"));
    public TextMergeInteractionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task AutomaticAdvanceIsOffUntilExplicitlyEnabledAsync()
    {
        var owner = await OpenThreeConflictsAsync();
        Assert.False(owner.AutoAdvance);
        Assert.True(owner.Apply("mine", owner.Session.Version));
        Assert.Equal(0, owner.SelectedBlock);
        Assert.Equal(2, owner.Session.UnresolvedCount);
        Assert.False(File.Exists(owner.Session.OutputPath));
    }

    [Fact]
    public async Task AutomaticAdvanceSkipsResolvedItemsWrapsAndStopsOnLastRemainingAsync()
    {
        var owner = await OpenThreeConflictsAsync();
        Assert.True(owner.Select(1));
        Assert.True(owner.Apply("theirs", owner.Session.Version));
        Assert.True(owner.Select(2));
        owner.AutoAdvance = true;
        Assert.True(owner.Apply("base", owner.Session.Version));
        Assert.Equal(0, owner.SelectedBlock); // Wrap past an already resolved middle conflict.
        Assert.Equal(1, owner.Session.UnresolvedCount);
        Assert.True(owner.Apply("confirm", owner.Session.Version));
        Assert.Equal(0, owner.SelectedBlock);
        Assert.Equal(0, owner.Session.UnresolvedCount);
        Assert.False(owner.Session.HasSaved);
        Assert.False(File.Exists(owner.Session.OutputPath));
    }

    [Fact]
    public async Task FailedCommandAndResolvedRechoiceNeverAdvanceAsync()
    {
        var owner = await OpenThreeConflictsAsync();
        owner.AutoAdvance = true;
        Assert.False(owner.Apply("mine", owner.Session.Version - 1));
        Assert.Equal(0, owner.SelectedBlock);
        Assert.True(owner.Apply("mine", owner.Session.Version));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.True(owner.Select(0));
        Assert.True(owner.Apply("theirs", owner.Session.Version));
        Assert.Equal(0, owner.SelectedBlock);
        Assert.Equal(2, owner.Session.UnresolvedCount);
    }

    [Fact]
    public async Task UndoAndRedoSelectTheDecisionRegionInsteadOfAutoAdvancingAsync()
    {
        var owner = await OpenThreeConflictsAsync();
        owner.AutoAdvance = true;
        Assert.True(owner.Apply("mine", owner.Session.Version));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.True(owner.Apply("theirs", owner.Session.Version));
        Assert.Equal(2, owner.SelectedBlock);
        Assert.True(owner.Apply("undo", owner.Session.Version));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.Equal(2, owner.Session.UnresolvedCount);
        Assert.True(owner.Select(2));
        Assert.True(owner.Apply("redo", owner.Session.Version));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.Equal(1, owner.Session.UnresolvedCount);
    }

    [Fact]
    public async Task UndoLengthChangingRechoiceTargetsItsBlockNotShiftedLaterBlocksAsync()
    {
        var owner = await OpenThreeConflictsAsync();
        Assert.True(owner.Apply("mine", owner.Session.Version));
        Assert.True(owner.Apply("base", owner.Session.Version));
        Assert.True(owner.Select(2));
        Assert.True(owner.Apply("undo", owner.Session.Version));
        Assert.Equal(0, owner.SelectedBlock);
        Assert.Contains("first mine longer", owner.Session.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultSelectionUsesCurrentUtf16RangesAndExcludesEndBoundaryAsync()
    {
        var owner = await OpenThreeConflictsAsync();
        var second = owner.Session.Blocks[1];
        Assert.True(owner.SelectResultOffset(second.ResultOffset));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.True(owner.SelectResultOffset(second.ResultOffset + second.ResultLength - 1));
        Assert.False(owner.SelectResultOffset(second.ResultOffset + second.ResultLength));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.False(owner.SelectResultOffset(-1));
        Assert.False(owner.SelectResultOffset(owner.Session.ResultText.Length + 1));
        Assert.False(owner.SelectResultOffset(0));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.True(owner.Select(0));
        Assert.True(owner.Apply("mine", owner.Session.Version));
        second = owner.Session.Blocks[1];
        Assert.True(owner.SelectResultOffset(second.ResultOffset));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.Contains("😀", owner.Session.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyInsertionAndDeletionAnchorsSelectExactlyAndCanBeRechosenAsync()
    {
        var owner = await OpenAsync("header\nanchor\ndelete me\nfooter\n",
            "header\nmy inserted\nanchor\nfooter\n",
            "header\ntheir inserted\nanchor\nchanged deletion\nfooter\n");
        Assert.Equal(2, owner.Session.UnresolvedCount);
        var insertion = owner.Session.Blocks[0];
        Assert.Equal(0, insertion.ResultLength);
        Assert.True(owner.Select(1));
        Assert.False(owner.SelectResultOffset(insertion.ResultOffset - 1));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.True(owner.SelectResultOffset(insertion.ResultOffset));
        Assert.Equal(0, owner.SelectedBlock);
        Assert.True(owner.Apply("base", owner.Session.Version));
        Assert.True(owner.Select(1));
        Assert.True(owner.Apply("mine", owner.Session.Version));
        var deletion = owner.Session.Blocks[1];
        Assert.Equal(0, deletion.ResultLength);
        Assert.True(owner.Select(0));
        Assert.True(owner.SelectResultOffset(deletion.ResultOffset));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.True(owner.Apply("theirs", owner.Session.Version));
        Assert.Contains("changed deletion", owner.Session.ResultText, StringComparison.Ordinal);
        Assert.Equal(0, owner.Session.UnresolvedCount);
    }

    [Fact]
    public async Task EndOfFileEmptyAnchorCanBeSelectedAndResolvedWithoutTextChangeAsync()
    {
        var owner = await OpenAsync("header\n", "header\nmy ending\n", "header\ntheir ending\n");
        Assert.True(owner.SelectResultOffset(owner.Session.ResultText.Length));
        owner.AutoAdvance = true;
        Assert.True(owner.Apply("base", owner.Session.Version));
        Assert.Equal(0, owner.SelectedBlock);
        Assert.Equal("header\n", owner.Session.ResultText);
        Assert.Equal(0, owner.Session.UnresolvedCount);
        Assert.True(owner.Apply("undo", owner.Session.Version));
        Assert.Equal(0, owner.SelectedBlock);
        Assert.Equal(1, owner.Session.UnresolvedCount);
    }

    [Fact]
    public async Task OverlappingManualRangesPreferCurrentOtherwiseLowestAndDoNotRestoreChoosePermissionAsync()
    {
        var owner = await OpenThreeConflictsAsync();
        var first = owner.Session.Blocks[0];
        var second = owner.Session.Blocks[1];
        var changed = owner.Session.ResultText.Remove(first.ResultOffset, second.ResultOffset + second.ResultLength - first.ResultOffset)
            .Insert(first.ResultOffset, "manually combined\n");
        Assert.True(owner.UpdateResult(changed, owner.Session.Version));
        Assert.False(owner.Session.Blocks[0].CanChoose);
        Assert.False(owner.Session.Blocks[1].CanChoose);
        Assert.True(owner.Select(1));
        Assert.True(owner.SelectResultOffset(first.ResultOffset));
        Assert.Equal(1, owner.SelectedBlock);
        Assert.False(owner.Apply("mine", owner.Session.Version));
        Assert.True(owner.Select(2));
        Assert.True(owner.SelectResultOffset(first.ResultOffset));
        Assert.Equal(0, owner.SelectedBlock);
        Assert.False(owner.Session.Blocks[0].CanChoose);
        owner.AutoAdvance = true;
        Assert.True(owner.Apply("confirm", owner.Session.Version));
        Assert.Equal(1, owner.SelectedBlock);
    }

    [Fact]
    public async Task NavigationWithoutSelectionStartsAtFirstOrLastAsync()
    {
        var owner = await OpenThreeConflictsAsync();
        // The public setter intentionally does not permit arbitrary selection. Seed
        // the absent-selection state to verify navigation's defensive initialization.
        var selection = typeof(TextMergeViewModel).GetProperty(nameof(TextMergeViewModel.SelectedBlock))!;
        selection.SetValue(owner, -1);
        owner.Navigate(-1);
        Assert.Equal(2, owner.SelectedBlock);
        selection.SetValue(owner, -1);
        owner.Navigate(1);
        Assert.Equal(0, owner.SelectedBlock);
        owner.Navigate(-1);
        Assert.Equal(2, owner.SelectedBlock);
    }

    private Task<TextMergeViewModel> OpenThreeConflictsAsync() => OpenAsync(
        "😀 header\nfirst base\nkeep one\nsecond base\nkeep two\nthird base\nfooter\n",
        "😀 header\nfirst mine longer\nkeep one\nsecond mine\nkeep two\nthird mine\nfooter\n",
        "😀 header\nfirst theirs\nkeep one\nsecond theirs\nkeep two\nthird theirs\nfooter\n");

    private async Task<TextMergeViewModel> OpenAsync(string basis, string mine, string theirs)
    {
        var basePath = Path.Combine(_directory, "base.txt");
        var minePath = Path.Combine(_directory, "mine.txt");
        var theirsPath = Path.Combine(_directory, "theirs.txt");
        await File.WriteAllTextAsync(basePath, basis, new UTF8Encoding(false));
        await File.WriteAllTextAsync(minePath, mine, new UTF8Encoding(false));
        await File.WriteAllTextAsync(theirsPath, theirs, new UTF8Encoding(false));
        return new TextMergeViewModel(await TextMergeSession.OpenAsync(basePath, minePath, theirsPath,
            Path.Combine(_directory, "result.txt")));
    }
}
