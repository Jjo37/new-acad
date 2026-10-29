using System.Globalization;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// 面板/对话框本地化（2026-09-14）。
/// 语言来源: config.json 的 locale 字段 —— auto(跟随系统 UI 语言) | zh-CN | en-US。
/// 用法: Loc.T("中文", "English")；静态控件文案在 BuildContent 里现算，语言切换走 RebuildUi 重建面板。
/// </summary>
internal static class Loc
{
    public const string Auto = "auto";
    public const string Zh = "zh-CN";
    public const string En = "en-US";

    /// <summary>当前生效的语言配置值（auto 表示跟随系统）。</summary>
    public static string Current { get; private set; } = Auto;

    /// <summary>true = 英文界面。</summary>
    public static bool IsEnglish { get; private set; }

    private static bool inited;

    /// <summary>从 config.json 读 locale 并应用（幂等，只读一次）。</summary>
    public static void Init()
    {
        if (inited) return;
        inited = true;
        string loc = Auto;
        try
        {
            var cfg = Path.Combine(Root(), "config.json");
            if (File.Exists(cfg))
            {
                var jo = JsonNode.Parse(File.ReadAllText(cfg)) as JsonObject;
                var v = jo?["locale"]?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) loc = v!.Trim();
            }
        }
        catch (Exception ex) {
          PluginLog.Debug("Loc", "reading the locale from config.json failed; keeping the default: " + ex.Message);
        }
        Apply(loc);
    }

    /// <summary>应用语言值（不写盘）。</summary>
    public static void Apply(string locale)
    {
        var l = (locale ?? Auto).Trim();
        if (l.StartsWith("en", StringComparison.OrdinalIgnoreCase)) { Current = En; IsEnglish = true; return; }
        if (l.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) { Current = Zh; IsEnglish = false; return; }
        Current = Auto;
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        IsEnglish = !two.Equals("zh", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>中文 / 英文二选一。</summary>
    public static string T(string zh, string en) => IsEnglish ? en : zh;

    // ── 2026-10-19: 统一文案表（i18n/messages.json），与 Node 侧 server/i18n.js 共用 ──
    private static Dictionary<string, JsonObject>? catalog;

    private static void EnsureCatalog()
    {
        if (catalog != null) return;
        catalog = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var f = Path.Combine(Root(), "i18n", "messages.json");
            if (!File.Exists(f)) return;
            if (JsonNode.Parse(File.ReadAllText(f)) is not JsonObject jo) return;
            foreach (var kv in jo)
            {
                if (kv.Key.StartsWith("_", StringComparison.Ordinal)) continue;
                if (kv.Value is JsonObject o) catalog[kv.Key] = o;
            }
        }
        catch (Exception ex) { PluginLog.Debug("Loc", "load i18n/messages.json failed: " + ex.Message); }
    }

    /// <summary>
    /// 按 key 取文案：Tk("panel.send") / Tk("status.saveFailed", ("msg", ex.Message))。
    /// 缺 key 时**显形**为 ⟪key⟫——开发期立刻能看到，不静默给错字。
    /// </summary>
    public static string Tk(string key, params (string Key, object? Value)[] vars)
    {
        EnsureCatalog();
        if (catalog == null || !catalog.TryGetValue(key, out var o)) return "\u27EA" + key + "\u27EB";
        var s = (IsEnglish ? o["en"]?.ToString() : o["zh"]?.ToString())
                ?? o["zh"]?.ToString() ?? o["en"]?.ToString();
        if (s == null) return "\u27EA" + key + "\u27EB";
        if (vars != null)
            foreach (var v in vars) s = s.Replace("{" + v.Key + "}", v.Value?.ToString() ?? "");
        return s;
    }

    /// <summary>文案表里是否有该 key（迁移期用来"有则用表、无则退回 Loc.T(zh,en)"）。</summary>
    public static bool HasKey(string key)
    {
        EnsureCatalog();
        return catalog != null && catalog.ContainsKey(key);
    }

    /// <summary>config.json 所在根目录（DLL 位于 &lt;root&gt;\plugin\AcBridge-v24\）。</summary>
    public static string Root()
    {
        // 2026-09-15: 统一探测式根目录（bundle 布局兼容；旧版 asmDir\..\.. 在 bundle 下差一级）
        try { return PluginRuntime.ProjectRoot(); }
        catch { return ""; }
    }
}
