using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-08-13: 文档/压缩包格式读取——面板 agent 无 exec，解码能力由插件补齐（安全架构哲学：一切能力经插件物理校验）
/// 低成本档：docx/xlsx/pptx 都是 zip+XML，.NET 原生 System.IO.Compression 解压提取文字，零第三方依赖
/// 安全: 绝对路径 + FileBoundary.AssertReadablePath（敏感黑名单）+ 大小限制 + 二进制检测
/// </summary>
public static class FileFormatCommands
{
    // ---------- readDocx / readDoc: Word 文档（.docx=OOXML zip；.doc=Word97 OLE2 二进制，见 LegacyOfficeReaders） ----------
    public static Task<object?> ReadDocxAsync(JsonObject? parameters) => ReadWordCoreAsync(parameters);
    public static Task<object?> ReadDocAsync(JsonObject? parameters) => ReadWordCoreAsync(parameters);

    private static Task<object?> ReadWordCoreAsync(JsonObject? parameters)
    {
        var path = PluginRuntime.GetRequiredString(parameters, "path");
        var maxChars = PluginRuntime.GetOptionalInt(parameters, "maxChars") ?? 8000;
        return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
        {
            var full = ResolveReadPath(path, ".docx", ".doc");
            // 2026-09-11: .doc（Word 97-2003 二进制）走自带解析器；.docx 走 zip+XML
            var isLegacy = Path.GetExtension(full).Equals(".doc", StringComparison.OrdinalIgnoreCase);
            var text = isLegacy ? ReadLegacyDoc(full) : ExtractDocxText(full);
            return BuildTextResult(full, text, maxChars, isLegacy ? "doc" : "docx");
        });
    }

    // ---------- readXlsx / readXls: Excel 表格（.xlsx=sharedStrings+sheet1；.xls=Excel97 BIFF8 二进制，见 LegacyOfficeReaders） ----------
    public static Task<object?> ReadXlsxAsync(JsonObject? parameters) => ReadExcelCoreAsync(parameters);
    public static Task<object?> ReadXlsAsync(JsonObject? parameters) => ReadExcelCoreAsync(parameters);

    private static Task<object?> ReadExcelCoreAsync(JsonObject? parameters)
    {
        var path = PluginRuntime.GetRequiredString(parameters, "path");
        var maxRows = PluginRuntime.GetOptionalInt(parameters, "maxRows") ?? 50;
        return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
        {
            var full = ResolveReadPath(path, ".xlsx", ".xls");
            // 2026-09-11: .xls（Excel 97-2003 BIFF8 二进制）走自带解析器
            if (Path.GetExtension(full).Equals(".xls", StringComparison.OrdinalIgnoreCase))
                return BuildTextResult(full, ReadLegacyXls(full, maxRows), 8000, "xls", "（tab 分隔，第一张工作表，前 " + maxRows + " 行）");
            using var zip = OpenZipShared(full);
            var shared = new List<string>();
            var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
            if (ssEntry != null)
            {
                using var ssReader = new StreamReader(ssEntry.Open(), Encoding.UTF8);
                shared = ParseSharedStrings(ssReader.ReadToEnd());
            }
            var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml")
                ?? throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", "Not a valid xlsx (xl/worksheets/sheet1.xml missing): " + full);
            using var sheetReader = new StreamReader(sheetEntry.Open(), Encoding.UTF8);
            var sheet = ParseSheet(sheetReader.ReadToEnd(), shared, maxRows);
            return BuildTextResult(full, sheet, 8000, "xlsx", "（tab 分隔；" + shared.Count + " 个共享字符串，前 " + maxRows + " 行）");
        });
    }

    // ---------- readPptx: PowerPoint（提取所有幻灯片 <a:t> 文本） ----------
    public static Task<object?> ReadPptxAsync(JsonObject? parameters)
    {
        var path = PluginRuntime.GetRequiredString(parameters, "path");
        var maxChars = PluginRuntime.GetOptionalInt(parameters, "maxChars") ?? 8000;
        return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
        {
            var full = ResolveReadPath(path, ".pptx");
            using var zip = OpenZipShared(full);
            var slides = zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml"))
                .OrderBy(e => e.FullName, StringComparer.Ordinal)
                .ToList();
            if (slides.Count == 0)
                throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", "Not a valid pptx (no slides found): " + full);
            var sb = new StringBuilder();
            int idx = 0;
            foreach (var entry in slides)
            {
                using var r = new StreamReader(entry.Open(), Encoding.UTF8);
                var xml = r.ReadToEnd();
                var texts = Regex.Matches(xml, @"<a:t>(.*?)</a:t>", RegexOptions.Singleline)
                    .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value));
                sb.AppendLine("--- 幻灯片 " + (++idx) + " ---");
                sb.AppendLine(string.Join("\n", texts));
            }
            return BuildTextResult(full, sb.ToString(), maxChars, "pptx");
        });
    }

    // ---------- readZip: 压缩包（无 entry=列清单；有 entry=提取该条目文本） ----------
    public static Task<object?> ReadZipAsync(JsonObject? parameters)
    {
        var path = PluginRuntime.GetRequiredString(parameters, "path");
        var entryName = PluginRuntime.GetOptionalString(parameters, "entry");
        return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
        {
            var full = ResolveReadPath(path, ".zip");
            using var zip = OpenZipShared(full);
            if (string.IsNullOrWhiteSpace(entryName))
            {
                var entries = new List<object?>();
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue; // 目录条目
                    entries.Add(new Dictionary<string, object?> { ["name"] = e.FullName, ["size"] = e.Length });
                }
                return (object?)new Dictionary<string, object?>
                {
                    ["path"] = full,
                    ["entries"] = entries,
                    ["count"] = entries.Count,
                    ["note"] = "列清单完成。要读某个文本条目内容，传 entry=条目名",
                };
            }
            // 提取指定条目
            var target = zip.Entries.FirstOrDefault(e => e.FullName == entryName || e.Name == entryName)
                ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "Entry not found in zip: " + entryName);
            if (target.Length > 1024 * 1024)
                throw new JsonRpcDispatchException("CIVIL3D.FILE_TOO_LARGE", "Entry too large to read as text (>1MB): " + entryName);
            using var reader = new StreamReader(target.Open(), Encoding.UTF8);
            var content = reader.ReadToEnd();
            if (content.IndexOf('\0') >= 0)
                throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", "Entry is binary, not readable as text: " + entryName);
            var truncated = content.Length > 8000;
            return (object?)new Dictionary<string, object?>
            {
                ["path"] = full,
                ["entry"] = target.FullName,
                ["size"] = target.Length,
                ["truncated"] = truncated,
                ["content"] = truncated ? content[..8000] : content,
                ["note"] = "条目内容将发送给 AI 模型服务商处理",
            };
        });
    }

    // ---------- 内部工具 ----------

    private static string ResolveReadPath(string rawPath, params string[] allowedExts)
    {
        if (!Path.IsPathFullyQualified(rawPath))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Path must be absolute: {rawPath}");
        var full = Path.GetFullPath(rawPath);
        FileBoundary.AssertReadablePath(full);
        if (!File.Exists(full))
            throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"File not found: {full}");
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (!allowedExts.Contains(ext))
            throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", $"Expected {string.Join("/", allowedExts)} file, got {ext}: {full}");
        return full;
    }

    // 2026-09-11: 老格式 .doc/.xls 读取入口（共享读 + 友好错误包装）
    private static string ReadLegacyDoc(string full)
    {
        var bytes = ReadAllBytesShared(full);
        try { return LegacyOfficeReaders.ReadDocText(bytes); }
        catch (InvalidDataException ex) { throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", "Not a readable .doc (Word 97-2003): " + ex.Message); }
    }

    private static string ReadLegacyXls(string full, int maxRows)
    {
        var bytes = ReadAllBytesShared(full);
        try { return LegacyOfficeReaders.ReadXlsText(bytes, maxRows); }
        catch (InvalidDataException ex) { throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", "Not a readable .xls (Excel 97-2003): " + ex.Message); }
    }

    private static byte[] ReadAllBytesShared(string full)
    {
        try
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 64L * 1024 * 1024)
                throw new JsonRpcDispatchException("CIVIL3D.FILE_TOO_LARGE", "File too large (>64MB): " + full);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch (IOException ex)
        {
            throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"File is locked by another process: {full} ({ex.Message})");
        }
    }

    private static object BuildTextResult(string full, string text, int maxChars, string format, string extraNote = "")
    {
        var truncated = text.Length > maxChars;
        return new Dictionary<string, object?>
        {
            ["path"] = full,
            ["format"] = format,
            ["totalChars"] = text.Length,
            ["truncated"] = truncated,
            ["content"] = truncated ? text[..maxChars] : text,
            ["note"] = "内容将发送给 AI 模型服务商处理" + (extraNote.Length > 0 ? "；" + extraNote : ""),
        };
    }

    private static ZipArchive OpenZipShared(string full)
    {
        try
        {
            // 2026-08-13: FileShare.ReadWrite——允许读被 Word/Excel/CAD 打开的文件（ZipFile.OpenRead 默认独占读会撞锁）
            var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (IOException ex)
        {
            throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"File is locked by another process: {full} ({ex.Message})");
        }
    }

    private static string ExtractDocxText(string path)
    {
        using var zip = OpenZipShared(path);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", "Not a valid docx (word/document.xml missing): " + path);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        var text = Regex.Replace(xml, @"</w:p>", "\n");
        text = Regex.Replace(text, @"<[^>]+>", "");
        return WebUtility.HtmlDecode(text);
    }

    private static List<string> ParseSharedStrings(string xml)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(xml, @"<si>(.*?)</si>", RegexOptions.Singleline))
        {
            var sb = new StringBuilder();
            foreach (Match t in Regex.Matches(m.Groups[1].Value, @"<t[^>]*>(.*?)</t>", RegexOptions.Singleline))
                sb.Append(WebUtility.HtmlDecode(t.Groups[1].Value));
            list.Add(sb.ToString());
        }
        return list;
    }

    private static string ParseSheet(string xml, List<string> shared, int maxRows)
    {
        var sb = new StringBuilder();
        int rowCount = 0;
        foreach (Match row in Regex.Matches(xml, @"<row[^>]*>(.*?)</row>", RegexOptions.Singleline))
        {
            if (rowCount >= maxRows) { sb.AppendLine("...（已截断）"); break; }
            rowCount++;
            var cells = new List<string>();
            foreach (Match cell in Regex.Matches(row.Groups[1].Value, @"<c[^>]*>(.*?)</c>", RegexOptions.Singleline))
            {
                var tMatch = Regex.Match(cell.Groups[0].Value, @"t=""([^""]+)""");
                var vMatch = Regex.Match(cell.Groups[1].Value, @"<v>(.*?)</v>", RegexOptions.Singleline);
                if (!vMatch.Success) { cells.Add(""); continue; }
                var v = WebUtility.HtmlDecode(vMatch.Groups[1].Value);
                if (tMatch.Success && tMatch.Groups[1].Value == "s")
                {
                    if (int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count) cells.Add(shared[idx]);
                    else cells.Add("");
                }
                else cells.Add(v);
            }
            sb.AppendLine(string.Join("\t", cells));
        }
        return sb.ToString();
    }
}
