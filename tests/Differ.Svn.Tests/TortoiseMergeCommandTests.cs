using Differ.Svn;

namespace Differ.Svn.Tests;

public sealed class TortoiseMergeCommandTests
{
    [Fact]
    public void TemplateUsesPublishedFourFileProtocolWithoutRegistryAccess()
    {
        var command = TortoiseMergeCommand.Create("C:\\中文 工具\\Differ.App.exe");
        Assert.StartsWith("\"C:\\中文 工具\\Differ.App.exe\" --merge ", command);
        Assert.Contains("--base %base --mine %mine --theirs %theirs --merged %merged", command, StringComparison.Ordinal);
        Assert.Contains("--theirs-name %tname --merged-name %mname", command, StringComparison.Ordinal);
    }
}
