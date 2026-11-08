using Differ.Svn;
using Differ.Svn.Abstractions;

namespace Differ.Svn.Tests;

public sealed class SvnClientLocatorTests
{
    [Fact]
    public void FindTortoiseProcUsesValidatedRegisteredCandidateInUnicodeDirectory()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var executable = CreateExecutable(testRoot, "公司 工具", "TortoiseProc.exe");
            var locator = new SvnClientLocator(
                new CandidatePaths([Path.Combine(testRoot, "missing.exe"), executable]),
                string.Empty,
                []);

            Assert.Equal(executable, locator.FindTortoiseProc());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void RegisteredCommandLinesAndOtherExecutablesAreNotAcceptedAsTortoiseProc()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var executable = CreateExecutable(testRoot, "工具", "TortoiseProc.exe");
            var otherExecutable = CreateExecutable(testRoot, "工具", "OtherTool.exe");
            var locator = new SvnClientLocator(
                new CandidatePaths([$"\"{executable}\" /command:settings", otherExecutable, "TortoiseProc.exe"]),
                string.Empty,
                []);

            Assert.Null(locator.FindTortoiseProc());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void ConfiguredAndPathLocationsKeepTheirExistingPrecedence()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var configured = CreateExecutable(testRoot, "configured", "TortoiseProc.exe");
            var onPath = CreateExecutable(testRoot, "path", "TortoiseProc.exe");
            var registered = CreateExecutable(testRoot, "registry", "TortoiseProc.exe");
            var locator = new SvnClientLocator(
                new CandidatePaths([registered]),
                $"\"{Path.GetDirectoryName(onPath)}\"",
                []);

            Assert.Equal(configured, locator.FindTortoiseProc(configured));
            Assert.Equal(onPath, locator.FindTortoiseProc());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void StaleRegisteredPathFallsBackToStandardInstallation()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var executable = CreateExecutable(testRoot,
                Path.Combine("TortoiseSVN", "bin"), "TortoiseProc.exe");
            var locator = new SvnClientLocator(
                new CandidatePaths([Path.Combine(testRoot, "stale", "TortoiseProc.exe")]),
                string.Empty,
                [testRoot]);

            Assert.Equal(executable, locator.FindTortoiseProc());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void MissingTortoiseInstallationDoesNotFallBackToSvnExe()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            CreateExecutable(testRoot, "bin", "svn.exe");
            var locator = new SvnClientLocator(
                new CandidatePaths([]), Path.Combine(testRoot, "bin"), []);

            Assert.Null(locator.FindTortoiseProc());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void FindWorkingCopyRootWalksUpFromNestedFile()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(testRoot, ".svn"));
            var nested = Path.Combine(testRoot, "src", "feature");
            Directory.CreateDirectory(nested);
            var filePath = Path.Combine(nested, "sample.cs");
            File.WriteAllText(filePath, "// sample");

            var result = new SvnClientLocator().FindWorkingCopyRoot(filePath);

            Assert.Equal(testRoot, result);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Differ.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateExecutable(string root, string directory, string name)
    {
        var path = Path.Combine(root, directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Test executable placeholder; never launched.");
        return path;
    }

    private sealed class CandidatePaths(IEnumerable<string> paths) : ITortoiseSvnInstallationPaths
    {
        public IEnumerable<string> GetExecutableCandidates() => paths;
    }
}
