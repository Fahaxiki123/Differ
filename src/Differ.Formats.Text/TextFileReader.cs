using System.Text;
using System.Text.Unicode;
using Differ.Core.Algorithms;
using Differ.Core.Security;
using Differ.Formats.Text.Resources;

namespace Differ.Formats.Text;

public sealed record TextFileContent(
    string Text,
    string EncodingName,
    string LineEnding,
    bool IsBinary,
    IReadOnlyList<string> Warnings)
{
    public bool HasBom { get; init; }
}

public sealed class TextFileReader : ITextFileReader
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LittleEndianBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianBom = [0xFE, 0xFF];
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    static TextFileReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Task<TextFileContent> ReadAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        return ReadWithEncodingAsync(path, null, cancellationToken);
    }

    public async Task<TextFileContent> ReadWithEncodingAsync(
        string? path,
        string? encodingName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new TextFileContent(
                string.Empty,
                "empty",
                "none",
                IsBinary: false,
                Warnings: []);
        }

        var bytes = await ReadAllBytesSharedAsync(path, cancellationToken).ConfigureAwait(false);
        var (encoding, preambleLength, warning) = DetectEncoding(bytes);
        if (!string.IsNullOrWhiteSpace(encodingName))
        {
            encoding = Encoding.GetEncoding(
                encodingName,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            var preamble = encoding.GetPreamble();
            preambleLength = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
            warning = null;
        }
        if (encoding is null)
        {
            return new TextFileContent(
                string.Empty,
                "binary",
                "unknown",
                IsBinary: true,
                Warnings: [TextStrings.ReaderBinaryData]);
        }

        string text;
        try
        {
            if (encoding.GetCharCount(bytes, preambleLength, bytes.Length - preambleLength)
                > DiffPerformanceOptions.MaximumTextCharacters)
            {
                throw new ComparisonResourceLimitException(TextStrings.ReaderFileTooLarge);
            }

            text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        }
        catch (DecoderFallbackException)
        {
            return new TextFileContent(
                string.Empty,
                encoding.WebName,
                "unknown",
                IsBinary: true,
                Warnings: [TextStrings.ReaderEncodingProbeFailed]);
        }

        var warnings = warning is null ? [] : new[] { warning };
        var lineCount = 1;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (text[index] == '\n' || (text[index] == '\r'
                && (index + 1 == text.Length || text[index + 1] != '\n')))
            {
                if (++lineCount > DiffPerformanceOptions.MaximumTextLines)
                {
                    throw new ComparisonResourceLimitException(TextStrings.ReaderFileTooLarge);
                }
            }
        }

        return new TextFileContent(
            text,
            encoding.WebName,
            DetectLineEnding(text),
            IsBinary: false,
            warnings)
        { HasBom = preambleLength > 0 };
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > DiffPerformanceOptions.MaximumTextFileBytes)
        {
            throw new ComparisonResourceLimitException(TextStrings.ReaderFileTooLarge);
        }

        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static (Encoding? Encoding, int PreambleLength, string? Warning) DetectEncoding(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Bom))
        {
            return (StrictUtf8, 3, null);
        }

        if (bytes.StartsWith(Utf16LittleEndianBom))
        {
            return (new UnicodeEncoding(false, true, true), 2, null);
        }

        if (bytes.StartsWith(Utf16BigEndianBom))
        {
            return (new UnicodeEncoding(true, true, true), 2, null);
        }

        if (bytes.IsEmpty)
        {
            return (StrictUtf8, 0, null);
        }

        if (LooksLikeUtf16(bytes, evenBytesAreNull: false))
        {
            return (
                new UnicodeEncoding(false, false, true),
                0,
                TextStrings.ReaderUtf16LeNoBom);
        }

        if (LooksLikeUtf16(bytes, evenBytesAreNull: true))
        {
            return (
                new UnicodeEncoding(true, false, true),
                0,
                TextStrings.ReaderUtf16BeNoBom);
        }

        if (ContainsSuspiciousControlBytes(bytes))
        {
            return (null, 0, null);
        }

        // Validating the span decides the same question as decoding it, without
        // materialising a throwaway copy of the whole file that is discarded here
        // and then decoded again by the caller.
        if (Utf8.IsValid(bytes))
        {
            return (StrictUtf8, 0, null);
        }

        // Fall through to the Windows Chinese encoding used by many legacy repositories.
        var gb18030 = Encoding.GetEncoding(
            54936,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        try
        {
            // GetCharCount runs the same decoder fallback as GetString, so an
            // invalid byte still throws, but the decoded text is never allocated.
            _ = gb18030.GetCharCount(bytes);
            return (
                gb18030,
                0,
                TextStrings.ReaderGb18030Fallback);
        }
        catch (DecoderFallbackException)
        {
            return (null, 0, null);
        }
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, bool evenBytesAreNull)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        var sampleLength = Math.Min(bytes.Length, 4096);
        var expectedNulls = 0;
        var otherNulls = 0;
        var printableCounterparts = 0;
        for (var index = 0; index < sampleLength; index++)
        {
            if (bytes[index] != 0)
            {
                continue;
            }

            if ((index % 2 == 0) == evenBytesAreNull)
            {
                expectedNulls++;
                var counterpartIndex = index % 2 == 0 ? index + 1 : index - 1;
                if (counterpartIndex >= 0 && counterpartIndex < sampleLength)
                {
                    var counterpart = bytes[counterpartIndex];
                    if (counterpart is 0x09 or 0x0A or 0x0D || counterpart >= 0x20)
                    {
                        printableCounterparts++;
                    }
                }
            }
            else
            {
                otherNulls++;
            }
        }

        var pairCount = sampleLength / 2;
        return expectedNulls >= Math.Max(2, pairCount * 3 / 5)
            && otherNulls <= Math.Max(1, pairCount / 20)
            && printableCounterparts == expectedNulls;
    }

    private static bool ContainsSuspiciousControlBytes(ReadOnlySpan<byte> bytes)
    {
        var sampleLength = Math.Min(bytes.Length, 8192);
        var suspicious = 0;
        for (var index = 0; index < sampleLength; index++)
        {
            var value = bytes[index];
            if (value == 0)
            {
                return true;
            }

            if (value < 0x09 || (value > 0x0D && value < 0x20))
            {
                suspicious++;
            }
        }

        return suspicious > Math.Max(2, sampleLength / 100);
    }

    private static string DetectLineEnding(ReadOnlySpan<char> text)
    {
        // One pass separates CR from CRLF directly. Building a CRLF-free copy of
        // the document instead allocated a second full-size string per file.
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        crlf++;
                        index++;
                    }
                    else
                    {
                        cr++;
                    }

                    break;
                case '\n':
                    lf++;
                    break;
                default:
                    break;
            }
        }

        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        if (kinds > 1)
        {
            return "mixed";
        }

        if (crlf > 0)
        {
            return "CRLF";
        }

        if (lf > 0)
        {
            return "LF";
        }

        if (cr > 0)
        {
            return "CR";
        }

        return "none";
    }
}
