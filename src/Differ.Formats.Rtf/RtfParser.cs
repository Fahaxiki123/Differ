using System.Text;
using Differ.Formats.Rtf.Resources;

namespace Differ.Formats.Rtf;

/// <summary>Read visible RTF text without evaluating fields or embedded objects.</summary>
internal static class RtfParser
{
    private static readonly Lazy<Encoding> Gb18030 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    });

    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "listtable", "listoverridetable", "info",
        "pict", "object", "header", "headerl", "headerr", "headerf", "footer", "footerl",
        "footerr", "footerf", "footnote", "fldinst", "themedata", "colorschememapping",
        "latentstyles", "datastore", "generator"
    };

    public static List<string> ExtractParagraphs(string rtf, CancellationToken cancellationToken = default)
    {
        if (rtf.Length == 0)
        {
            return [];
        }

        if (!rtf.AsSpan().TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            throw new InvalidDataException(RtfStrings.InvalidDocument);
        }

        var paragraphs = new List<string>();
        var current = new StringBuilder();
        var pendingBytes = new List<byte>();
        var groups = new Stack<(bool Skip, int UnicodeFallback)>();
        var skip = false;
        var unicodeFallback = 1;
        var fallbackRemaining = 0;

        for (var index = 0; index < rtf.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var character = rtf[index];
            if (character is '{' or '}')
            {
                FlushBytes(pendingBytes, current);
                // A group delimiter terminates any unfinished ANSI fallback.
                fallbackRemaining = 0;
                if (character == '{')
                {
                    groups.Push((skip, unicodeFallback));
                }
                else if (groups.TryPop(out var state))
                {
                    (skip, unicodeFallback) = state;
                }
                else
                {
                    throw new InvalidDataException(RtfStrings.InvalidDocument);
                }

                continue;
            }

            if (character is '\r' or '\n')
            {
                continue;
            }

            if (character != '\\')
            {
                if (groups.Count == 0)
                {
                    if (!char.IsWhiteSpace(character))
                    {
                        throw new InvalidDataException(RtfStrings.InvalidDocument);
                    }

                    continue;
                }

                Emit(character);
                continue;
            }

            if (++index >= rtf.Length)
            {
                throw new InvalidDataException(RtfStrings.InvalidDocument);
            }

            var next = rtf[index];
            if (next == '\'')
            {
                if (index + 2 >= rtf.Length || !char.IsAsciiHexDigit(rtf[index + 1])
                    || !char.IsAsciiHexDigit(rtf[index + 2]))
                {
                    throw new InvalidDataException(RtfStrings.InvalidDocument);
                }

                var value = (byte)Convert.ToInt32(rtf.Substring(index + 1, 2), 16);
                index += 2;
                if (!skip && !ConsumeFallback())
                {
                    pendingBytes.Add(value);
                }

                continue;
            }

            if (!char.IsAsciiLetter(next))
            {
                if (next == '*')
                {
                    skip = true;
                }
                else if (next is '\\' or '{' or '}')
                {
                    Emit(next);
                }
                else if (next == '~')
                {
                    Emit('\u00a0');
                }
                else if (next == '_')
                {
                    Emit('\u2011');
                }
                else if (next == '-')
                {
                    Emit('\u00ad');
                }

                continue;
            }

            var (word, parameter, endIndex) = ReadControlWord(rtf, index);
            index = endIndex;
            if (word == "bin")
            {
                if (parameter is not >= 0 || parameter.Value > rtf.Length - index - 1 || !skip)
                {
                    throw new InvalidDataException(RtfStrings.InvalidDocument);
                }

                index += parameter.Value;
                continue;
            }

            if (SkippedDestinations.Contains(word))
            {
                FlushBytes(pendingBytes, current);
                skip = true;
            }

            if (skip)
            {
                continue;
            }

            if (ConsumeFallback())
            {
                continue;
            }

            switch (word)
            {
                case "uc" when parameter is >= 0:
                    unicodeFallback = parameter.Value;
                    break;
                case "u" when parameter is >= -32768 and <= 65535:
                    FlushBytes(pendingBytes, current);
                    current.Append(unchecked((char)parameter.Value));
                    fallbackRemaining = unicodeFallback;
                    break;
                case "uc":
                case "u":
                    throw new InvalidDataException(RtfStrings.InvalidDocument);
                case "par":
                case "sect":
                case "page":
                    FlushBytes(pendingBytes, current);
                    EndParagraph(current, paragraphs);
                    break;
                case "line":
                    Emit('\n');
                    break;
                case "tab":
                    Emit('\t');
                    break;
                case "emdash":
                    Emit('\u2014');
                    break;
                case "endash":
                    Emit('\u2013');
                    break;
                case "lquote":
                    Emit('\u2018');
                    break;
                case "rquote":
                    Emit('\u2019');
                    break;
                case "ldblquote":
                    Emit('\u201c');
                    break;
                case "rdblquote":
                    Emit('\u201d');
                    break;
            }
        }

        if (groups.Count != 0)
        {
            throw new InvalidDataException(RtfStrings.InvalidDocument);
        }

        FlushBytes(pendingBytes, current);
        EndParagraph(current, paragraphs);
        return paragraphs;

        bool ConsumeFallback()
        {
            if (fallbackRemaining == 0)
            {
                return false;
            }

            fallbackRemaining--;
            return true;
        }

        void Emit(char value)
        {
            if (!skip && !ConsumeFallback())
            {
                FlushBytes(pendingBytes, current);
                current.Append(value);
            }
        }
    }

    private static void EndParagraph(StringBuilder current, List<string> paragraphs)
    {
        if (current.Length > 0)
        {
            paragraphs.Add(current.ToString());
        }

        current.Clear();
    }

    private static void FlushBytes(List<byte> pendingBytes, StringBuilder target)
    {
        if (pendingBytes.Count == 0)
        {
            return;
        }

        try
        {
            target.Append(Gb18030.Value.GetString(pendingBytes.ToArray()));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(RtfStrings.InvalidByteSequence, exception);
        }

        pendingBytes.Clear();
    }

    private static (string Word, int? Parameter, int EndIndex) ReadControlWord(string rtf, int start)
    {
        var position = start;
        while (position < rtf.Length && char.IsAsciiLetter(rtf[position]))
        {
            position++;
        }

        var word = rtf[start..position];
        var parameterStart = position;
        if (position < rtf.Length && rtf[position] == '-')
        {
            position++;
        }

        var digitStart = position;
        while (position < rtf.Length && char.IsAsciiDigit(rtf[position]))
        {
            position++;
        }

        int? parameter = null;
        if (position > digitStart)
        {
            if (!int.TryParse(rtf.AsSpan(parameterStart, position - parameterStart),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                throw new InvalidDataException(RtfStrings.InvalidDocument);
            }

            parameter = parsed;
        }

        if (position < rtf.Length && rtf[position] == ' ')
        {
            position++;
        }

        return (word, parameter, position - 1);
    }
}
