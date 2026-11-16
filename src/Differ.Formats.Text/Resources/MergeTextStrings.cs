using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.Formats.Text.Resources;

public static class MergeTextStrings
{
    private static readonly ResourceManager Manager = new("Differ.Formats.Text.Resources.MergeTextStrings", typeof(MergeTextStrings).Assembly);
    public static string TooLarge => Get();
    public static string CoarseMerge => Get();
    public static string UnsupportedFormat => Get();
    public static string MissingInput => Get();
    public static string Unresolved => Get();
    public static string ProtectedTarget => Get();
    public static string InvalidPath => Get();
    public static string ExternalChange => Get();
    public static string MixedNewlines => Get();
    public static string Saving => Get();
    private static string Get([CallerMemberName] string name = "") => Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
}
