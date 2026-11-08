using System.IO.Compression;
using Differ.Core.Security;

namespace Differ.Core.Tests;

public sealed class OfficePackageGuardTests
{
    [Fact]
    public void PackageSafetyBudgetHasADistinctNonFallbackError()
    {
        var root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "test.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var part = archive.CreateEntry("large.xml", CompressionLevel.SmallestSize).Open();
                part.Write(new byte[2 * 1024 * 1024]);
            }

            Assert.Throws<ComparisonResourceLimitException>(
                () => OfficePackageGuard.Validate(path, maxEntries: 0));
            Assert.Throws<ComparisonResourceLimitException>(
                () => OfficePackageGuard.Validate(path, maxExpandedBytes: 1024));
            Assert.Throws<ComparisonResourceLimitException>(
                () => OfficePackageGuard.Validate(path));

            var broken = Path.Combine(root, "broken.zip");
            File.WriteAllText(broken, "invalid package bytes");
            var error = Assert.Throws<StructuralParseException>(() => OfficePackageGuard.Validate(broken));
            Assert.IsType<InvalidDataException>(error.InnerException);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
