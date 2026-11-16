using System.IO;
using System.Security.Cryptography;
using System.Text;
using Differ.App.Resources;
using Differ.Core.External;

namespace Differ.App.Services;

internal static class AppRuntimeProfile
{
#if DIFFER_ISOLATED_PREVIEW
    private static readonly string ProfileId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)).ToUpperInvariant())).AsSpan(0, 12));

    public static bool IsPreview => true;
    public static string DataRoot { get; } = Path.Combine(ExternalDiffSessionStore.PreviewProfilesPath, ProfileId);
#else
    public static bool IsPreview => false;
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Differ");
#endif

    public static string CreateDiffInstanceKey()
    {
        var desktopKey = ExternalDiffInstanceCoordinator.CreateCurrentDesktopSessionKey();
#if DIFFER_ISOLATED_PREVIEW
        return $"{desktopKey}.preview.{ProfileId}";
#else
        return desktopKey;
#endif
    }

    public static void EnsureIntegrationWritable()
    {
        if (IsPreview) throw new InvalidOperationException(ChromeStrings.PreviewIntegrationReadOnly);
    }
}
