namespace Differ.Core.Algorithms;

public static class TextSimilarity
{
    public static double Jaccard(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1;
        }

        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string? value)
    {
        // The unit separators used by row and sheet signatures must split
        // tokens too, otherwise similarity spans logical boundaries and
        // weakens replacement/rename matching thresholds.
        return (value ?? string.Empty)
            .Split(
                [' ', '\t', '\r', '\n', '\u001e', '\u001f', ',', '.', ';', ':', '，', '。', '；', '：', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }
}
