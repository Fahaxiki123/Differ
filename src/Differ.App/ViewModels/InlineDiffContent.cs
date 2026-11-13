using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed record InlineDiffContent(DiffKind Kind, IReadOnlyList<InlineDiffSegment> Segments);
