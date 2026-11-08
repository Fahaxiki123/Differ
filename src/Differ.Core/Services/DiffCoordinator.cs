using System.Xml;
using Differ.Core.Abstractions;
using Differ.Core.Models;
using Differ.Core.Resources;
using Differ.Core.Security;

namespace Differ.Core.Services;

public sealed class DiffCoordinator(IEnumerable<IFileDiffAdapter> adapters)
{
    private readonly IReadOnlyList<IFileDiffAdapter> _adapters = adapters.ToArray();

    public async Task<DiffDocument> CompareAsync(
        DiffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var typePaths = new[]
        {
            request.RelativePath,
            request.CurrentFilePath,
            request.BaseFilePath
        }.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!);
        var adapter = _adapters.FirstOrDefault(
            candidate => typePaths.Any(candidate.CanHandle));

        if (adapter is null)
        {
            var node = new DiffNode
            {
                StableId = $"unsupported:{request.RelativePath}",
                Path = request.RelativePath,
                Kind = DiffKind.Unsupported,
                OldValue = request.BaseFilePath,
                NewValue = request.CurrentFilePath,
                Metadata = new Dictionary<string, string>
                {
                    ["reason"] = CoreStrings.CoordinatorUnsupportedReason
                }
            };
            var nodes = new[] { node };
            return new DiffDocument
            {
                RelativePath = request.RelativePath,
                FileKind = FileKind.Unsupported,
                BaseRevision = request.BaseRevision,
                Nodes = nodes,
                Summary = DiffSummary.FromNodes(nodes),
                Warnings = [CoreStrings.CoordinatorUnsupportedWarning]
            };
        }

        var binaryFallback = _adapters.LastOrDefault(
            candidate => candidate.FileKind == FileKind.Binary);
        DiffDocument document;
        try
        {
            document = await adapter
                .CompareAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            binaryFallback is not null
            && !ReferenceEquals(binaryFallback, adapter)
            && IsStructuralParseFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fallback = await binaryFallback
                .CompareAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return fallback with
            {
                Warnings = [CoreStrings.CoordinatorParseFallbackWarning, .. fallback.Warnings]
            };
        }

        // Content-based binary detection (for example an unknown extension
        // whose bytes are not text) must fall back to the byte-level adapter
        // instead of surfacing a "binary not supported" refusal.
        if (document.FileKind == FileKind.Binary
            && binaryFallback is not null
            && !ReferenceEquals(binaryFallback, adapter))
        {
            var fallback = await binaryFallback
                .CompareAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return fallback with
            {
                Warnings = document.Warnings.Concat(fallback.Warnings)
                    .Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        return document;
    }

    private static bool IsStructuralParseFailure(Exception exception)
    {
        if (exception is not (StructuralParseException
            or InvalidDataException or XmlException or FormatException))
        {
            return false;
        }

        // SDK-specific failures are normalized by adapters. Even when an
        // SDK wraps a failure, access, cancellation and safety limits retain
        // their original meaning rather than launching another parser.
        var knownArchiveFailure = exception is StructuralParseException
        {
            IsArchiveStructureFailure: true
        };
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException
                or UnauthorizedAccessException
                or ComparisonResourceLimitException
                or FileNotFoundException
                or DirectoryNotFoundException
                || current is IOException and not StructuralParseException
                    && !IsKnownOfficeFormatIOException(current)
                    && !(knownArchiveFailure && current.GetType() == typeof(IOException)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsKnownOfficeFormatIOException(Exception exception)
    {
        // These SDK exceptions derive from IOException but describe invalid
        // document signatures, not access failures. Adapters normalize them
        // explicitly; keep the core independent of the optional SDK package.
        return exception.GetType().FullName is
            "NPOI.POIFS.FileSystem.NotOLE2FileException"
            or "NPOI.POIFS.FileSystem.OfficeXmlFileException";
    }
}
