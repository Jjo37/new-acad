using System.Text.Json.Nodes;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace Civil3DMcpPlugin;

/// <summary>
/// 2026-09-23: 颜色解析（新增"逐项着色/填充"能力时抽出，供 importVectorPaths / createHatch / createPolyline 共用）。
///   支持三种写法：
///     [r,g,b]   → TrueColor（0~255）
///     "r,g,b"   → TrueColor
///     数字       → ACI 索引（0~256；256=ByLayer，1~255 常用）
///   解析失败返回 null（调用方保持原色，不报错）。
/// </summary>
internal static class ColorUtils
{
    public static AcColor? Parse(JsonNode? node)
    {
        if (node == null) return null;

        if (node is JsonArray arr && arr.Count >= 3)
        {
            var r = ToInt(arr[0]); var g = ToInt(arr[1]); var b = ToInt(arr[2]);
            if (r < 0 || g < 0 || b < 0) return null;
            return AcColor.FromRgb(Clamp(r), Clamp(g), Clamp(b));
        }

        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var idx) && idx >= 0)
                return AcColor.FromColorIndex(AcColorMethod.ByAci, (short)Math.Min(idx, 256));
            if (v.TryGetValue<double>(out var d) && d >= 0)
                return AcColor.FromColorIndex(AcColorMethod.ByAci, (short)Math.Min((int)Math.Round(d), 256));
            if (v.TryGetValue<string>(out var s))
            {
                var parts = s.Split(',');
                if (parts.Length >= 3
                    && int.TryParse(parts[0].Trim(), out var r) && int.TryParse(parts[1].Trim(), out var g) && int.TryParse(parts[2].Trim(), out var b))
                    return AcColor.FromRgb(Clamp(r), Clamp(g), Clamp(b));
                if (int.TryParse(s.Trim(), out var aci) && aci >= 0)
                    return AcColor.FromColorIndex(AcColorMethod.ByAci, (short)Math.Min(aci, 256));
            }
        }
        return null;
    }

    private static int ToInt(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        }
        return -1;
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
