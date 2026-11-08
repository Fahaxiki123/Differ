using Differ.Core.External;

namespace Differ.Core.Tests;

public sealed class ExternalDiffArgumentsTests
{
    [Fact]
    public void ParseReturnsNoArgumentsForDirectLaunch()
    {
        var result = ExternalDiffArguments.Parse([]);

        Assert.Equal(ExternalDiffArgumentStatus.NoArguments, result.Status);
        Assert.Null(result.Request);
    }

    [Fact]
    public void ParseMapsTortoiseSvnNamedArgumentsInAnyOrder()
    {
        var result = ExternalDiffArguments.Parse(
        [
            "--mine-name", "W-物品表.xlsx：工作副本",
            "--base", @"C:\Temp\W-物品表-r22.xlsx",
            "--file-name=W-物品表.xlsx",
            "--mine", @"C:\工作区\测试用例\W-物品表.xlsx",
            "--base-revision", "22",
            "--base-name", "W-物品表.xlsx：版本 22",
            "--mine-revision", "working",
            "--source", "tortoisesvn"
        ]);

        Assert.Equal(ExternalDiffArgumentStatus.Success, result.Status);
        Assert.NotNull(result.Request);
        Assert.Equal(@"C:\Temp\W-物品表-r22.xlsx", result.Request.BaseFilePath);
        Assert.Equal(@"C:\工作区\测试用例\W-物品表.xlsx", result.Request.MineFilePath);
        Assert.Equal("W-物品表.xlsx：版本 22", result.Request.BaseDisplayName);
        Assert.Equal("W-物品表.xlsx：工作副本", result.Request.MineDisplayName);
        Assert.Equal("22", result.Request.BaseRevision);
        Assert.Equal("working", result.Request.MineRevision);
        Assert.Equal("tortoisesvn", result.Request.InvocationSource);
        Assert.Equal("W-物品表.xlsx", result.Request.OriginalFileName);
        Assert.Equal("W-物品表.xlsx", result.Request.FileTypePath);
    }

    [Fact]
    public void ParseSupportsTwoPositionalFiles()
    {
        var result = ExternalDiffArguments.Parse(
        [
            @"C:\Temp\Base File.cs",
            @"C:\Work\Current File.cs"
        ]);

        Assert.Equal(ExternalDiffArgumentStatus.Success, result.Status);
        Assert.Equal(@"C:\Temp\Base File.cs", result.Request?.BaseFilePath);
        Assert.Equal(@"C:\Work\Current File.cs", result.Request?.MineFilePath);
    }

    [Fact]
    public void ParseSupportsLegacySixPositionalProtocol()
    {
        var result = ExternalDiffArguments.Parse(
        [
            @"C:\Temp\W-物品表-r22.xlsx",
            @"C:\工作区\测试用例\W-物品表.xlsx",
            "W-物品表.xlsx：版本 22",
            "W-物品表.xlsx：工作副本",
            "22",
            "working"
        ]);

        Assert.Equal(ExternalDiffArgumentStatus.Success, result.Status);
        Assert.NotNull(result.Request);
        Assert.Equal(@"C:\Temp\W-物品表-r22.xlsx", result.Request.BaseFilePath);
        Assert.Equal(@"C:\工作区\测试用例\W-物品表.xlsx", result.Request.MineFilePath);
        Assert.Equal("W-物品表.xlsx：版本 22", result.Request.BaseDisplayName);
        Assert.Equal("W-物品表.xlsx：工作副本", result.Request.MineDisplayName);
        Assert.Equal("22", result.Request.BaseRevision);
        Assert.Equal("working", result.Request.MineRevision);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public void ParseRejectsUnsupportedPositionalCounts(int count)
    {
        var arguments = Enumerable.Range(0, count)
            .Select(index => $"arg{index}.txt")
            .ToArray();

        var result = ExternalDiffArguments.Parse(arguments);

        Assert.Equal(ExternalDiffArgumentStatus.Error, result.Status);
        Assert.Equal(ExternalDiffArgumentError.InvalidPositionalCount, result.Error);
    }

    [Theory]
    [InlineData("--base", "base.txt")]
    [InlineData("--mine", "mine.txt")]
    public void ParseRequiresBothInputFiles(params string[] arguments)
    {
        var result = ExternalDiffArguments.Parse(arguments);

        Assert.Equal(ExternalDiffArgumentStatus.Error, result.Status);
        Assert.Equal(ExternalDiffArgumentError.MissingRequiredOption, result.Error);
    }

    [Fact]
    public void ParseRejectsUnknownOptions()
    {
        var result = ExternalDiffArguments.Parse(
        [
            "--base", "base.txt",
            "--mine", "mine.txt",
            "--mystery", "value"
        ]);

        Assert.Equal(ExternalDiffArgumentStatus.Error, result.Status);
        Assert.Equal(ExternalDiffArgumentError.UnknownOption, result.Error);
        Assert.Equal("--mystery", result.ErrorArgument);
    }

    [Fact]
    public void ParseRejectsDuplicateOptions()
    {
        var result = ExternalDiffArguments.Parse(
        [
            "--base", "one.txt",
            "--base", "two.txt",
            "--mine", "mine.txt"
        ]);

        Assert.Equal(ExternalDiffArgumentStatus.Error, result.Status);
        Assert.Equal(ExternalDiffArgumentError.DuplicateOption, result.Error);
    }
}
