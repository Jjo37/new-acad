using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Processing;
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
    catch {
      /* by design: a non-scalar JSON node is not truthy */
    }
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
  // ImageSharp 解码（纯托管，自带 webp）—— 最终兜底，不依赖系统 WIC 编解码器。
  // 2026-10-14: 与 LoadGrayViaGdiPlus/Wic 同契约（灰度+可选 RGB + 原始尺寸）。
  private static (byte[] Gray, byte[] Rgb, int W, int H, int OW, int OH) LoadGrayViaImageSharp(string full, int maxSide)
  {
    using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(full);
    var ow = img.Width; var oh = img.Height;
    var scale = Math.Min(1.0, (double)maxSide / Math.Max(ow, oh));
    var w = Math.Max(1, (int)Math.Round(ow * scale));
    var h = Math.Max(1, (int)Math.Round(oh * scale));
    SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> work = img;
    if (w != ow || h != oh) work = img.Clone(x => x.Resize(w, h));
    try
    {
      var px = new byte[w * h * 4];
      work.CopyPixelDataTo(px);
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
    finally { if (!ReferenceEquals(work, img)) work.Dispose(); }
  }
  // ---------------- exportImageGray ----------------
  public static Task<object?> ExportImageGrayAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    var maxSide = PluginRuntime.GetOptionalInt(parameters, "maxSide") ?? 1400;
    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
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
        try
        {
          var wic = LoadGrayViaWic(full, maxSide);
          gray = wic.Gray; rgb = wic.Rgb; w = wic.W; h = wic.H; ow = wic.OW; oh = wic.OH;
        }
        catch
        {
          // 2026-10-14: GDI+ 与 WIC 都不行时走 ImageSharp（随包）—— webp 等在任何机器都能描摹
          var ish = LoadGrayViaImageSharp(full, maxSide);
          gray = ish.Gray; rgb = ish.Rgb; w = ish.W; h = ish.H; ow = ish.OW; oh = ish.OH;
        }
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

  // 2026-10-14: 统一解码入口 —— GDI+（png/jpg/gif/bmp/tiff）→ WIC（系统编解码器）→ ImageSharp（纯托管，自带 webp）。
  // 返回的 Bitmap 已与文件流解耦，可直接 crop/缩放/保存（供 exportImageView 用）。
  // 第三档 ImageSharp 是包内随附的解码器，因此 webp 不再依赖系统 WIC 编解码器。
  private static System.Drawing.Bitmap DecodeToBitmap(string full)
  {
    try
    {
      var raw = File.ReadAllBytes(full);
      using var ms = new MemoryStream(raw);
      using var tmp = new System.Drawing.Bitmap(ms);
      return new System.Drawing.Bitmap(tmp);   // 克隆脱离流，避免锁文件
    }
    catch { /* fall through to WIC */ }
    try
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
      var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
      conv.Freeze();
      var w = conv.PixelWidth; var h = conv.PixelHeight;
      var stride = w * 4;
      var px = new byte[stride * h];
      conv.CopyPixels(px, stride, 0);
      var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
      var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
      try { Marshal.Copy(px, 0, data.Scan0, px.Length); } finally { bmp.UnlockBits(data); }
      return bmp;
    }
    catch { /* fall through to ImageSharp */ }
    // 3) ImageSharp（纯托管，自带 webp 解码）—— 随包分发，不依赖系统 WIC 编解码器
    try
    {
      using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(full);
      var w = img.Width; var h = img.Height;
      var px = new byte[w * h * 4];
      img.CopyPixelDataTo(px);
      var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
      var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
      try { Marshal.Copy(px, 0, data.Scan0, px.Length); } finally { bmp.UnlockBits(data); }
      return bmp;
    }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        Loc.Tk("error.imageDecodeFailed", ("name", Path.GetFileName(full)), ("msg", ex.Message)));
    }
  }
  // ---------------- exportImageView (2026-10-13) ----------------
  /// <summary>
  /// 有损/局部图片处理——**仅在用户明确选择时使用**：readImage 超限后的“压缩后查看 / 局部高清”两条路径。
  /// GDI+ 解码(png/jpg/gif/bmp/tiff) → 可选 crop 裁剪 → 可选等比缩放 → 彩色 JPEG/PNG base64（可直接喂模型视觉）。
  /// 参数：path, maxSide?(0=不缩放), quality?(JPEG,默认85), format?(jpeg|png), crop?{x,y,w,h}
  /// </summary>
  public static Task<object?> ExportImageViewAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "path");
    var maxSide = PluginRuntime.GetOptionalInt(parameters, "maxSide") ?? 0;
    var quality = PluginRuntime.GetOptionalInt(parameters, "quality") ?? 85;
    var format = (PluginRuntime.GetOptionalString(parameters, "format") ?? "jpeg").ToLowerInvariant();
    var cropNode = parameters?["crop"] as JsonObject;
    return CivilExecution.ReadAsync<object?>((doc, database, transaction) =>
    {
      var full = ResolveImagePath(path);
      if (quality < 10) quality = 10;
      if (quality > 100) quality = 100;

      // 2026-10-14: GDI+ 优先，GDI+ 不认的格式（webp 等）回退 WIC（见 DecodeToBitmap）
      using var src = DecodeToBitmap(full);

      var crop = new System.Drawing.Rectangle(0, 0, src.Width, src.Height);
      if (cropNode != null)
      {
        var x = PluginRuntime.GetOptionalInt(cropNode, "x") ?? 0;
        var y = PluginRuntime.GetOptionalInt(cropNode, "y") ?? 0;
        var cw = PluginRuntime.GetOptionalInt(cropNode, "w") ?? 0;
        var ch = PluginRuntime.GetOptionalInt(cropNode, "h") ?? 0;
        if (cw <= 0 || ch <= 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", Loc.Tk("error.cropNeedsWH"));
        x = Math.Max(0, Math.Min(x, src.Width - 1));
        y = Math.Max(0, Math.Min(y, src.Height - 1));
        cw = Math.Min(cw, src.Width - x);
        ch = Math.Min(ch, src.Height - y);
        crop = new System.Drawing.Rectangle(x, y, cw, ch);
      }

      var scale = (maxSide > 0) ? Math.Min(1.0, (double)maxSide / Math.Max(crop.Width, crop.Height)) : 1.0;
      var fw = Math.Max(1, (int)Math.Round(crop.Width * scale));
      var fh = Math.Max(1, (int)Math.Round(crop.Height * scale));

      using var dst = new System.Drawing.Bitmap(fw, fh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
      using (var gfx = System.Drawing.Graphics.FromImage(dst))
      {
        gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        gfx.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        gfx.DrawImage(src, new System.Drawing.Rectangle(0, 0, fw, fh), crop, System.Drawing.GraphicsUnit.Pixel);
      }

      string mime;
      byte[] outBytes;
      using (var outMs = new MemoryStream())
      {
        if (format == "png")
        {
          dst.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
          mime = "image/png";
        }
        else
        {
          mime = "image/jpeg";
          var enc = FindImageEncoder(System.Drawing.Imaging.ImageFormat.Jpeg);
          using var ep = new System.Drawing.Imaging.EncoderParameters(1);
          ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
          dst.Save(outMs, enc, ep);
        }
        outBytes = outMs.ToArray();
      }

      return new Dictionary<string, object?>
      {
        ["path"] = full,
        ["mime"] = mime,
        ["base64"] = Convert.ToBase64String(outBytes),
        ["width"] = fw,
        ["height"] = fh,
        ["originalWidth"] = src.Width,
        ["originalHeight"] = src.Height,
        ["cropped"] = cropNode != null,
        ["scaled"] = scale < 1.0,
        ["bytes"] = outBytes.Length,
      };
    });
  }

  private static System.Drawing.Imaging.ImageCodecInfo FindImageEncoder(System.Drawing.Imaging.ImageFormat fmt)
  {
    foreach (var c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
      if (c.FormatID == fmt.Guid) return c;
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", Loc.Tk("error.encoderNotFound", ("fmt", fmt.ToString())));
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
        catch (System.Exception ex) { PluginLog.Warn("Trace", "VerifyLayerCounts failed to read entities (assertions may be skewed): " + ex.Message); }
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

    return CivilExecution.WriteAsync<object?>((doc, database, transaction) =>
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
          catch {
            /* by design: layer lookup failed -> keep the current layer */
          }
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
