using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Amifolio.Core;

internal static class FormatCases
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    internal static readonly (string Name, Func<Fixture, Task> Run)[] All =
    [
        ("format5 PDF Chinese ToUnicode compressed multipage keeps physical page locators and original", async f =>
        {
            var path = Pdf(f, "中文.pdf", [TextPage("01020304"), TextPage("05060708")], compress: true);
            var result = await ReadOnly(path);
            Check(result.Status == "已提取", result.Status);
            Check(result.Preview.Contains("[页 1]") && result.Preview.Contains("中文第一"), result.Preview);
            Check(result.Preview.Contains("[页 2]") && result.Preview.Contains("第二页文"), result.Preview);
            Check(result.Preview.IndexOf("中文第一", StringComparison.Ordinal) < result.Preview.IndexOf("第二页文", StringComparison.Ordinal), "PDF page order changed");
        }),
        ("format5 PDF scan blank and mixed pages never fabricate OCR or completeness", async f =>
        {
            var scan = await ReadOnly(Pdf(f, "scan.pdf", [ImagePage], image: true));
            Check(scan.Preview.Length == 0 && scan.Status.Contains("OCR") && !scan.Status.StartsWith("已提取"), scan.Status);
            var blank = await ReadOnly(Pdf(f, "blank.pdf", [""]));
            Check(blank.Preview.Length == 0 && blank.Status.Contains("无可提取文本") && !blank.Status.Contains("扫描件已识别"), blank.Status);
            var mixed = await ReadOnly(Pdf(f, "mixed.pdf", [ImagePage, TextPage("0102")], image: true));
            Check(mixed.Preview.Contains("[页 2]") && mixed.Preview.Contains("中文") && !mixed.Preview.Contains("[页 1]"), mixed.Preview);
            Check(mixed.Status.Contains("无可提取文本") && mixed.Status.Contains("OCR"), mixed.Status);
        }),
        ("format5 PDF encryption including empty password is explicitly unsupported", async f =>
        {
            foreach (var password in new[] { "fixture-secret", "" })
            {
                var path = Pdf(f, "encrypted-" + password.Length + ".pdf", [TextPage("0102")], password: password);
                // Prove this is actual readable encryption, rather than a forged /Encrypt marker.
                using (var document = UglyToad.PdfPig.PdfDocument.Open(path, new UglyToad.PdfPig.ParsingOptions { Password = password }))
                    Check(document.IsEncrypted && document.GetPage(1).Text == "中文", "encrypted fixture cannot round-trip with its password");
                var result = await ReadOnly(path);
                Check(result.Status.Contains("加密") && !result.Status.StartsWith("已提取") && result.Preview.Length == 0, result.Status);
            }
        }),
        ("format5 PDF damaged page and damaged header discard partial preview", async f =>
        {
            foreach (var path in new[] { f.File("broken.pdf", "%PDF-1.7\ntruncated"), Pdf(f, "bad-page.pdf", [TextPage("0102"), "BT /F1 12 Tf <01> Tj ET"], badSecondFont: true) })
            {
                var result = await ReadOnly(path);
                Check(result.Status.StartsWith("提取失败") && result.Status.Contains("损坏") && result.Preview.Length == 0, result.Status + result.Preview);
            }
        }),
        ("format5 PDF preview page input and decompression limits are explicit", async f =>
        {
            var manyText = string.Concat(Enumerable.Repeat(TextPage(new string('0', 256).Replace("00", "01")), 32));
            var preview = await ReadOnly(Pdf(f, "long.pdf", Enumerable.Repeat(manyText, 20).ToArray(), compress: true));
            Check(preview.Preview.Length <= ArchiveSafety.PreviewChars && preview.Status.Contains("截断"), preview.Status);
            var manyPages = await ReadOnly(Pdf(f, "pages.pdf", Enumerable.Repeat("", 257).ToArray()));
            Check(manyPages.Status.StartsWith("提取受限") && manyPages.Status.Contains("256"), manyPages.Status);
            var bomb = await ReadOnly(Pdf(f, "bomb.pdf", [new string(' ', 4 * 1024 * 1024 + 1)], compress: true));
            Check(bomb.Status.StartsWith("提取受限") && bomb.Preview.Length == 0, bomb.Status);
            var large = f.File("large.pdf", "%PDF-1.7\n");
            using (var stream = System.IO.File.OpenWrite(large)) stream.SetLength(16 * 1024 * 1024 + 1);
            var limited = await ReadOnly(large);
            Check(limited.Status.StartsWith("提取受限") && limited.Status.Contains("16 MiB"), limited.Status);
        }),
        ("format5 DOCX preserves paragraphs table cells tabs and breaks excluding deleted text", async f =>
        {
            var path = f.Zip("paragraph.docx", ("[Content_Types].xml", ContentTypes("word/document.xml", "wordprocessingml.document.main")),
                ("_rels/.rels", RootRel("word/document.xml")),
                ("word/document.xml", $"<w:document xmlns:w='{W}'><w:body><w:p><w:r><w:t>中文</w:t><w:tab/><w:t>列二</w:t><w:br/><w:t>下一行</w:t></w:r><w:del><w:r><w:delText>已删除</w:delText></w:r></w:del></w:p><w:p/><w:tbl><w:tr><w:tc><w:p><w:r><w:t>表格值</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>"));
            var result = await ReadOnly(path);
            Check(result.Status == "已提取" && result.Preview.Contains("[段落 1] 中文\t列二\n下一行") && result.Preview.Contains("[段落 3，表格单元格] 表格值") && !result.Preview.Contains("已删除"), result.Preview);
        }),
        ("format5 XLSX rich strings actual cells cached formulas and absent cache are truthful", async f =>
        {
            var path = Workbook(f, "cells.xlsx", $"<row r='3'><c r='A3' t='s'><v>0</v></c><c r='C3' t='inlineStr'><is><t>内联</t></is></c><c r='D3'><f>SUM(B1:B2)</f><v>42</v></c><c r='E3'><f>1+1</f></c><c r='F3' t='b'><v>1</v></c></row>");
            var result = await ReadOnly(path);
            Check(new[] { "单元格 A3] 共享中文", "单元格 C3] 内联", "单元格 D3] =SUM(B1:B2)（仅缓存值：42）", "单元格 E3] =1+1（无缓存值；未计算）", "单元格 F3] TRUE" }.All(result.Preview.Contains), result.Preview);
        }),
        ("format5 XLSX invalid shared string and forged cell address fail closed", async f =>
        {
            foreach (var cells in new[] { "<row><c r='A1' t='s'><v>9</v></c></row>", "<row><c r='A0'><v>9</v></c></row>" })
            {
                var result = await ReadOnly(Workbook(f, Guid.NewGuid() + ".xlsx", cells));
                Check(result.Status.StartsWith("提取失败") && result.Preview.Length == 0, result.Status + result.Preview);
            }
        }),
        ("format5 PPTX relationship order tabs breaks blank slide keep physical slide numbers", async f =>
        {
            var path = f.Zip("ordered.pptx", ("[Content_Types].xml", ContentTypes("ppt/presentation.xml", "presentationml.presentation.main")),
                ("_rels/.rels", RootRel("ppt/presentation.xml")),
                ("ppt/presentation.xml", $"<p:presentation xmlns:p='{P}' xmlns:r='{R}'><p:sldIdLst><p:sldId id='256' r:id='two'/><p:sldId id='257' r:id='blank'/><p:sldId id='258' r:id='one'/></p:sldIdLst></p:presentation>"),
                ("ppt/_rels/presentation.xml.rels", $"<Relationships xmlns='{Rel}'><Relationship Id='one' Target='slides/slide1.xml'/><Relationship Id='two' Target='slides/slide2.xml'/><Relationship Id='blank' Target='slides/slide3.xml'/></Relationships>"),
                ("ppt/slides/slide1.xml", $"<p:sld xmlns:p='{P}' xmlns:a='{A}'><p:cSld><p:spTree><a:p><a:r><a:t>末页</a:t></a:r></a:p></p:spTree></p:cSld></p:sld>"),
                ("ppt/slides/slide2.xml", $"<p:sld xmlns:p='{P}' xmlns:a='{A}'><p:cSld><p:spTree><a:p><a:r><a:t>首页</a:t></a:r><a:br/><a:r><a:t>下一行</a:t></a:r></a:p></p:spTree></p:cSld></p:sld>"),
                ("ppt/slides/slide3.xml", $"<p:sld xmlns:p='{P}'><p:cSld><p:spTree/></p:cSld></p:sld>"));
            var result = await ReadOnly(path);
            Check(result.Preview.Contains("[幻灯片 1，段落 1] 首页\n下一行") && result.Preview.Contains("[幻灯片 3，段落 1] 末页"), result.Preview);
        }),
        ("format5 Office empty paragraphs are not successful extraction and wrong roots fail", async f =>
        {
            var empty = await ReadOnly(f.Zip("empty.docx", ("word/document.xml", $"<w:document xmlns:w='{W}'><w:body><w:p/></w:body></w:document>")));
            Check(!empty.Status.StartsWith("已提取") && empty.Status.Contains("无可提取文本") && empty.Preview.Length == 0, empty.Status);
            var wrong = await ReadOnly(f.Zip("wrong.xlsx", ("xl/workbook.xml", $"<wrong xmlns='{S}'/>")));
            Check(wrong.Status.StartsWith("提取失败"), wrong.Status);
        }),
        ("format5 Office locator-only preview limit never claims later text is absent", async f =>
        {
            var path = f.Zip("empty-prefix.docx", ("word/document.xml", $"<w:document xmlns:w='{W}'><w:body>{string.Concat(Enumerable.Repeat("<w:p/>", 8000))}<w:p><w:r><w:t>正文在后面</w:t></w:r></w:p></w:body></w:document>"));
            var result = await ReadOnly(path);
            Check(result.Status.StartsWith("提取受限") && result.Status.Contains("截断") && result.Preview.Length == 0, result.Status);
        }),
        ("format5 text BOM Chinese CR LF CRLF code and CSV preserve physical line numbers", async f =>
        {
            foreach (var name in new[] { "code.cs", "data.csv", "notes.md" })
            {
                var path = f.File(name, "");
                System.IO.File.WriteAllText(path, "甲\r乙\r\n丙\n丁", new UnicodeEncoding(false, true, true));
                var result = await ReadOnly(path);
                Check(result.Preview == "[行 1] 甲\n[行 2] 乙\n[行 3] 丙\n[行 4] 丁\n", result.Preview);
            }
        }),
        ("format5 scripts stay inert and cancellation propagates", async f =>
        {
            var marker = Path.Combine(f.Root, "must-not-run");
            var path = f.File("script.ps1", "Set-Content '" + marker.Replace("'", "''") + "' executed");
            var result = await ReadOnly(path);
            Check(result.Kind == "代码" && result.Preview.Contains("[行 1]") && !System.IO.File.Exists(marker), "script was executed");
            using var ct = new CancellationTokenSource(); ct.Cancel();
            try { await TextExtraction.ExtractAsync(path, Path.GetFileName(path), ct.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { }
        }),
        ("format5 PDF missing Unicode mapping never indexes binary control characters as text", async f =>
        {
            var result = await ReadOnly(Pdf(f, "missing-map.pdf", [TextPage("09")]));
            Check(!result.Status.StartsWith("已提取") && result.Preview.Length == 0 && result.Status.Contains("映射"), result.Status + result.Preview);
        }),
        ("format5 PDF runs in real bounded worker directly from readonly original", async f =>
        {
            var path = Pdf(f, "worker.pdf", [TextPage("0102"), TextPage("05060708")], compress: true);
            var bytes = await System.IO.File.ReadAllBytesAsync(path); var timestamp = System.IO.File.GetLastWriteTimeUtc(path);
            System.IO.File.SetAttributes(path, FileAttributes.ReadOnly); var attrs = System.IO.File.GetAttributes(path);
            var artifact = new ArtifactRecord("fixture", "worker", "worker.pdf", "PDF", "fixture", null, "", path,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, DateTimeOffset.UtcNow, 1, "待提取", "");
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var siblingWorker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Amifolio.Worker", configuration, "Amifolio.Worker.dll"));
            var process = System.IO.File.Exists(siblingWorker) ? new ExtractionProcess(["dotnet", siblingWorker]) : new ExtractionProcess();
            var result = await process.RunAsync(artifact, CancellationToken.None);
            Check(result.Status == "已提取" && result.Preview.Contains("[页 1] 中文") && result.Preview.Contains("[页 2] 第二页文"), result.Status + result.Preview);
            var after = await System.IO.File.ReadAllBytesAsync(path);
            Check(bytes.SequenceEqual(after) && timestamp == System.IO.File.GetLastWriteTimeUtc(path) && attrs == System.IO.File.GetAttributes(path), "worker modified original");
            Check(Directory.GetFiles(f.Root, "*.pdf").Length == 1, "worker copied original");
        }),
    ];

    private static async Task<Extraction> ReadOnly(string path)
    {
        var before = SHA256.HashData(await System.IO.File.ReadAllBytesAsync(path));
        var time = System.IO.File.GetLastWriteTimeUtc(path);
        var directory = Path.GetDirectoryName(path)!;
        var entries = Directory.GetFileSystemEntries(directory).Order().ToArray();
        System.IO.File.SetAttributes(path, System.IO.File.GetAttributes(path) | FileAttributes.ReadOnly);
        var attrs = System.IO.File.GetAttributes(path);
        var result = await TextExtraction.ExtractAsync(path, Path.GetFileName(path), CancellationToken.None);
        var after = SHA256.HashData(await System.IO.File.ReadAllBytesAsync(path));
        Check(before.SequenceEqual(after) && time == System.IO.File.GetLastWriteTimeUtc(path) && attrs == System.IO.File.GetAttributes(path), "source bytes/time/attributes changed");
        Check(entries.SequenceEqual(Directory.GetFileSystemEntries(directory).Order()), "parser wrote a copy or sidecar");
        return result;
    }

    private static string ContentTypes(string part, string type) => $"<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/{part}' ContentType='application/vnd.openxmlformats-officedocument.{type}+xml'/></Types>";
    private static string RootRel(string target) => $"<Relationships xmlns='{Rel}'><Relationship Id='root' Type='{R}/officeDocument' Target='{target}'/></Relationships>";
    private static string Workbook(Fixture f, string name, string cells) => f.Zip(name,
        ("[Content_Types].xml", ContentTypes("xl/workbook.xml", "spreadsheetml.sheet.main")), ("_rels/.rels", RootRel("xl/workbook.xml")),
        ("xl/workbook.xml", $"<workbook xmlns='{S}' xmlns:r='{R}'><sheets><sheet name='中文表' sheetId='1' r:id='sheet'/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels", $"<Relationships xmlns='{Rel}'><Relationship Id='sheet' Target='worksheets/sheet1.xml'/></Relationships>"),
        ("xl/sharedStrings.xml", $"<sst xmlns='{S}'><si><r><t>共享</t></r><r><t>中文</t></r><rPh sb='0' eb='1'><t>phonetic-not-cell-text</t></rPh></si></sst>"),
        ("xl/worksheets/sheet1.xml", $"<worksheet xmlns='{S}'><sheetData>{cells}</sheetData></worksheet>"));

    private static string TextPage(string hex) => $"BT /F1 12 Tf 30 700 Td <{string.Concat(hex.Chunk(2).Select(x => "00" + new string(x)))}> Tj ET\n";
    private const string ImagePage = "q 10 0 0 10 20 20 cm /Im1 Do Q";

    // Small deterministic ISO 32000 PDFs: real xref, ToUnicode CMap, Flate streams,
    // image XObject and Standard Security Handler R2. No external reader/font/tool.
    private static string Pdf(Fixture f, string name, string[] pages, bool compress = false, bool image = false, string? password = null, bool badSecondFont = false)
    {
        var objects = new List<byte[]>();
        void Add(string value) => objects.Add(Encoding.ASCII.GetBytes(value));
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add($"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', Enumerable.Range(0, pages.Length).Select(i => $"{6 + 2 * i} 0 R"))}] >>");
        Add($"<< /Type /Font /Subtype /Type0 /BaseFont /AmifolioFixture /Encoding /Identity-H /DescendantFonts [{6 + 2 * pages.Length} 0 R] /ToUnicode 4 0 R >>");
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Fixture def /CMapType 2 def 1 begincodespacerange <0000> <FFFF> endcodespacerange 8 beginbfchar <0001> <4E2D> <0002> <6587> <0003> <7B2C> <0004> <4E00> <0005> <7B2C> <0006> <4E8C> <0007> <9875> <0008> <6587> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end";
        objects.Add(StreamBytes(Encoding.ASCII.GetBytes(cmap), ""));
        objects.Add(StreamBytes([0, 0, 0], "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8"));

        byte[]? encryptionKey = null; byte[]? owner = null; byte[]? user = null;
        var fileId = MD5.HashData(Encoding.ASCII.GetBytes("Amifolio-format-fixture"));
        if (password is not null)
        {
            var ownerKey = MD5.HashData(Padded("fixture-owner"))[..5];
            owner = Rc4(ownerKey, Padded(password));
            encryptionKey = MD5.HashData([.. Padded(password), .. owner, 0xFC, 0xFF, 0xFF, 0xFF, .. fileId])[..5];
            user = Rc4(encryptionKey, Padded(""));
            var objectKey = MD5.HashData([.. encryptionKey, 4, 0, 0, 0, 0])[..10];
            objects[3] = StreamBytes(Rc4(objectKey, Encoding.ASCII.GetBytes(cmap)), "");
        }
        for (var i = 0; i < pages.Length; i++)
        {
            Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 {(badSecondFont && i == 1 ? "999" : "3")} 0 R >> {(image ? "/XObject << /Im1 5 0 R >>" : "")} >> /Contents {7 + 2 * i} 0 R >>");
            var bytes = Encoding.ASCII.GetBytes(pages[i]);
            if (compress) { using var buffer = new MemoryStream(); using (var z = new ZLibStream(buffer, CompressionLevel.SmallestSize, true)) z.Write(bytes); bytes = buffer.ToArray(); }
            if (encryptionKey is not null)
            {
                var number = 7 + 2 * i;
                var key = MD5.HashData([.. encryptionKey, (byte)number, (byte)(number >> 8), (byte)(number >> 16), 0, 0])[..10];
                bytes = Rc4(key, bytes);
            }
            objects.Add(StreamBytes(bytes, compress ? "/Filter /FlateDecode" : ""));
        }
        Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AmifolioFixture /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {7 + 2 * pages.Length} 0 R /DW 1000 /CIDToGIDMap /Identity >>");
        Add("<< /Type /FontDescriptor /FontName /AmifolioFixture /Flags 4 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 >>");
        if (password is not null) Add($"<< /Filter /Standard /V 1 /R 2 /O <{Convert.ToHexString(owner!)}> /U <{Convert.ToHexString(user!)}> /P -4 >>");
        using var pdf = new MemoryStream();
        void Write(string value) => pdf.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.7\n"); var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++) { offsets.Add(pdf.Position); Write($"{i + 1} 0 obj\n"); pdf.Write(objects[i]); Write("\nendobj\n"); }
        var xref = pdf.Position; Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /ID [<{Convert.ToHexString(fileId)}> <{Convert.ToHexString(fileId)}>] {(password is not null ? $"/Encrypt {objects.Count} 0 R" : "")} >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(f.Root, name); System.IO.File.WriteAllBytes(path, pdf.ToArray()); return path;
    }

    private static byte[] StreamBytes(byte[] bytes, string dictionary)
    {
        using var output = new MemoryStream(); output.Write(Encoding.ASCII.GetBytes($"<< /Length {bytes.Length} {dictionary} >>\nstream\n"));
        output.Write(bytes); output.Write(Encoding.ASCII.GetBytes("\nendstream")); return output.ToArray();
    }
    private static byte[] Padded(string password)
    {
        byte[] padding = [0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0, 0x4E, 0x56, 0xFF, 0xFA, 1, 8, 0x2E, 0x2E, 0, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A];
        return Encoding.ASCII.GetBytes(password).Concat(padding).Take(32).ToArray();
    }
    private static byte[] Rc4(byte[] key, byte[] input)
    {
        var state = Enumerable.Range(0, 256).ToArray(); var j = 0;
        for (var i = 0; i < 256; i++) { j = (j + state[i] + key[i % key.Length]) & 255; (state[i], state[j]) = (state[j], state[i]); }
        var x = 0; j = 0; var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++) { x = (x + 1) & 255; j = (j + state[x]) & 255; (state[x], state[j]) = (state[j], state[x]); output[i] = (byte)(input[i] ^ state[(state[x] + state[j]) & 255]); }
        return output;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
