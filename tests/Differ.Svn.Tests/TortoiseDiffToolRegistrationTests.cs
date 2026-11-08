namespace Differ.Svn.Tests;

public sealed class TortoiseDiffToolRegistrationTests
{
    [Fact]
    public void BuildCommandUsesDocumentedTortoiseSvnPlaceholders()
    {
        var command = TortoiseDiffToolRegistration.BuildCommand(
            @"E:\Differ Publish\Differ.App.exe");

        Assert.StartsWith(
            "\"E:\\Differ Publish\\Differ.App.exe\"",
            command,
            StringComparison.Ordinal);
        Assert.Contains("--base %base", command, StringComparison.Ordinal);
        Assert.Contains("--mine %mine", command, StringComparison.Ordinal);
        Assert.Contains("--base-name %bname", command, StringComparison.Ordinal);
        Assert.Contains("--mine-name %yname", command, StringComparison.Ordinal);
        Assert.Contains("--base-revision %brev", command, StringComparison.Ordinal);
        Assert.Contains("--mine-revision %yrev", command, StringComparison.Ordinal);
        Assert.Contains("--file-name %fname", command, StringComparison.Ordinal);
        Assert.Contains("--source tortoisesvn", command, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedExtensionsContainOnlyImplementedFormats()
    {
        Assert.Contains(".cs", TortoiseDiffToolRegistration.SupportedExtensions);
        Assert.Contains(".docx", TortoiseDiffToolRegistration.SupportedExtensions);
        Assert.Contains(".rtf", TortoiseDiffToolRegistration.SupportedExtensions);
        Assert.Contains(".xlsx", TortoiseDiffToolRegistration.SupportedExtensions);
        Assert.Contains(".xlsm", TortoiseDiffToolRegistration.SupportedExtensions);
        Assert.Contains(".xls", TortoiseDiffToolRegistration.SupportedExtensions);
        Assert.Contains(".csv", TortoiseDiffToolRegistration.SupportedExtensions);
        Assert.Contains(".doc", TortoiseDiffToolRegistration.SupportedExtensions);
        Assert.DoesNotContain(".pdf", TortoiseDiffToolRegistration.SupportedExtensions);
    }
}
