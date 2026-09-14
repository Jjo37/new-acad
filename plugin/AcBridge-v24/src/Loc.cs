using System.Globalization;
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
        catch { }
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

    /// <summary>config.json 所在根目录（DLL 位于 &lt;root&gt;\plugin\AcBridge-v24\）。</summary>
    public static string Root()
    {
        try
        {
            var asmDir = Path.GetDirectoryName(typeof(PluginRuntime).Assembly.Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                var root = Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                if (Directory.Exists(root)) return root;
            }
            return asmDir ?? "";
        }
        catch { return ""; }
    }
}
