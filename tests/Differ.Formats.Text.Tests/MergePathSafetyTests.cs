using System.ComponentModel;
using System.Runtime.InteropServices;
using Differ.Core.External;
using Differ.Formats.Text.Resources;

namespace Differ.Formats.Text.Tests;

public sealed class MergePathSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ.MergePath.Tests", Guid.NewGuid().ToString("N"));
    public MergePathSafetyTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void OutputMayBeNewOrIndependentFileWithSameContent()
    {
        var input = Write("source.txt");
        MergePathSafety.AssertOutputSafe(Path.Combine(_directory, "new.txt"), [input]);
        MergePathSafety.AssertOutputSafe(Write("existing.txt"), [input]);
        Assert.Throws<IOException>(() => MergePathSafety.AssertOutputSafe(input, [input]));
        Assert.Throws<IOException>(() => MergePathSafety.AssertOutputSafe(Path.Combine(_directory, ".", "source.txt"), [input]));
        Assert.Throws<DirectoryNotFoundException>(() => MergePathSafety.AssertOutputSafe(Path.Combine(_directory, "missing", "new.txt"), [input]));
        Assert.Throws<IOException>(() => MergePathSafety.NormalizeAndValidate(_directory));
    }

    [Fact]
    public void SessionDirectoryCannotBeAnOutput()
    {
        var sessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Differ", "Sessions");
        Assert.Throws<IOException>(() => MergePathSafety.AssertOutputSafe(Path.Combine(sessions, "not-created", "result.txt"), [Write("source.txt")]));
    }

    [Fact]
    public void PreviewProfileCannotBeAnOutput()
    {
        var target = Path.Combine(ExternalDiffSessionStore.PreviewProfilesPath, "test", "Sessions", "new.txt");
        var error = Assert.Throws<IOException>(() => MergePathSafety.AssertOutputSafe(target, [Write("source.txt")]));
        Assert.Equal(MergePathStrings.ProtectedOutput, error.Message);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("COM1.txt")]
    [InlineData("LPT²")]
    [InlineData("CONOUT$")]
    [InlineData("source.txt:stream")]
    [InlineData("source.txt.")]
    [InlineData("source.txt ")]
    [InlineData("folder.\\result.txt")]
    [InlineData("\\\\?\\C:\\result.txt")]
    [InlineData("\\\\.\\C:\\result.txt")]
    public void WindowsAmbiguousOrDeviceSpellingIsRejected(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<IOException>(() => MergePathSafety.NormalizeAndValidate(Path.Combine(_directory, name)));
    }

    [Fact]
    public void HardLinkedSourceCannotBeUsedAsOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var input = Write("source.txt");
        var linkedOutput = Path.Combine(_directory, "hardlink.txt");
        if (!CreateHardLinkW(linkedOutput, input, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Assert.Throws<IOException>(() => MergePathSafety.AssertOutputSafe(linkedOutput, [input]));
        Assert.Equal("source", File.ReadAllText(input));
    }

    [Fact]
    public void SymbolicFileAndDirectoryAliasesAreRejected()
    {
        var input = Write("source.txt");
        var fileLink = Path.Combine(_directory, "link.txt");
        var linkedDirectory = Path.Combine(_directory, "link-directory");
        try
        {
            File.CreateSymbolicLink(fileLink, input);
            Directory.CreateSymbolicLink(linkedDirectory, _directory);
            Assert.Throws<IOException>(() => MergePathSafety.NormalizeAndValidate(fileLink));
            Assert.Throws<IOException>(() => MergePathSafety.AssertOutputSafe(Path.Combine(linkedDirectory, "new.txt"), [input]));
        }
        finally
        {
            // Remove just the link entries; never recursively traverse their targets.
            if (File.Exists(fileLink)) File.Delete(fileLink);
            if (Directory.Exists(linkedDirectory)) Directory.Delete(linkedDirectory);
        }
    }

    private string Write(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "source");
        return path;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr securityAttributes);
}
