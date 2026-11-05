using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using UglyToad.PdfPig;

namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-10-13 M6: PDF 文本层抽取（用户知识库摄取用）。
///   仅抽取**文本层**；扫描件（图片型 PDF）无文本层 → 内容可能为空（本期不做 OCR）。
///   readPdf {path, maxChars?, maxPages?} → {path, format, totalChars, truncated, content, note}
/// </summary>
public static class PdfCommands
{
  public static Task<object?> ReadPdfAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    var maxChars = PluginRuntime.GetOptionalInt(parameters, "maxChars") ?? 20000;
    var maxPages = PluginRuntime.GetOptionalInt(parameters, "maxPages") ?? 0;
    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      var full = ResolvePath(path);
      var sb = new StringBuilder();
      int pages = 0;
      try
      {
        using var pdf = PdfDocument.Open(full);
        var n = pdf.NumberOfPages;
        pages = maxPages > 0 ? Math.Min(maxPages, n) : n;
        for (var i = 1; i <= pages; i++)
        {
          sb.Append("--- Page ").Append(i).Append(" ---\n");
          foreach (var w in pdf.GetPage(i).GetWords()) { sb.Append(w.Text); sb.Append(' '); }
          sb.Append('\n');
          if (sb.Length > maxChars * 3) break;   // 提前刹车（最终仍按 maxChars 截断）
        }
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", Loc.Tk("error.pdfUnreadable", ("msg", ex.Message)));
      }
      var text = sb.ToString();
      var truncated = text.Length > maxChars;
      return new Dictionary<string, object?>
      {
        ["path"] = full,
        ["format"] = "pdf",
        ["pages"] = pages,
        ["totalChars"] = text.Length,
        ["truncated"] = truncated,
        ["content"] = truncated ? text[..maxChars] : text,
        ["note"] = Loc.Tk("status.pdfTextLayerOnly")
      };
    });
  }

  private static string ResolvePath(string rawPath)
  {
    if (string.IsNullOrWhiteSpace(rawPath))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "path is required.");
    if (!Path.IsPathRooted(rawPath))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Path must be absolute: {rawPath}");
    var full = Path.GetFullPath(rawPath);
    FileBoundary.AssertReadablePath(full);
    if (!File.Exists(full))
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"File not found: {full}");
    if (!Path.GetExtension(full).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", $"Expected .pdf, got {Path.GetExtension(full)}: {full}");
    return full;
  }
}
