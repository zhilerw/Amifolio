using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Amifolio.Core;

internal sealed record Extraction(string Kind, string Status, string Preview);

internal static class TextExtraction
{
    private const long MaxXmlBytes = 8L * 1024 * 1024;
    private const long MaxExpandedBytes = 32L * 1024 * 1024;
    private const int MaxZipEntries = 2048;
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".csx", ".fs", ".vb", ".cpp", ".c", ".h", ".hpp", ".java", ".kt", ".py", ".pyi", ".js", ".jsx", ".ts", ".tsx", ".go", ".rs", ".rb", ".php", ".swift", ".sql", ".sh", ".ps1", ".bat", ".cmd", ".html", ".css", ".scss", ".vue", ".svelte", ".json", ".xml", ".xaml", ".yaml", ".yml", ".toml", ".ini", ".config", ".csproj", ".props", ".targets", ".sln", ".slnx", ".r", ".dart", ".lua", ".lock" };

    internal static Task<Extraction> ExtractAsync(string path, string originalName, CancellationToken token) => Task.Run(() => Extract(path, originalName, token), token);

    private static Extraction Extract(string path, string name, CancellationToken token)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant(); var kind = Classify(extension);
        try
        {
            token.ThrowIfCancellationRequested();
            if (extension is ".docx" or ".xlsx" or ".pptx") return Office(path, extension, kind, token);
            if (kind is "代码" or "笔记" or "文本" || extension is ".csv" or ".tsv" || name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
                return Text(path, kind, token);
            return new(kind, "不支持提取：" + (extension == ".pdf" ? "PDF 文本与扫描件尚未接入解析/OCR" : "此格式仅保存原件"), "");
        }
        catch (OperationCanceledException) { throw; }
        catch (ExtractionLimitException ex) { return new(kind, "提取受限：" + ex.Message, ""); }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or DecoderFallbackException or FormatException or OverflowException)
        { return new(kind, "提取失败：文件损坏、编码无效或不安全的文档结构", ""); }
    }

    private static string Classify(string ext) => ext switch
    {
        ".docx" or ".doc" or ".odt" or ".rtf" => "文档",
        ".xlsx" or ".xls" or ".ods" or ".csv" or ".tsv" => "表格",
        ".pptx" or ".ppt" or ".odp" => "演示",
        ".pdf" => "PDF", ".md" or ".markdown" => "笔记", ".txt" or ".log" => "文本",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tiff" or ".svg" => "图片",
        _ => CodeExtensions.Contains(ext) ? "代码" : "其他"
    };

    private static Extraction Text(string path, string kind, CancellationToken token)
    {
        using var input = ArchiveSafety.OpenRead(path);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, bufferSize: 8192);
        // Read at most preview length + one char; never ReadLine on an unbounded input line.
        var chars = new char[ArchiveSafety.PreviewChars + 1]; var count = 0;
        while (count < chars.Length)
        {
            token.ThrowIfCancellationRequested(); var read = reader.Read(chars, count, Math.Min(8192, chars.Length - count));
            if (read == 0) break; count += read;
        }
        var text = new string(chars, 0, Math.Min(count, ArchiveSafety.PreviewChars));
        if (text.Any(c => c == '\0' || (char.IsControl(c) && c is not '\t' and not '\r' and not '\n' and not '\f')))
            return new(kind, "不支持提取：文件包含二进制控制字符", "");
        var output = new PreviewBuilder(token);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length && !output.Truncated; i++) output.Add($"[行 {i + 1}] {lines[i].TrimEnd('\r')}");
        if (count > ArchiveSafety.PreviewChars) output.Truncated = true;
        return output.Result(kind);
    }

    private static Extraction Office(string path, string ext, string kind, CancellationToken token)
    {
        using var stream = ArchiveSafety.OpenRead(path); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxZipEntries) throw new ExtractionLimitException("Office ZIP 超过 2048 条目上限");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var e in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (e.FullName.StartsWith('/') || e.FullName.Contains('\\') || e.FullName.Contains(':') || e.FullName.Split('/').Any(x => x is "." or "..") ||
                ((e.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !entries.TryAdd(e.FullName, e))
                throw new InvalidDataException("Unsafe ZIP entry");
            total = checked(total + e.Length);
            if (total > MaxExpandedBytes || e.Length > MaxXmlBytes || (e.Length > 1024 * 1024 && e.Length / Math.Max(e.CompressedLength, 1) > 200))
                throw new ExtractionLimitException("Office ZIP 条目 8 MiB、总展开 32 MiB 或压缩比 200:1 上限");
        }
        var output = new PreviewBuilder(token);
        long processedXmlBytes = 0; var processedNodes = 0;
        XDocument Load(string entryName)
        {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(entryName, out var entry)) throw new InvalidDataException("Missing Office part");
            using var source = entry.Open(); using var buffer = new MemoryStream();
            var block = new byte[16 * 1024]; long copied = 0; int read;
            while ((read = source.Read(block)) != 0)
            {
                token.ThrowIfCancellationRequested(); copied += read; processedXmlBytes += read;
                if (copied > MaxXmlBytes || processedXmlBytes > MaxExpandedBytes) throw new ExtractionLimitException("XML 单条目 8 MiB 或累计处理 32 MiB 上限");
                buffer.Write(block, 0, read);
            }
            buffer.Position = 0;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes, IgnoreComments = true };
            // Bound nesting independently of expanded size, then load from this same bounded buffer.
            var partNodes = 0;
            using (var check = XmlReader.Create(buffer, settings))
            {
                while (check.Read())
                {
                    token.ThrowIfCancellationRequested(); partNodes++; processedNodes++;
                    if (check.Depth > 128 || partNodes > 100_000 || processedNodes > 250_000)
                        throw new ExtractionLimitException("XML 超过 128 层、单条目 100000 节点或累计 250000 节点上限");
                }
            }
            buffer.Position = 0; using var xml = XmlReader.Create(buffer, settings); return XDocument.Load(xml);
        }
        Dictionary<string, string> Relationships(string part, string prefix, string permitted)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!entries.ContainsKey(part)) return map;
            foreach (var rel in Load(part).Descendants(Rel + "Relationship"))
            {
                token.ThrowIfCancellationRequested();
                var id = (string?)rel.Attribute("Id"); var target = (string?)rel.Attribute("Target");
                if (id is null || target is null || (string?)rel.Attribute("TargetMode") == "External") continue;
                var normalized = target.StartsWith('/') ? target.TrimStart('/') : prefix + target;
                // Relationships are ZIP part lookups only. Never open file://, HTTP, or filesystem targets.
                if (normalized.StartsWith(permitted, StringComparison.Ordinal) && !normalized.Contains('\\') && !normalized.Contains(':') && !normalized.Split('/').Any(x => x is "." or ".."))
                    map.TryAdd(id, normalized);
            }
            return map;
        }
        if (ext == ".docx")
        {
            var doc = Load("word/document.xml");
            if (doc.Root?.Name != W + "document") throw new InvalidDataException("Missing Word document root");
            var i = 0;
            foreach (var paragraph in doc.Descendants(W + "p"))
            {
                if (output.Truncated) break;
                var text = string.Concat(paragraph.Descendants(W + "t").Select(x => x.Value));
                output.Add($"[段落 {++i}{(paragraph.Ancestors(W + "tc").Any() ? "，表格单元格" : "")}] {text}");
            }
        }
        else if (ext == ".xlsx")
        {
            var shared = entries.ContainsKey("xl/sharedStrings.xml") ? Load("xl/sharedStrings.xml").Descendants(S + "si").Select(x => string.Concat(x.Descendants(S + "t").Select(t => t.Value))).ToArray() : [];
            var workbook = Load("xl/workbook.xml");
            var relations = Relationships("xl/_rels/workbook.xml.rels", "xl/", "xl/worksheets/");
            foreach (var sheet in workbook.Descendants(S + "sheet"))
            {
                token.ThrowIfCancellationRequested(); if (output.Truncated) break;
                var name = (string?)sheet.Attribute("name") ?? "工作表"; var id = (string?)sheet.Attribute(R + "id");
                if (id is null || !relations.TryGetValue(id, out var target)) throw new InvalidDataException("Missing worksheet relationship");
                var worksheet = Load(target);
                foreach (var cell in worksheet.Descendants(S + "c"))
                {
                    if (output.Truncated) break;
                    var value = cell.Element(S + "v")?.Value ?? "";
                    if ((string?)cell.Attribute("t") == "s")
                    {
                        if (!int.TryParse(value, out var index) || index < 0 || index >= shared.Length) throw new InvalidDataException("Bad shared string");
                        value = shared[index];
                    }
                    else if ((string?)cell.Attribute("t") == "inlineStr") value = string.Concat(cell.Descendants(S + "t").Select(t => t.Value));
                    if (cell.Element(S + "f") is { } formula) value = "=" + formula.Value + "（仅缓存值：" + value + "）";
                    output.Add($"[工作表 {name}，单元格 {(string?)cell.Attribute("r") ?? "未知"}] {value}");
                }
            }
        }
        else
        {
            var presentation = Load("ppt/presentation.xml");
            var relations = Relationships("ppt/_rels/presentation.xml.rels", "ppt/", "ppt/slides/"); var slideNumber = 0;
            foreach (var slide in presentation.Descendants(P + "sldId"))
            {
                token.ThrowIfCancellationRequested(); if (output.Truncated) break;
                var id = (string?)slide.Attribute(R + "id");
                if (id is null || !relations.TryGetValue(id, out var target)) throw new InvalidDataException("Missing slide relationship");
                slideNumber++; var slideDoc = Load(target); var paragraphNumber = 0;
                foreach (var paragraph in slideDoc.Descendants(A + "p"))
                {
                    if (output.Truncated) break;
                    output.Add($"[幻灯片 {slideNumber}，段落 {++paragraphNumber}] {string.Concat(paragraph.Descendants(A + "t").Select(x => x.Value))}");
                }
            }
        }
        if (output.Length == 0) return new(kind, "不支持提取：无可提取文本；图片或扫描内容需 OCR", "");
        return output.Result(kind);
    }

    private sealed class ExtractionLimitException(string message) : Exception(message);
    private sealed class PreviewBuilder(CancellationToken token)
    {
        private readonly StringBuilder builder = new();
        internal bool Truncated { get; set; }
        internal int Length => builder.Length;
        internal void Add(string text)
        {
            token.ThrowIfCancellationRequested(); var available = ArchiveSafety.PreviewChars - builder.Length;
            if (text.Length + 1 > available) { builder.Append(text.AsSpan(0, Math.Max(0, available))); Truncated = true; }
            else builder.Append(text).Append('\n');
        }
        internal Extraction Result(string kind) => new(kind, Truncated ? "已提取（截断）" : "已提取", builder.ToString());
    }
}
