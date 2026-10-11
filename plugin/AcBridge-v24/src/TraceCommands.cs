using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-09-22: 位图 → CAD 矢量线条 的插件侧配套（Node 侧 server/trace-image.js 负责算法与落地编排）
///
///   exportImageGray  : 任意位图（GDI+ 支持 png/jpg/gif/bmp/tiff）→ 灰度 + 等比缩放 + deflate + base64
///                      用途：Node 侧 trace-image.js 只自带 PNG/BMP 解码；JPEG/GIF/TIFF 先过这个方法。
///   importVectorPaths: 批量导入矢量路径 —— 一次事务画完几百条多段线（支持 bulge/closed/逐项图层）
///                      用途：traceImage 落地批量（省掉逐条 RPC）
///
/// 安全：读图片走 FileBoundary.AssertReadablePath（与 readDocx 等一致），绝对路径 + 黑名单；写入仅限当前图纸。
/// </summary>
public static class TraceCommands
{
  private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

  private static string ResolveImagePath(string rawPath)
  {
    if (string.IsNullOrWhiteSpace(rawPath))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "path is required.");
    if (!Path.IsPathRooted(rawPath))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Path must be absolute: {rawPath}");
    var full = Path.GetFullPath(rawPath);
    FileBoundary.AssertReadablePath(full);
    if (!File.Exists(full))
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"File not found: {full}");
    var ext = Path.GetExtension(full).ToLowerInvariant();
    if (Array.IndexOf(ImageExts, ext) < 0)
      throw new JsonRpcDispatchException("CIVIL3D.FILE_TYPE_NOT_ALLOWED", $"Expected image ({string.Join("/", ImageExts)}), got {ext}: {full}");
    return full;
  }

  private static bool IsTruthy(JsonNode? node)
  {
    if (node == null) return false;
    try
    {
      if (node is JsonValue v)
      {
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<double>(out var d)) return Math.Abs(d) > 1e-9;
      }
    }
    catch { /* 非标量节点 */ }
    return false;
  }

  // GDI+ 解码（png/jpg/gif/bmp/tiff）；格式不支持时抛异常，由调用方回退 WIC
  // 2026-10-10 D-046: 同时返回 RGB（flat/region/posterize/tone 需要真实颜色）
  private static (byte[] Gray, byte[] Rgb, int W, int H, int OW, int OH) LoadGrayViaGdiPlus(string full, int maxSide)
  {
    using var src = new System.Drawing.Bitmap(full);
    var ow = src.Width; var oh = src.Height;
    var scale = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
    var w = Math.Max(1, (int)Math.Round(src.Width * scale));
    var h = Math.Max(1, (int)Math.Round(src.Height * scale));
    using var scaled = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
    using (var gfx = System.Drawing.Graphics.FromImage(scaled))
    {
      gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
      gfx.DrawImage(src, 0, 0, w, h);
    }
    var rect = new System.Drawing.Rectangle(0, 0, w, h);
    var data = scaled.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
    byte[] gray;
    byte[] rgb;
    try
    {
      var stride = data.Stride;
      var tmp = new byte[stride * h];
      Marshal.Copy(data.Scan0, tmp, 0, tmp.Length);
      gray = new byte[w * h];
      rgb = new byte[w * h * 3];
      for (var y = 0; y < h; y++)
      {
        var row = y * stride;
        for (var x = 0; x < w; x++)
        {
          var o = row + x * 3;      // 24bppRgb 内存序 = B,G,R
          var p = (y * w + x) * 3;
          rgb[p] = tmp[o + 2]; rgb[p + 1] = tmp[o + 1]; rgb[p + 2] = tmp[o];
          gray[y * w + x] = (byte)((tmp[o + 2] * 299 + tmp[o + 1] * 587 + tmp[o] * 114) / 1000);
        }
      }
    }
    finally { scaled.UnlockBits(data); }
    return (gray, rgb, w, h, ow, oh);
  }

  // WIC 解码（WPF/PresentationCore 成像栈 → 系统 WIC 编解码器）：覆盖 GDI+ 不支持的 webp 等
  private static (byte[] Gray, byte[] Rgb, int W, int H, int OW, int OH) LoadGrayViaWic(string full, int maxSide)
  {
    System.Windows.Media.Imaging.BitmapSource frame;
    using (var fs = System.IO.File.OpenRead(full))
    {
      var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
        fs,
        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
      frame = decoder.Frames[0];
    }
    frame.Freeze();
    var ow = frame.PixelWidth; var oh = frame.PixelHeight;
    var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
    conv.Freeze();
    System.Windows.Media.Imaging.BitmapSource src = conv;
    var scale = Math.Min(1.0, (double)maxSide / Math.Max(ow, oh));
    if (scale < 1.0)
    {
      var tb = new System.Windows.Media.Imaging.TransformedBitmap(conv, new System.Windows.Media.ScaleTransform(scale, scale));
      tb.Freeze();
      src = tb;
    }
    var w = src.PixelWidth; var h = src.PixelHeight;
    var stride = w * 4;
    var px = new byte[stride * h];
    src.CopyPixels(px, stride, 0);
    var gray = new byte[w * h];
    var rgb = new byte[w * h * 3];
    for (var i = 0; i < w * h; i++)
    {
      var o = i * 4;   // Bgra32: B,G,R,A
      rgb[i * 3] = px[o + 2]; rgb[i * 3 + 1] = px[o + 1]; rgb[i * 3 + 2] = px[o];
      gray[i] = (byte)((px[o + 2] * 299 + px[o + 1] * 587 + px[o] * 114) / 1000);
    }
    return (gray, rgb, w, h, ow, oh);
  }
  // ---------------- exportImageGray ----------------
  public static Task<object?> ExportImageGrayAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    var maxSide = PluginRuntime.GetOptionalInt(parameters, "maxSide") ?? 1400;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var full = ResolveImagePath(path);
      if (maxSide < 64) maxSide = 64;
      if (maxSide > 4096) maxSide = 4096;

      // 2026-10-04: GDI+ 优先（png/jpg/gif/bmp/tiff）；GDI+ 不认的格式（webp 等）回退 WIC（PresentationCore）
      // 2026-10-10 D-046: withColor=true 时额外回传 RGB（同级 deflate+base64），供 flat/tone 保色
      var withColor = IsTruthy(parameters?["withColor"]);
      byte[] gray;
      byte[]? rgb = null;
      int w, h, ow, oh;
      try
      {
        var gdi = LoadGrayViaGdiPlus(full, maxSide);
        gray = gdi.Gray; rgb = gdi.Rgb; w = gdi.W; h = gdi.H; ow = gdi.OW; oh = gdi.OH;
      }
      catch
      {
        var wic = LoadGrayViaWic(full, maxSide);
        gray = wic.Gray; rgb = wic.Rgb; w = wic.W; h = wic.H; ow = wic.OW; oh = wic.OH;
      }

      string b64;
      using (var ms = new MemoryStream())
      {
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(gray, 0, gray.Length);
        b64 = Convert.ToBase64String(ms.ToArray());
      }
      string? rgbB64 = null;
      if (withColor && rgb != null)
      {
        using var ms2 = new MemoryStream();
        using (var ds2 = new DeflateStream(ms2, CompressionLevel.Optimal, true)) ds2.Write(rgb, 0, rgb.Length);
        rgbB64 = Convert.ToBase64String(ms2.ToArray());
      }

      var res = new Dictionary<string, object?>
      {
        ["path"] = full,
        ["width"] = w,
        ["height"] = h,
        ["originalWidth"] = ow,
        ["originalHeight"] = oh,
        ["encoding"] = "gray-deflate-b64",
        ["gray"] = b64,
      };
      if (rgbB64 != null) { res["rgb"] = rgbB64; res["colorEncoding"] = "rgb-deflate-b64"; res["channels"] = 3; }
      return res;
    });
  }

  // ---------------- importVectorPaths ----------------
  /// <summary>B3.2：校验"每层实际多段线数 ≥ 本次创建数"</summary>
  private static Dictionary<string, object?> VerifyLayerCounts(Transaction transaction, BlockTableRecord modelSpace, Dictionary<string, int> createdByLayer)
  {
    var byLayer = new Dictionary<string, object?>();
    var pass = true;
    foreach (var kv in createdByLayer)
    {
      var actual = 0;
      foreach (ObjectId id in modelSpace)
      {
        if (id.IsNull || id.IsErased) continue;
        try
        {
          if (transaction.GetObject(id, OpenMode.ForRead, false) is Polyline pl && string.Equals(pl.Layer, kv.Key, StringComparison.OrdinalIgnoreCase)) actual++;
        }
        catch (System.Exception ex) { PluginLog.Warn("Trace", "VerifyLayerCounts 读取实体失败(断言可能失真): " + ex.Message); }
      }
      var ok = actual >= kv.Value;
      pass &= ok;
      byLayer[kv.Key] = new Dictionary<string, object?> { ["created"] = kv.Value, ["countOnLayer"] = actual, ["pass"] = ok };
    }
    return new Dictionary<string, object?>
    {
      ["pass"] = pass,
      ["check"] = "polylineCountOnLayer >= created",
      ["byLayer"] = byLayer,
    };
  }

  public static Task<object?> ImportVectorPathsAsync(JsonObject? parameters)
  {
    var itemsNode = PluginRuntime.GetParameter(parameters, "items") as JsonArray
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "items array is required: {items:[{points, bulges?, closed?, layer?}], layer?}");
    var defaultLayer = PluginRuntime.GetOptionalString(parameters, "layer");
    if (itemsNode.Count > 20000)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"items too many ({itemsNode.Count} > 20000)");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var created = 0; var colored = 0;
      var createdByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // B3.2 自动断言用
      var vertexCount = 0;
      var failed = 0;
      var failedSamples = new List<string>();
      var layers = new List<string>();

      foreach (var node in itemsNode)
      {
        if (node is not JsonObject item) { failed++; continue; }
        if (item["points"] is not JsonArray pts || pts.Count < 2)
        {
          failed++;
          if (failedSamples.Count < 5) failedSamples.Add("item without >=2 points");
          continue;
        }

        var bulges = item["bulges"] as JsonArray;
        var layerName = item["layer"] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : defaultLayer;
        var closed = IsTruthy(item["closed"]);

        var pl = new Polyline();
        for (var i = 0; i < pts.Count; i++)
        {
          double x, y;
          var p = pts[i];
          if (p is JsonArray pa && pa.Count >= 2 && pa[0] != null && pa[1] != null)
          {
            x = pa[0]!.GetValue<double>();
            y = pa[1]!.GetValue<double>();
          }
          else if (p is JsonObject po && po["x"] != null && po["y"] != null)
          {
            x = po["x"]!.GetValue<double>();
            y = po["y"]!.GetValue<double>();
          }
          else continue;

          double bulge = 0;
          if (bulges != null && i < bulges.Count && bulges[i] != null)
          {
            try { bulge = bulges[i]!.GetValue<double>(); } catch { bulge = 0; }
            if (double.IsNaN(bulge) || double.IsInfinity(bulge)) bulge = 0;
            bulge = Math.Max(-1.9, Math.Min(1.9, bulge));
          }
          pl.AddVertexAt(pl.NumberOfVertices, new Point2d(x, y), bulge, 0, 0);
        }

        if (pl.NumberOfVertices < 2)
        {
          failed++;
          if (failedSamples.Count < 5) failedSamples.Add("item with <2 valid points");
          continue;
        }

        pl.Closed = closed;
        if (!string.IsNullOrWhiteSpace(layerName))
        {
          try { pl.LayerId = LookupUtils.GetLayerId(database, transaction, layerName); }
          catch { /* 图层解析失败则用当前层 */ }
        }
        // 2026-09-23 修复：item.color 之前被完全忽略（flat 模式的逐项颜色一直丢弃）
        var itemColor = ColorUtils.Parse(item["color"]);
        if (itemColor != null) { pl.Color = itemColor; colored++; }
        modelSpace.AppendEntity(pl);
        transaction.AddNewlyCreatedDBObject(pl, true);
        created++;
        var layerKey = pl.Layer ?? "0";
        createdByLayer[layerKey] = createdByLayer.TryGetValue(layerKey, out var c0) ? c0 + 1 : 1;
        vertexCount += pl.NumberOfVertices;
        if (!layers.Contains(pl.Layer, StringComparer.OrdinalIgnoreCase)) layers.Add(pl.Layer);
      }

      return new Dictionary<string, object?>
      {
        ["created"] = created,
        ["colored"] = colored,
        ["vertexCount"] = vertexCount,
        ["failed"] = failed,
        ["failedSamples"] = failedSamples.ToArray(),
        ["layers"] = layers.ToArray(),
        // B3.2（2026-09-28）：操作后自动断言 —— 每层多段线总数 ≥ 本次创建数（落地漏画会立刻暴露）
        ["verification"] = VerifyLayerCounts(transaction, modelSpace, createdByLayer),
      };
    });
  }
}
