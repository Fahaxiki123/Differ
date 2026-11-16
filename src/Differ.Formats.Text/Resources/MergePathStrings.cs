using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.Formats.Text.Resources;

internal static class MergePathStrings
{
    private static readonly ResourceManager Manager = new("Differ.Formats.Text.Resources.MergePathStrings", typeof(MergePathStrings).Assembly);
    internal static string InvalidPath => Get();
    internal static string LinkedPath => Get();
    internal static string ProtectedOutput => Get();
    internal static string MissingOutputDirectory => Get();
    internal static string PathIdentityUnavailable => Get();
    private static string Get([CallerMemberName] string name = "") => Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
}
