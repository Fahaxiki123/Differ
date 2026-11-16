using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Differ.Core.External;
using Differ.Formats.Text.Resources;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("Differ.Formats.Text.Tests")]

namespace Differ.Formats.Text;

/// <summary>Validates merge output paths independently of their display names or file contents.</summary>
internal static class MergePathSafety
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string NormalizeAndValidate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows()) ValidateWindowsSpelling(path);
        var fullPath = Path.GetFullPath(path);
        AssertNoReparseAncestors(fullPath);
        if (Directory.Exists(fullPath)) throw new IOException(MergePathStrings.InvalidPath);
        return OperatingSystem.IsWindows() ? ExpandShortNames(fullPath) : fullPath;
    }

    internal static void AssertOutputSafe(string target, IReadOnlyList<string> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        target = NormalizeAndValidate(target);
        var sessions = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Differ", "Sessions"));
        if (IsSameOrChild(target, sessions) || ExternalDiffSessionStore.IsWithinPreviewProfile(target))
            throw new IOException(MergePathStrings.ProtectedOutput);
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException(MergePathStrings.MissingOutputDirectory);

        FileIdentity? targetIdentity = OperatingSystem.IsWindows() && File.Exists(target) ? GetIdentity(target) : null;
        foreach (var input in inputs)
        {
            var fullInput = NormalizeAndValidate(input);
            if (string.Equals(fullInput, target, PathComparison))
                throw new IOException(MergePathStrings.ProtectedOutput);
            // Path spelling alone does not detect NTFS hard links or 8.3 short-name aliases.
            if (targetIdentity is { } identity && GetIdentity(fullInput) == identity)
                throw new IOException(MergePathStrings.ProtectedOutput);
        }
    }

    private static bool IsSameOrChild(string path, string parent) => string.Equals(path, parent, PathComparison)
        || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    private static void AssertNoReparseAncestors(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(MergePathStrings.LinkedPath);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateWindowsSpelling(string path)
    {
        var normalizedSeparators = path.Replace('/', '\\');
        if (normalizedSeparators.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || normalizedSeparators.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || normalizedSeparators.StartsWith("\\??\\", StringComparison.Ordinal))
            throw new IOException(MergePathStrings.InvalidPath);

        for (var index = 0; index < path.Length; index++)
            if (path[index] == ':' && (index != 1 || !char.IsAsciiLetter(path[0])))
                throw new IOException(MergePathStrings.InvalidPath);

        foreach (var part in normalizedSeparators.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.Length == 2 && part[1] == ':') continue;
            if (part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || part.Any(char.IsControl)) throw new IOException(MergePathStrings.InvalidPath);
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && "123456789¹²³".Contains(stem[3], StringComparison.Ordinal))
                throw new IOException(MergePathStrings.InvalidPath);
        }
    }

    private static FileIdentity GetIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException(MergePathStrings.PathIdentityUnavailable, new Win32Exception(Marshal.GetLastPInvokeError()));
        return new FileIdentity(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }

    private static string ExpandShortNames(string path)
    {
        // Canonicalize existing ancestors too: a new output beneath SESSIO~1
        // must not evade the protected Sessions directory check.
        var remaining = new Stack<string>();
        var existing = path;
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            remaining.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(existing)) throw new IOException(MergePathStrings.InvalidPath);
        }
        var buffer = new char[32768];
        var length = GetLongPathNameW(existing, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length)
            throw new IOException(MergePathStrings.PathIdentityUnavailable, new Win32Exception(Marshal.GetLastPInvokeError()));
        var expanded = new string(buffer, 0, (int)length);
        while (remaining.Count > 0) expanded = Path.Combine(expanded, remaining.Pop());
        return expanded;
    }

    private readonly record struct FileIdentity(uint Volume, uint IndexHigh, uint IndexLow);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);
}
