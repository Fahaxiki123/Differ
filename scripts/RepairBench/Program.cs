using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Differ.App.ViewModels;
using Differ.Core.Abstractions;
using Differ.Core.Models;
using Differ.Formats.Excel;
using Differ.Formats.Text;
using Differ.Formats.Word;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

var data = Path.GetFullPath(args[0]);
Directory.CreateDirectory(data);
var output = args[1];
var measurements = new List<object>();
var cases = new[] {
    ("excel-small", "xlsx", 1000, 30), ("excel-medium", "xlsx", 10000, 40),
    ("excel-large", "xlsx", 30000, 30), ("excel-insert", "xlsx", 1000, 30),
    ("excel-multi", "xlsx", 1000, 30),
    ("word-small", "docx", 100, 0), ("word-medium", "docx", 1000, 0),
    ("word-large", "docx", 5000, 0), ("word-rewrite", "docx", 100, 0),
    ("code-small", "lua", 1000, 0), ("code-medium", "lua", 10000, 0),
    ("code-large", "lua", 50000, 0), ("code-long", "lua", 1000, 0) };
foreach (var (name, extension, count, columns) in cases)
{
    if (args.Length > 2 && !name.Contains(args[2], StringComparison.Ordinal)) continue;
    var oldPath = Path.Combine(data, name + ".base." + extension);
    var newPath = Path.Combine(data, name + ".mine." + extension);
    if (!File.Exists(oldPath)) Create(oldPath, name, extension, count, columns, false);
    if (!File.Exists(newPath)) Create(newPath, name, extension, count, columns, true);
    var adapter = extension switch { "xlsx" => (IFileDiffAdapter)new ExcelDiffAdapter(),
        "docx" => new WordDiffAdapter(), _ => new TextDiffAdapter(new TextFileReader()) };
    var request = new DiffRequest { RelativePath = name + "." + extension,
        BaseFilePath = oldPath, CurrentFilePath = newPath };
    for (var run = 0; run < 4; run++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        DiffDocument document;
#if DOCUMENT_EDITOR
        if (extension == "lua")
        {
            var session = await EditableTextSession.OpenAsync(oldPath, newPath, null, null, CancellationToken.None);
            var comparison = EditorDiffEngine.Compare(session!.CaptureText(), request.Options, CancellationToken.None);
            document = EditableTextSession.CreateDocument(comparison, request.RelativePath, null);
        }
        else
#endif
            document = await adapter.CompareAsync(request, CancellationToken.None);
        var adapterMs = timer.Elapsed.TotalMilliseconds;
        var adapterBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        allocated = GC.GetTotalAllocatedBytes(true); timer.Restart();
        var presentation = ComparisonPresentationBuilder.Build(document, CancellationToken.None);
        var presentationMs = timer.Elapsed.TotalMilliseconds;
        var row = new { name, count, columns, run, mode = run == 0 ? "first-call" : "warm",
            oldBytes = new FileInfo(oldPath).Length, newBytes = new FileInfo(newPath).Length,
            oldSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oldPath))),
            newSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(newPath))),
            adapterMs, adapterAllocatedBytes = adapterBytes, presentationMs,
            presentationAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            gc = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(),
            peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
            nodes = document.Nodes.Count, changed = document.Summary.Changed,
            displayRows = presentation.AllRows.Count, warnings = document.Warnings.Count };
        measurements.Add(row);
        File.WriteAllText(output, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{name} {run}: adapter={adapterMs:F1}ms presentation={presentationMs:F1}ms changed={document.Summary.Changed}");
    }
}

static void Create(string path, string name, string extension, int count, int columns, bool modified)
{
    if (extension == "lua")
    {
        var lines = Enumerable.Range(1, count).Select(i => $"local item_{i} = {{ id = {i}, name = \"配置条目{i}\", value = {(modified && i % 997 == 0 ? 5 : 3)} }}").ToArray();
        if (name == "code-long") lines[500] = "local long = \"" + new string('a', 10000) + (modified ? "变" : "原") + new string('b', 30000) + (modified ? "5" : "3") + new string('c', 10000) + "\"";
        File.WriteAllText(path, string.Join("\r\n", lines), new UTF8Encoding(false)); return;
    }
    if (extension == "docx")
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var part = doc.AddMainDocumentPart(); var body = new W.Body();
        for (var i = 1; i <= count; i++)
        {
            if (name == "word-rewrite" && modified && i is > 20 and <= 40)
            { if (i <= 25) body.Append(new W.Paragraph(new W.Run(new W.Text($"全新规则{i}：此区间重新编写，后续标题保持对齐。")))); continue; }
            body.Append(new W.Paragraph(new W.Run(new W.Text($"第{i}条配置规则：每日可刷新{(modified && i % 17 == 0 ? 5 : 3)}次，购买次数与等级相关，完成任务可获得奖励。"))));
        }
        part.Document = new W.Document(body); part.Document.Save(); return;
    }
    using var workbook = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
    var wp = workbook.AddWorkbookPart(); wp.Workbook = new S.Workbook(); var sheets = wp.Workbook.AppendChild(new S.Sheets());
    var inserted = modified && name == "excel-insert";
    for (var sheet = 1; sheet <= (name == "excel-multi" ? 5 : 1); sheet++)
    {
        var part = wp.AddNewPart<WorksheetPart>(); var data = new S.SheetData();
        for (var r = 1; r <= count + 1; r++)
        {
            var row = new S.Row { RowIndex = (uint)r };
            for (var c = 1; c <= columns + (inserted ? 1 : 0); c++)
            {
                var logical = inserted && c > 8 ? c - 1 : c;
                var value = inserted && c == 8 ? (r == 1 ? "new_field" : "新增") : r == 1 ? $"field_{logical}" : $"{r}:{logical}:{sheet}";
                if (modified && r % 997 == 0 && logical == 5) value += "修改";
                row.Append(new S.Cell { CellReference = Letter(c) + r.ToString(CultureInfo.InvariantCulture), DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(value)) });
            }
            data.Append(row);
        }
        part.Worksheet = new S.Worksheet(data);
        sheets.Append(new S.Sheet { Id = wp.GetIdOfPart(part), SheetId = (uint)sheet, Name = "配置" + sheet });
    }
    wp.Workbook.Save();
}
static string Letter(int index) { var result = ""; for (; index > 0; index = (index - 1) / 26) result = (char)('A' + (index - 1) % 26) + result; return result; }

