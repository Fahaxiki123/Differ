using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Differ.App.Tests;

/// <summary>
/// Guards the design-token contract: Light.xaml is the single source of truth
/// for every themed key, and Dark.xaml must define exactly the same key set.
/// A drift here would otherwise surface only at runtime as a transparent brush
/// (a missing <c>DynamicResource</c> resolves to null silently), so the parity
/// is asserted at build time instead.
/// </summary>
public sealed class ThemeResourceParityTests
{
    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void LightAndDarkThemesDefineTheSameKeys()
    {
        var lightKeys = ReadKeys("Light.xaml");
        var darkKeys = ReadKeys("Dark.xaml");

        var missingInDark = lightKeys.Except(darkKeys).OrderBy(key => key).ToArray();
        var missingInLight = darkKeys.Except(lightKeys).OrderBy(key => key).ToArray();

        Assert.True(
            missingInDark.Length == 0,
            $"Dark.xaml is missing keys defined in Light.xaml: {string.Join(", ", missingInDark)}");
        Assert.True(
            missingInLight.Length == 0,
            $"Light.xaml is missing keys defined in Dark.xaml: {string.Join(", ", missingInLight)}");
    }

    [Fact]
    public void BothThemesDeclareTheirName()
    {
        Assert.Contains("Theme.Name", ReadKeys("Light.xaml"));
        Assert.Contains("Theme.Name", ReadKeys("Dark.xaml"));
    }

    private static HashSet<string> ReadKeys(string fileName)
    {
        var path = Path.Combine(ThemesDirectory(), fileName);
        var document = XDocument.Load(path);
        return document
            .Descendants()
            .Select(element => element.Attribute(XamlNamespace + "Key")?.Value)
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ThemesDirectory([CallerFilePath] string callerPath = "")
    {
        // callerPath = <repo>\tests\Differ.App.Tests\ThemeResourceParityTests.cs
        var repoRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(callerPath)!, "..", ".."));
        return Path.Combine(repoRoot, "src", "Differ.App", "Themes");
    }
}
