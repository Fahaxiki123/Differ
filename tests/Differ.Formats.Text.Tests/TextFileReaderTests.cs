using System.Text;

namespace Differ.Formats.Text.Tests;

public sealed class TextFileReaderTests
{
    [Fact]
    public async Task ReadDetectsUtf8AndLineEndingAsync()
    {
        var path = CreateTestFilePath(".txt");
        try
        {
            await File.WriteAllTextAsync(
                path,
                "第一行\r\n第二行\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = await new TextFileReader().ReadAsync(path);

            Assert.False(result.IsBinary);
            Assert.Equal("utf-8", result.EncodingName);
            Assert.Equal("CRLF", result.LineEnding);
            Assert.Contains("第二行", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadDetectsLegacyChineseEncodingWithWarningAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = CreateTestFilePath(".txt");
        try
        {
            var encoding = Encoding.GetEncoding(54936);
            await File.WriteAllBytesAsync(path, encoding.GetBytes("中文内容\r\n第二行"));

            var result = await new TextFileReader().ReadAsync(path);

            Assert.False(result.IsBinary);
            Assert.Equal("gb18030", result.EncodingName);
            Assert.Contains(result.Warnings, warning => warning.Contains("GB18030"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadRejectsBinaryDataAsync()
    {
        var path = CreateTestFilePath(".dat");
        try
        {
            await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x03, 0x00, 0x10]);

            var result = await new TextFileReader().ReadAsync(path);

            Assert.True(result.IsBinary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTestFilePath(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "Differ.Tests");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{Guid.NewGuid():N}{extension}");
    }
}
