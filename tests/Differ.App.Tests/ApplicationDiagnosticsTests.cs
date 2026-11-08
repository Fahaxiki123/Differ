using System.Diagnostics;
using System.IO;
using System.Reflection;
using Differ.App.Resources;
using Differ.App.Services;

namespace Differ.App.Tests;

public sealed class ApplicationDiagnosticsTests
{
    [Fact]
    public void DescriptionShowsActualBuildRuntimeAndParserVersions()
    {
        var actual = ApplicationDiagnostics.Describe(null);
        var assembly = typeof(App).Assembly;

        Assert.Contains(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            actual,
            StringComparison.Ordinal);
        Assert.Contains(assembly.ManifestModule.ModuleVersionId.ToString("N")[..12], actual, StringComparison.Ordinal);
        Assert.Contains(Environment.Version.ToString(), actual, StringComparison.Ordinal);
        Assert.Contains("Open XML", actual, StringComparison.Ordinal);
        Assert.Contains("NPOI", actual, StringComparison.Ordinal);
        Assert.Contains(Strings.TortoiseNotFound, actual, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingVersionFileDegradesWithoutExposingItsPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe");
        var actual = ApplicationDiagnostics.Describe(missing);

        Assert.Contains(Strings.DiagnosticVersionUnavailable, actual, StringComparison.Ordinal);
        Assert.DoesNotContain(missing, actual, StringComparison.Ordinal);
    }

    [Fact]
    public void LocatedExecutableUsesItsFileVersion()
    {
        var executable = typeof(App).Assembly.Location;
        var version = FileVersionInfo.GetVersionInfo(executable).FileVersion!;

        Assert.Contains($"TortoiseSVN {version}", ApplicationDiagnostics.Describe(executable), StringComparison.Ordinal);
    }
}
