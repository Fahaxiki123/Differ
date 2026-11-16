namespace Differ.Core.Algorithms;

/// <summary>
/// Centralized guardrails for algorithms whose exact diff matrix is quadratic.
/// Keep these values together so large-file behavior can be benchmarked and
/// adjusted without scattering thresholds across format adapters.
/// </summary>
public static class DiffPerformanceOptions
{
    public const long MaximumSequenceMatrixCells = 4_000_000;

    public const long MaximumInlineMatrixCells = 250_000;

    public const int MaximumMeasuredTextUnits = 48;

    public const int MaximumWordLocalMatrixCells = 16_384;

    public const int MaximumWordSimilarityCandidates = 100_000;

    public const int MaximumWordSimilarityTextSamples = 2_048;

    public const int MaximumWordGroupPreviewParagraphs = 3;

    public const int MaximumWordGroupWindowParagraphs = 24;

    public const int MaximumInlineAnchorComparisons = 500_000;

    public const int MaximumEditorDiffRegionCharacters = 1_000_000;

    public const int MaximumEditorDetailedLineCharacters = 100_000;

    // Text views materialize the whole file as a decoded string, so the
    // readable size ceiling is a centrally configured threshold instead of
    // an inline int.MaxValue check.
    public const long MaximumTextFileBytes = 64L * 1024 * 1024;

    public const int MaximumTextCharacters = 64 * 1024 * 1024;

    public const int MaximumTextLines = 1_000_000;

    public const int MaximumMergeHistoryCharacters = 8_000_000;

    public const int MaximumMergeHistoryEntries = 100;

    public const int MaximumTabularRows = 100_000;

    public const int MaximumTabularColumns = 16_384;

    // This budget covers all expanded sheets in one comparison, before
    // the UI materializes its left/right cell presentation objects.
    public const long MaximumTabularCells = 1_000_000;

    // Binary comparison materializes hex lines for both sides plus the
    // alignment key set, so the ceiling is below the text one. Keep it
    // here so large-file behavior can be benchmarked and adjusted centrally.
    public const long MaximumBinaryFileBytes = 8L * 1024 * 1024;

    public const int HexLineBytes = 16;
}
