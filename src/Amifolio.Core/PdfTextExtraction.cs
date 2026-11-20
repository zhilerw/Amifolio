using System.IO.Compression;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace Amifolio.Core;

internal static class PdfTextExtraction
{
    private const long MaxInputBytes = 16L * 1024 * 1024;
    private const int MaxPages = 256;
    private const int MaxDecodedStreamBytes = 4 * 1024 * 1024;
    private const int MaxDecodedBytes = 16 * 1024 * 1024;

    internal static Extraction Extract(string path, CancellationToken token)
    {
        // The production caller is the existing isolated, timed 256 MiB worker.
        // Pass the verified seekable read handle to PdfPig: no staging file or full-file byte copy.
        using var stream = ArchiveSafety.OpenRead(path);
        if (stream.Length > MaxInputBytes) throw new TextExtraction.ExtractionLimitException("PDF 输入超过 16 MiB 上限");
        try
        {
            using var document = PdfDocument.Open(stream, new ParsingOptions
            {
                UseLenientParsing = false,
                SkipMissingFonts = false,
                MaxStackDepth = 64,
                FilterProvider = new BoundedFilterProvider(token)
            });
            // Also reject owner-encrypted files that open with the default empty password.
            // This feature has no password intake, password guessing or permission bypass.
            if (document.IsEncrypted) return Encrypted();
            if (document.NumberOfPages > MaxPages) throw new TextExtraction.ExtractionLimitException("PDF 超过 256 页上限");
            var output = new TextExtraction.PreviewBuilder(token);
            var emptyPages = 0;
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages && !output.Truncated; pageNumber++)
            {
                token.ThrowIfCancellationRequested();
                var page = document.GetPage(pageNumber);
                if (page.Letters.Count > 100_000) throw new TextExtraction.ExtractionLimitException("PDF 单页超过 100000 字形上限");
                if (page.Letters.Any(letter => letter.Value.Any(c => char.IsControl(c) || c == '\uFFFD')))
                    return new("PDF", "不支持提取：PDF 字符映射无效或缺失；无法可靠恢复文本", "");
                var text = ContentOrderTextExtractor.GetText(page);
                if (string.IsNullOrWhiteSpace(text)) { emptyPages++; continue; }
                output.Add($"[页 {pageNumber}] {text}");
            }
            if (output.Length == 0) return new("PDF", "不支持提取：无可提取文本；可能为空白、图片或扫描内容，需 OCR", "");
            var result = output.Result("PDF");
            return emptyPages == 0 ? result : result with { Status = result.Status + $"；{emptyPages} 页无可提取文本，图片或扫描内容需 OCR" };
        }
        catch (PdfDocumentEncryptedException) { return Encrypted(); }
        catch (NotSupportedException) { return new("PDF", "不支持提取：PDF 使用当前受限解析器不支持的结构或压缩方式", ""); }
        catch (Exception ex) when (ex is PdfDocumentFormatException or InvalidDataException or InvalidOperationException or ArgumentException or IOException or IndexOutOfRangeException or KeyNotFoundException or FormatException or OverflowException)
        { return new("PDF", "提取失败：PDF 损坏、缺少字体或结构无效", ""); }
    }

    private static Extraction Encrypted() => new("PDF", "不支持提取：加密 PDF；未提供密码或解密功能", "");

    // Only the text/font/object-stream codecs needed for this bounded text reader.
    // Do not decode images, execute actions/JavaScript, launch readers, or follow links.
    private sealed class BoundedFilterProvider : BaseFilterProvider
    {
        internal BoundedFilterProvider(CancellationToken token) : base(CreateFilters(token)) { }

        private static Dictionary<string, IFilter> CreateFilters(CancellationToken token)
        {
            var budget = new DecodeBudget(token);
            var flate = new BoundedFilter(null, budget);
            var ascii85 = new BoundedFilter(new Ascii85Filter(), budget);
            var asciiHex = new BoundedFilter(new AsciiHexDecodeFilter(), budget);
            return new(StringComparer.Ordinal)
            {
                ["FlateDecode"] = flate, ["Fl"] = flate,
                ["ASCII85Decode"] = ascii85, ["A85"] = ascii85,
                ["ASCIIHexDecode"] = asciiHex, ["AHx"] = asciiHex
            };
        }
    }

    private sealed class DecodeBudget(CancellationToken token)
    {
        private long decoded;
        internal void Add(int bytes)
        {
            token.ThrowIfCancellationRequested();
            decoded += bytes;
            if (decoded > MaxDecodedBytes) throw new TextExtraction.ExtractionLimitException("PDF 累计解码超过 16 MiB 上限");
        }
    }

    private sealed class BoundedFilter(IFilter? inner, DecodeBudget budget) : IFilter
    {
        public bool IsSupported => true;
        public Memory<byte> Decode(Memory<byte> input, DictionaryToken dictionary, IFilterProvider provider, int index)
        {
            budget.Add(0);
            if (input.Length > MaxDecodedStreamBytes) throw new TextExtraction.ExtractionLimitException("PDF 编码流超过 4 MiB 上限");
            if (inner is not null)
            {
                var decoded = inner.Decode(input, dictionary, provider, index);
                if (decoded.Length > MaxDecodedStreamBytes) throw new TextExtraction.ExtractionLimitException("PDF 解码流超过 4 MiB 上限");
                budget.Add(decoded.Length);
                return decoded;
            }
            // Predictors are image-oriented and can allocate from attacker-provided dimensions.
            // Reject them rather than letting the general-purpose codec allocate without bounds.
            if (dictionary.Data.ContainsKey(NameToken.DecodeParms) || dictionary.Data.ContainsKey(NameToken.Dp))
                throw new NotSupportedException("PDF decode parameters are outside bounded text extraction.");
            using var encoded = new MemoryStream(input.ToArray(), writable: false);
            using var decoder = new ZLibStream(encoded, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var block = new byte[8192];
            int read;
            while ((read = decoder.Read(block)) != 0)
            {
                budget.Add(read);
                if (output.Length + read > MaxDecodedStreamBytes) throw new TextExtraction.ExtractionLimitException("PDF 解码流超过 4 MiB 上限");
                output.Write(block, 0, read);
            }
            return output.ToArray();
        }
    }
}
