using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using Differ.App.Resources;
using Differ.App.ViewModels;
using Differ.Core.Models;

namespace Differ.App.Services;

public static class ComparisonExportService
{
    private static readonly CompositeFormat SummaryFormat = CompositeFormat.Parse(Strings.ExportSummaryFormat);

    public static Task ExportHtmlAsync(DiffDocument document, string title, string path,
        IReadOnlyList<string> protectedPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync(path, protectedPaths, (writer, token) =>
        {
            writer.Write("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            writer.Write("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>");
            Encode(writer, title);
            writer.Write("</title><style>body{font-family:system-ui,sans-serif;margin:2rem}table{border-collapse:collapse;width:100%;table-layout:fixed}th,td{border:1px solid;padding:.5rem;text-align:left;vertical-align:top}pre{white-space:pre-wrap;overflow-wrap:anywhere;margin:0}</style></head><body><h1>");
            Encode(writer, title);
            writer.Write("</h1><p>");
            Encode(writer, string.Format(CultureInfo.CurrentCulture, SummaryFormat,
                document.Summary.Added, document.Summary.Deleted, document.Summary.Modified,
                document.Summary.Moved, document.Summary.Conflicts + document.Summary.Unsupported));
            writer.Write("</p>");
            foreach (var warning in document.Warnings)
            {
                token.ThrowIfCancellationRequested();
                writer.Write("<p>"); Encode(writer, warning); writer.Write("</p>");
            }
            writer.Write("<table><thead><tr><th>"); Encode(writer, Strings.ExportLocation);
            writer.Write("</th><th>"); Encode(writer, Strings.ExportOldValue);
            writer.Write("</th><th>"); Encode(writer, Strings.ExportNewValue);
            writer.Write("</th></tr></thead><tbody>");
            var pending = new Stack<DiffNode>(document.Nodes.Reverse());
            while (pending.TryPop(out var node))
            {
                token.ThrowIfCancellationRequested();
                foreach (var child in node.Children.Reverse()) pending.Push(child);
                if (node.Kind == DiffKind.Unchanged) continue;
                writer.Write("<tr><td>"); Encode(writer, node.Path);
                writer.Write("<br>"); Encode(writer, DiffRowViewModel.Localize(node.Kind));
                writer.Write("</td><td><pre>"); Encode(writer, node.OldValue);
                writer.Write("</pre></td><td><pre>"); Encode(writer, node.NewValue);
                writer.Write("</pre></td></tr>");
            }
            writer.Write("</tbody></table>");
            if (document.FileKind == FileKind.Excel)
            {
                // Structural events have compact navigation nodes; their full
                // values still come from the same mapped grid used for review.
                foreach (var section in document.TabularSections)
                {
                    writer.Write("<h2>"); Encode(writer, section.Key); writer.Write("</h2><p>");
                    Encode(writer, section.MappingDescription); writer.Write("</p><table><tbody>");
                    foreach (var row in section.Rows)
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var cell in row.Cells)
                        {
                            if (cell.Kind == DiffKind.Unchanged && !cell.IsStructuralReferenceAdjustment) continue;
                            writer.Write("<tr><td>"); Encode(writer, $"{cell.OldAddress ?? "—"} → {cell.NewAddress ?? "—"}");
                            writer.Write("</td><td><pre>"); Encode(writer, cell.OldDetails ?? cell.OldValue);
                            writer.Write("</pre></td><td><pre>"); Encode(writer, cell.NewDetails ?? cell.NewValue);
                            writer.Write("</pre></td></tr>");
                        }
                    }
                    writer.Write("</tbody></table>");
                }
            }
            writer.Write("</body></html>");
        }, cancellationToken);
    }

    public static Task ExportTextAsync(string location, string oldText, string newText, string path,
        IReadOnlyList<string> protectedPaths, CancellationToken cancellationToken = default)
    {
        return WriteAsync(path, protectedPaths, (writer, token) =>
        {
            token.ThrowIfCancellationRequested();
            writer.WriteLine(location);
            writer.WriteLine(Strings.BaseSide);
            writer.WriteLine(oldText);
            writer.WriteLine(Strings.MineSide);
            writer.WriteLine(newText);
        }, cancellationToken);
    }

    private static Task WriteAsync(string path, IReadOnlyList<string> protectedPaths,
        Action<StreamWriter, CancellationToken> render, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(path);
            var canonicalDestination = ResolveLinks(destination);
            if (protectedPaths.Any(source => string.Equals(ResolveLinks(source), canonicalDestination,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException(Strings.ExportProtectedPath);
            }

            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".differ-export-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    render(writer, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Replacing the directory entry avoids truncating a different
                // source path that happens to be a hard link to this target.
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellationToken);
    }

    private static void Encode(TextWriter writer, string? value) => HtmlEncoder.Default.Encode(writer, value ?? string.Empty);

    private static string ResolveLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        var segments = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileSystemInfo info = index == segments.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
            if (info.LinkTarget is not null)
            {
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException(Strings.ExportProtectedPath);
            }
        }
        return Path.GetFullPath(current);
    }
}
