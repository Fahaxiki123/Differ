using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Differ.App.Resources;
using DocumentFormat.OpenXml.Packaging;
using NPOI.HSSF.UserModel;

namespace Differ.App.Services;

internal static class ApplicationDiagnostics
{
    private static readonly CompositeFormat DescriptionFormat = CompositeFormat.Parse(Strings.DiagnosticsFormat);

    public static string Describe(string? tortoiseExecutablePath)
    {
        var applicationAssembly = typeof(App).Assembly;
        return string.Format(
            CultureInfo.CurrentCulture,
            DescriptionFormat,
            GetVersion(applicationAssembly),
            applicationAssembly.ManifestModule.ModuleVersionId.ToString("N")[..12],
            Environment.Version.ToString(),
            GetVersion(typeof(WordprocessingDocument).Assembly),
            GetVersion(typeof(HSSFWorkbook).Assembly),
            GetTortoiseVersion(tortoiseExecutablePath));
    }

    private static string GetVersion(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? Strings.DiagnosticVersionUnavailable;
    }

    private static string GetTortoiseVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Strings.TortoiseNotFound;
        }

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return version.FileVersion ?? version.ProductVersion ?? Strings.DiagnosticVersionUnavailable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Strings.DiagnosticVersionUnavailable;
        }
    }
}
