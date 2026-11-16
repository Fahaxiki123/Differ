using Differ.Core.External;

namespace Differ.Core.Tests;

public sealed class ExternalMergeArgumentsTests
{
    [Fact]
    public void FourNamedPathsKeepTitlesAndIgnoreOrder()
    {
        var result = ExternalMergeArguments.Parse(["--merged", "C:\\结果 文件.txt", "--mine-name=我的修改", "--merge",
            "--theirs", "theirs.txt", "--mine", "mine.txt", "--base", "base.txt", "--source", "tortoisesvn"]);
        Assert.Equal(ExternalDiffArgumentError.None, result.Error);
        Assert.NotNull(result.Request);
        Assert.Equal("C:\\结果 文件.txt", result.Request.MergedFilePath);
        Assert.Equal("我的修改", result.Request.MineDisplayName);
        Assert.Equal("base.txt", result.Request.BaseFilePath);
        Assert.Equal("theirs.txt", result.Request.TheirsFilePath);
    }

    [Theory]
    [InlineData("--merged", ExternalDiffArgumentError.MissingOptionValue)]
    [InlineData("--unknown", ExternalDiffArgumentError.UnknownOption)]
    [InlineData("--merge", ExternalDiffArgumentError.DuplicateOption)]
    public void InvalidArgumentsFailClosed(string tail, ExternalDiffArgumentError error)
    {
        var result = ExternalMergeArguments.Parse(["--merge", "--base", "b", "--mine", "m", "--theirs", "t", tail]);
        Assert.Null(result.Request);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void MissingModeOrOutputCannotBeTreatedAsTwoWayDiff()
    {
        Assert.True(ExternalMergeArguments.IsMergeInvocation(["--merged=x"]));
        Assert.True(ExternalMergeArguments.IsMergeInvocation(["--merge"]));
        Assert.False(ExternalMergeArguments.IsMergeInvocation(["base", "mine"]));
        Assert.Equal("--merge", ExternalMergeArguments.Parse(["--base=b", "--mine=m", "--theirs=t", "--merged=o"]).ErrorArgument);
        Assert.Equal("--merged", ExternalMergeArguments.Parse(["--merge", "--base=b", "--mine=m", "--theirs=t"]).ErrorArgument);
        Assert.Equal(ExternalDiffArgumentStatus.Success, ExternalDiffArguments.Parse(["base", "mine"]).Status);
        Assert.Equal(ExternalDiffArgumentStatus.Success, ExternalDiffArguments.Parse(["base", "mine", "bname", "yname", "1", "2"]).Status);
    }
}
