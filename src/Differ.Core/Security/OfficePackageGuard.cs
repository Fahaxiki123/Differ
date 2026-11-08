using System.Globalization;
using System.IO.Compression;
using System.Text;
using Differ.Core.Resources;

namespace Differ.Core.Security;

public static class OfficePackageGuard
{
    public const int DefaultMaxEntries = 20_000;
    public const long DefaultMaxExpandedBytes = 1L * 1024 * 1024 * 1024;
    public const double DefaultMaxCompressionRatio = 250;

    private static readonly CompositeFormat TooManyPartsFormat =
        CompositeFormat.Parse(CoreStrings.GuardTooManyPartsFormat);
    private static readonly CompositeFormat ExpandedTooLargeFormat =
        CompositeFormat.Parse(CoreStrings.GuardExpandedTooLargeFormat);
    private static readonly CompositeFormat SuspiciousRatioFormat =
        CompositeFormat.Parse(CoreStrings.GuardSuspiciousRatioFormat);

    public static void Validate(
        string path,
        int maxEntries = DefaultMaxEntries,
        long maxExpandedBytes = DefaultMaxExpandedBytes,
        double maxCompressionRatio = DefaultMaxCompressionRatio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var archive = OpenArchive(stream);

        if (archive.Entries.Count > maxEntries)
        {
            throw new ComparisonResourceLimitException(string.Format(
                CultureInfo.CurrentCulture,
                TooManyPartsFormat,
                archive.Entries.Count,
                maxEntries));
        }

        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            // Crafted archives can declare absurd entry lengths. Saturate and
            // compare before adding so a malicious header surfaces as an
            // resource-limit exception instead of an OverflowException.
            var entryLength = Math.Max(entry.Length, 0);
            if (entryLength > maxExpandedBytes - expandedBytes)
            {
                throw new ComparisonResourceLimitException(string.Format(
                    CultureInfo.CurrentCulture,
                    ExpandedTooLargeFormat,
                    maxExpandedBytes));
            }

            expandedBytes += entryLength;

            if (entryLength <= 1024 * 1024 || entry.CompressedLength == 0)
            {
                continue;
            }

            var ratio = (double)entryLength / entry.CompressedLength;
            if (ratio > maxCompressionRatio)
            {
                throw new ComparisonResourceLimitException(string.Format(
                    CultureInfo.CurrentCulture,
                    SuspiciousRatioFormat,
                    entry.FullName,
                    ratio));
            }
        }
    }

    private static ZipArchive OpenArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException exception)
        {
            // Truncated central directories can produce a normal IOException
            // from an impossible seek, wrapped by ZipArchive as invalid data.
            // The stream was already opened; retain this explicit structural
            // classification and the original cause for the coordinator.
            throw new StructuralParseException(CoreStrings.GuardInvalidPackageStructure, exception)
            {
                IsArchiveStructureFailure = true
            };
        }
    }
}
