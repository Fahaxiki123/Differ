using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Differ.App.Tests;

/// <summary>
/// A missing DynamicResource key does not fail the build: WPF resolves it to
/// null at runtime and the affected region silently paints transparent or
/// black. These tests close that gap by asserting every resource key the views
/// and custom controls reference actually exists in both theme dictionaries.
/// </summary>
public sealed class ThemeResourceReferenceTests
{
    private static readonly Regex DynamicResourceReference = new(
        @"\{DynamicResource\s+([A-Za-z0-9_.]+)\s*\}",
        RegexOptions.Compiled);

    private static readonly Regex ThemeKeyLiteral = new(
        @"""((?:(?:Diff|Theme|Word)\.[A-Za-z0-9_.]+|[A-Za-z][A-Za-z0-9_.]*(?:Brush|\.Color)))""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryDynamicResourceKeyInViewsExistsInBothThemes()
    {
        var light = ReadKeys("Light.xaml");
        var dark = ReadKeys("Dark.xaml");
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in EnumerateViewXaml())
        {
            foreach (var match in DynamicResourceReference
                .Matches(File.ReadAllText(file))
                .Cast<Match>())
            {
                var key = match.Groups[1].Value;
                if (!light.Contains(key) || !dark.Contains(key))
                {
                    missing.Add(
                        $"{Path.GetFileName(file)} -> {key}"
                        + $" (light={light.Contains(key)}, dark={dark.Contains(key)})");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryThemeKeyLiteralInControlsExistsInBothThemes()
    {
        var light = ReadKeys("Light.xaml");
        var dark = ReadKeys("Dark.xaml");
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        var sources = Directory
            .EnumerateFiles(AppSourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path));

        foreach (var file in sources)
        {
            foreach (var match in ThemeKeyLiteral
                .Matches(File.ReadAllText(file))
                .Cast<Match>())
            {
                var key = match.Groups[1].Value;
                if (!light.Contains(key) || !dark.Contains(key))
                {
                    missing.Add(
                        $"{Path.GetFileName(file)} -> {key}"
                        + $" (light={light.Contains(key)}, dark={dark.Contains(key)})");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void ProductionViewsDoNotContainHardCodedColours()
    {
        var literalColour = new Regex(
            @"#[0-9A-Fa-f]{3,8}\b|(?:Background|Foreground|BorderBrush|Fill|Color)\s*=\s*""(?:White|Black|Red|Green|Blue|Gray|Grey)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in EnumerateViewXaml().Where(path => !IsThemePath(path)))
        {
            foreach (var match in literalColour
                .Matches(File.ReadAllText(path))
                .Cast<Match>())
            {
                offenders.Add($"{Path.GetFileName(path)} -> {match.Value}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void ThemeKeysInProductionXamlUseDynamicResource()
    {
        var themeKeys = ReadKeys("Light.xaml");
        var staticResource = new Regex(
            @"\{StaticResource\s+([A-Za-z0-9_.]+)\s*\}",
            RegexOptions.Compiled);
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in EnumerateViewXaml().Where(path => !IsThemePath(path)))
        {
            foreach (var match in staticResource
                .Matches(File.ReadAllText(path))
                .Cast<Match>())
            {
                if (themeKeys.Contains(match.Groups[1].Value))
                {
                    offenders.Add(
                        $"{Path.GetFileName(path)} -> {match.Groups[1].Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void ProductionControlsDoNotConstructHardCodedColours()
    {
        var literalColour = new Regex(
            @"Color\.(?:FromRgb|FromArgb)|Colors\.|Brushes\.(?!Transparent\b)|new\s+SolidColorBrush\s*\(",
            RegexOptions.Compiled);
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory
            .EnumerateFiles(AppSourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path)))
        {
            foreach (var match in literalColour
                .Matches(File.ReadAllText(path))
                .Cast<Match>())
            {
                offenders.Add($"{Path.GetFileName(path)} -> {match.Value}");
            }
        }

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> EnumerateViewXaml()
    {
        return Directory
            .EnumerateFiles(AppSourceRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path));
    }

    private static bool IsGeneratedPath(string path)
    {
        return path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
            || path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsThemePath(string path)
    {
        return path.Contains(
            $"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ReadKeys(string themeFileName)
    {
        var path = Path.Combine(AppSourceRoot, "Themes", themeFileName);
        var xaml = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
        return XDocument
            .Load(path)
            .Descendants()
            .Select(element => element.Attribute(xaml)?.Value)
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string AppSourceRoot =>
        Path.GetFullPath(Path.Combine(TestFileDirectory, "..", "..", "src", "Differ.App"));

    private static string TestFileDirectory =>
        Path.GetDirectoryName(ThisFile())!;

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
