// HankPalette.cs - with status indicators
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using System;
using System.Drawing;
using System.Net.Http;
using System.Text;

using System.Windows.Forms;
using SysExc = System.Exception;

[assembly: CommandClass(typeof(Civil3DMcpPlugin.HankPalette))]

namespace Civil3DMcpPlugin;

public static class HankPalette
{
    private static PaletteSet ps;
    private static RichTextBox msgs;
    private static TextBox input;
    private static Label statusLbl;
    private static HttpClient hc = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    private static string relayUrl = "http://127.0.0.1:19876";
    private static System.Timers.Timer statusTimer;
    // 2026-09-14: 状态栏拆「base(在线/模式/模型/CAD) + progress(进度)」两段；
    // 进度改成 relay 通过 /replies/stream 实时推送，10s 轮询降级为兜底
    private static string statusBase = "";
    private static string progressText = "";
    private static HttpClient sseHc = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private static System.Threading.CancellationTokenSource sseCts;
    private static int waitSeq = 0;

    // 模型配置 UI（2026-08-06 发布版：面板内选模型 + 填 key）
    private static ComboBox providerCbo;
    private static ComboBox modelCbo;
    private static TextBox apiKeyBox;
    private static TextBox baseUrlBox;
    private static TextBox turnsBox;   // 2026-08-10: 工具轮数上限（0=无限）

    private static ComboBox projectBox;
    private static Panel cfgPanel;
    private static bool cfgLoaded = false;  // 预设加载完成标记（后续可用）

    [CommandMethod("HANK_SHOW")]
    public static void Show()
    {
        if (ps != null && ps.Visible) return;
        BuildUI();
        StartTimers();
    }

    private static UserControl? rootUc;

    private static void BuildUI()
    {
        Loc.Init();   // 2026-09-14: 语言来源 config.json(locale)；auto = 跟随系统
        if (ps == null)
        {
            ps = new PaletteSet("", new Guid()) { Size = new Size(440, 570), MinimumSize = new Size(320, 470),
                Style = PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton };
            rootUc = new UserControl { Dock = DockStyle.Fill };
            rootUc.BackColor = Color.FromArgb(30, 30, 46);
            ps.Add("", rootUc);
        }
        BuildContent(rootUc!);
        ps.Visible = true;
        Add("", Loc.T("汉克就绪", "Hank ready"));
        _ = LoadLlmOptionsAsync();  // 异步拉服务商预设
    }

    // 2026-09-14 语言热切换：清空并重建面板内容（保留对话文本，控件全量 Dispose 防 GDI 泄漏）
    private static void RebuildUi()
    {
        if (ps == null || rootUc == null) return;
        var hist = msgs?.Text ?? "";
        try { StopTimers(); } catch { }
        while (rootUc.Controls.Count > 0)
        {
            var cc = rootUc.Controls[0];
            rootUc.Controls.RemoveAt(0);
            try { cc.Dispose(); } catch { }
        }
        cfgFolded = false;   // 重建后配置区一律展开（否则折叠按钮状态与画面不一致）
        BuildContent(rootUc);
        if (!string.IsNullOrEmpty(hist))
        {
            try { msgs!.Text = hist; msgs!.SelectionStart = msgs!.TextLength; msgs!.ScrollToCaret(); } catch { }
        }
        // 2026-09-14 修复（Bro 实机反馈）：重建后必须重新拉「服务商预设 + 当前配置 + 项目列表」，
        // 否则 BuildContent 新建的 providerCbo / projectBox 始终空白（首次构建走 BuildUI 里那次调用，
        // 重建这条路径漏了 → 点一次语言，两个下拉就全空）。
        _ = LoadLlmOptionsAsync();
        try { StartTimers(); } catch { }
    }

    // 2026-09-14 语言切换：写 config.json(locale) + 热重建面板
    private static async System.Threading.Tasks.Task SwitchLocaleAsync(string target)
    {
        try
        {
            await hc.PostAsync(relayUrl + "/locale", new StringContent("{\"locale\":\"" + target + "\"}", Encoding.UTF8, "application/json"));
        }
        catch (SysExc) { }
        Loc.Apply(target);
        RebuildUi();
    }

    private static void BuildContent(UserControl c)
    {

        // Status bar at top（2026-08-10: 外层加 statusBar，右侧挂"重启服务"按钮）
        var statusBar = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Color.FromArgb(49, 50, 68) };
        statusLbl = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(49, 50, 68), ForeColor = Color.FromArgb(166, 227, 161),
            Font = new Font("Microsoft YaHei", 8), Padding = new Padding(8, 0, 0, 0), Text = Loc.T("  ● 就绪", "  ● Ready") };
        var bRestart = new Button { Text = Loc.T("重启服务", "Restart"), Dock = DockStyle.Right, AutoSize = true,
            BackColor = Color.FromArgb(49, 50, 68), ForeColor = Color.FromArgb(249, 226, 175),
            FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 8, FontStyle.Bold) };
        bRestart.Click += async (_, _) => await RestartRelayAsync();
        // 2026-08-13 P3-B: 端口诊断按钮（位于“重启服务”左侧，statusLbl 不动）。AutoSize=true 按内容定宽，任意 DPI 不截断/不碰撞
        var bPorts = new Button { Text = Loc.T("端口", "Ports"), Dock = DockStyle.Right, AutoSize = true,
            BackColor = Color.FromArgb(49, 50, 68), ForeColor = Color.FromArgb(166, 227, 161),
            FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 8, FontStyle.Bold) };
        bPorts.Click += (_, _) => ShowPortStatus();
        // 2026-08-17: 折叠按钮移到状态栏最右（cfgPanel 里会被行布局干扰不可见；statusBar 是顶层 Dock Top 永远可见）
        var bFold = new Button { Text = "▲", Dock = DockStyle.Right, AutoSize = true,
            BackColor = Color.FromArgb(49, 50, 68), ForeColor = Color.FromArgb(205, 214, 244),
            FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 8, FontStyle.Bold) };
        bFold.Click += (_, _) => ToggleCfgPanel(bFold);
        statusBar.Controls.Add(bFold);
        statusBar.Controls.Add(bRestart);
        statusBar.Controls.Add(bPorts);
        statusBar.Controls.Add(statusLbl);

        msgs = new RichTextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 46),
            ForeColor = Color.FromArgb(205, 214, 244), ReadOnly = true, WordWrap = true,
            BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei", 10) };
        // 输入区（SplitContainer Panel2 内 Fill）: 左侧可拉高输入框 + 右侧竖排按钮
        var p = new Panel { Dock = DockStyle.Fill };
        p.BackColor = Color.FromArgb(49, 50, 68);
        input = new TextBox { Dock = DockStyle.Fill, Multiline = true, WordWrap = true, AcceptsReturn = false,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(30, 30, 46), ForeColor = Color.FromArgb(205, 214, 244),
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei", 10) };
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Send(); e.SuppressKeyPress = true; }
            // 2026-08-10: 拦截 ESC——不拦会冒泡给 AutoCAD，焦点丢出面板后
            // AutoHide 模式下面板整条收起，输入框"消失"，只能重新调出面板
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; }
        };
        // 2026-08-10: 按钮改横排（原竖排 4 按钮 120px，输入区只有 ~50px 时必然溢出打架）——
        // 按钮行 Dock Bottom 40px，输入框占剩余，任何面板尺寸都不会重叠
        var btnRow = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 40, ColumnCount = 5, Padding = new Padding(6, 5, 6, 3), BackColor = Color.FromArgb(49, 50, 68) };
        for (int ci = 0; ci < 5; ci++) btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        var b = new Button { Text = Loc.T("发送", "Send"), Dock = DockStyle.Fill, Margin = new Padding(2),
            BackColor = Color.FromArgb(137, 180, 250), ForeColor = Color.FromArgb(30, 30, 46),
            FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold) };
        b.Click += (_, _) => Send();
        var bCancel = new Button { Text = Loc.T("取消任务", "Cancel task"), Dock = DockStyle.Fill, Margin = new Padding(2),
            BackColor = Color.FromArgb(243, 139, 168), ForeColor = Color.FromArgb(30, 30, 46),
            FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold) };
        bCancel.Click += async (_, _) =>
        {
            try
            {
                await hc.PostAsync(relayUrl + "/cancel", new StringContent("{}", Encoding.UTF8, "application/json"));
                SetStatus(Loc.T("● 已取消", "● Cancelled"), Color.FromArgb(249, 226, 175));
            }
            catch (SysExc) { SetStatus(Loc.T("● 取消失败", "● Cancel failed"), Color.FromArgb(243, 139, 168)); }
        };
        var bClear = new Button { Text = Loc.T("清理会话", "Clear chat"), Dock = DockStyle.Fill, Margin = new Padding(2),
            BackColor = Color.FromArgb(49, 50, 68), ForeColor = Color.FromArgb(205, 214, 244),
            FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold) };
        bClear.Click += async (_, _) => await ClearScope("chat");
        // 2026-08-11: 「清空任务」= 清 activePlan（用户主动放弃任务）；与取消任务(停执行保计划)/清理会话(清聊天保计划)语义区分
        var bPlan = new Button { Text = Loc.T("清空任务", "Clear plan"), Dock = DockStyle.Fill, Margin = new Padding(2),
            BackColor = Color.FromArgb(249, 226, 175), ForeColor = Color.FromArgb(30, 30, 46),
            FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold) };
        bPlan.Click += async (_, _) =>
        {
            try
            {
                var resp = await hc.PostAsync(relayUrl + "/plan/clear", new StringContent("{}", Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();
                bool cleared = false;
                try { var j = System.Text.Json.JsonDocument.Parse(body); cleared = j.RootElement.TryGetProperty("cleared", out var c) && c.GetBoolean(); } catch { }
                SetStatus(cleared ? Loc.T("● 任务计划已清空", "● Task plan cleared") : Loc.T("● 任务计划已清空（本无计划）", "● Task plan cleared (was empty)"), Color.FromArgb(249, 226, 175));
            }
            catch (SysExc) { SetStatus(Loc.T("● 清空失败", "● Clear failed"), Color.FromArgb(243, 139, 168)); }
        };
        var bMore = new Button { Text = Loc.T("更多清理", "More"), Dock = DockStyle.Fill, Margin = new Padding(2),
            BackColor = Color.FromArgb(38, 39, 56), ForeColor = Color.FromArgb(166, 227, 161),
            FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 8, FontStyle.Bold) };
        var moreMenu = new ContextMenuStrip { Font = new Font("Microsoft YaHei", 9) };
        moreMenu.Items.Add(Loc.T("清理日志（relay.log 等）", "Clean logs (relay.log etc.)"), null, async (_, _) => await ClearScope("logs"));
        moreMenu.Items.Add(Loc.T("清理缓存文件（选择集/跨图）", "Clean cache (selection / cross-drawing)"), null, async (_, _) => await ClearScope("cache"));
        moreMenu.Items.Add(Loc.T("清理 MISSING 报告", "Clean MISSING reports"), null, async (_, _) => await ClearScope("reports"));
        moreMenu.Items.Add(new ToolStripSeparator());
        moreMenu.Items.Add(Loc.T("全部清理（含任务计划，长期记忆保留）", "Clean everything (incl. plan; long-term memory kept)"), null, async (_, _) => await ClearScope("all"));
        bMore.Click += (_, _) => moreMenu.Show(bMore, new Point(0, bMore.Height));
        btnRow.Controls.Add(b, 0, 0); btnRow.Controls.Add(bCancel, 1, 0);
        btnRow.Controls.Add(bClear, 2, 0); btnRow.Controls.Add(bPlan, 3, 0); btnRow.Controls.Add(bMore, 4, 0);
        // 先加按钮行再输入框：Dock 逆序布局 → 输入框被按钮行挤到上部，按钮固定底部
        p.Controls.Add(btnRow);
        p.Controls.Add(input);

        // SplitContainer: 消息区 / 输入区 可拖动分隔（输入栏高度可拉）
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5,
            Panel1MinSize = 100, Panel2MinSize = 130, BackColor = Color.FromArgb(49, 50, 68) };
        msgs.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(msgs);
        // 2026-08-08: 配置区移到输入区上方（Panel2 内 Top），不再占对话区上口空间——
        // 对话区上口直接贴状态栏下口，彻底治“首句被遮挡”。
        BuildCfgPanel();
        var panel2 = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(49, 50, 68) };
        cfgPanel.Dock = DockStyle.Top;
        p.Dock = DockStyle.Fill;
        panel2.Controls.Add(p);
        panel2.Controls.Add(cfgPanel);
        split.Panel2.Controls.Add(panel2);
        // 2026-08-08: SplitterDistance 按剩余高度动态分配：消息区 72%，输入区+配置区至少 130。
        // 2026-08-10: Panel2MinSize 不能在构造时设 155（Width 未布局时 SplitterDistance 越界 → 面板崩溃），
        // 等 Resize 拿到真实宽度后再提升下限（防用户把输入区拖没）
        bool p2MinSet = false;
        c.Resize += (_, _) =>
        {
            try
            {
                if (!p2MinSet && split.Width > 50) { split.Panel2MinSize = 314; p2MinSet = true; }   // 2026-09-14: 配置区 200→234（加语言行）→ 下限 280→314
                var avail = split.Height - split.SplitterWidth;
                var target = (int)(avail * 0.72);
                target = Math.Max(split.Panel1MinSize, Math.Min(target, avail - 314));
                if (Math.Abs(split.SplitterDistance - target) > 4 && target > 0)
                    split.SplitterDistance = target;
            }
            catch { }
        };

        c.Controls.Add(statusBar); c.Controls.Add(split);
    }

    // 构建模型配置区（状态栏下方，可折叠显示当前模型）
    private static void BuildCfgPanel()
    {
        cfgPanel = new Panel { Dock = DockStyle.Top, Height = 234, BackColor = Color.FromArgb(38, 39, 56) };
        var font = new Font("Microsoft YaHei", 8);
        // 2026-08-17: 配置区改 5 个自适应行（TableLayoutPanel + Anchor L|R）——面板任意宽度不溢出不碰撞，按钮 AutoSize 文字完整
        var lblP = new Label { Text = Loc.T("选择模型", "Provider"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(166, 227, 161), Font = font };
        var lblM = new Label { Text = Loc.T("模型", "Model"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(166, 227, 161), Font = font };
        providerCbo = new ComboBox { Dock = DockStyle.Fill, Margin = new Padding(2), DropDownStyle = ComboBoxStyle.DropDownList, Font = font };
        modelCbo = new ComboBox { Dock = DockStyle.Fill, Margin = new Padding(2), DropDownStyle = ComboBoxStyle.DropDownList, Font = font };
        StyleCombo(providerCbo);
        StyleCombo(modelCbo);
        apiKeyBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2), UseSystemPasswordChar = true, BackColor = Color.FromArgb(30, 30, 46), ForeColor = Color.FromArgb(205, 214, 244), BorderStyle = BorderStyle.FixedSingle, Font = font, PlaceholderText = "API Key" };
        baseUrlBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2), BackColor = Color.FromArgb(30, 30, 46), ForeColor = Color.FromArgb(205, 214, 244), BorderStyle = BorderStyle.FixedSingle, Font = font, PlaceholderText = "BaseURL", Visible = false };
        var btnSave = new Button { Text = Loc.T("保存", "Save"), Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(4, 2, 4, 2),
            BackColor = Color.FromArgb(137, 180, 250), ForeColor = Color.FromArgb(30, 30, 46), FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold) };
        var btnShow = new Button { Text = Loc.T("显示", "Show"), Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(4, 2, 4, 2), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(49, 50, 68), ForeColor = Color.FromArgb(205, 214, 244), Font = new Font("Microsoft YaHei", 8) };
        // 2026-08-10: 第 4 行——AI 操作步数上限（默认 20；0 = 不限）
        var lblT = new Label { Text = Loc.T("操作上限", "Step limit"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(166, 227, 161), Font = font };
        turnsBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2), Text = "20", BackColor = Color.FromArgb(30, 30, 46), ForeColor = Color.FromArgb(205, 214, 244), BorderStyle = BorderStyle.FixedSingle, Font = font };
        var row1 = new TableLayoutPanel { Location = new Point(6, 6), Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 34, ColumnCount = 2, BackColor = Color.FromArgb(38, 39, 56) };
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row1.Controls.Add(lblP, 0, 0); row1.Controls.Add(providerCbo, 1, 0);
        var row2 = new TableLayoutPanel { Location = new Point(6, 44), Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 34, ColumnCount = 2, BackColor = Color.FromArgb(38, 39, 56) };
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row2.Controls.Add(lblM, 0, 0); row2.Controls.Add(modelCbo, 1, 0);
        var row3 = new TableLayoutPanel { Location = new Point(6, 82), Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 34, ColumnCount = 3, BackColor = Color.FromArgb(38, 39, 56) };
        row3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        row3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        row3.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row3.Controls.Add(apiKeyBox, 0, 0); row3.Controls.Add(baseUrlBox, 1, 0); row3.Controls.Add(btnShow, 2, 0);
        var row4 = new TableLayoutPanel { Location = new Point(6, 120), Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 34, ColumnCount = 4, BackColor = Color.FromArgb(38, 39, 56) };
        row4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        row4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        row4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row4.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row4.Controls.Add(lblT, 0, 0); row4.Controls.Add(turnsBox, 1, 0); row4.Controls.Add(btnSave, 3, 0);
        // project row (2026-08-17)：自适应布局——按钮 AutoSize 按文字自适应（拉宽面板文字始终完整），下拉占剩余宽度
        var projRow = new TableLayoutPanel { Location = new Point(6, 158), Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 34, ColumnCount = 5, BackColor = Color.FromArgb(38, 39, 56) };
        projRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        projRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        projRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        projRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        projRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lblProj = new Label { Text = Loc.T("\u9879\u76EE\u540D", "Project"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(166, 227, 161), Font = font };
        projectBox = new ComboBox { Dock = DockStyle.Fill, Margin = new Padding(2), DropDownStyle = ComboBoxStyle.DropDown, BackColor = Color.FromArgb(30, 30, 46), ForeColor = Color.FromArgb(205, 214, 244), FlatStyle = FlatStyle.Flat, Font = font };
        var btnOpenProj = new Button { Text = Loc.T("\u6253\u5F00", "Open"), Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(4, 2, 4, 2), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(49, 50, 68), ForeColor = Color.FromArgb(166, 227, 161), Font = new Font("Microsoft YaHei", 8) };
        var btnCreateProj = new Button { Text = Loc.T("\u5EFA\u7ACB", "Create"), Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(4, 2, 4, 2), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(137, 180, 250), ForeColor = Color.FromArgb(30, 30, 46), Font = new Font("Microsoft YaHei", 8) };
        var btnClearProj = new Button { Text = Loc.T("\u6E05\u7406", "Clear"), Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(4, 2, 4, 2), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(49, 50, 68), ForeColor = Color.FromArgb(243, 139, 168), Font = new Font("Microsoft YaHei", 8) };
        projRow.Controls.Add(lblProj, 0, 0); projRow.Controls.Add(projectBox, 1, 0);
        projRow.Controls.Add(btnOpenProj, 2, 0); projRow.Controls.Add(btnCreateProj, 3, 0); projRow.Controls.Add(btnClearProj, 4, 0);
        var tipProj = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };
        var projTip = Loc.T("\u5F53\u524D\u753B\u56FE\u9879\u76EE\u540D\n\u4E0B\u62C9\u9009\u4E2D\u5DF2\u6709\u9879\u76EE = \u76F4\u63A5\u5207\u6362\u8FC7\u53BB\uFF0C\u5404\u9879\u76EE\u6570\u636E\u4E92\u4E0D\u5E72\u6270\n\u300C\u6253\u5F00\u300D\uFF1A\u5728\u8D44\u6E90\u7BA1\u7406\u5668\u6253\u5F00\u5F53\u524D\u9879\u76EE\u6587\u4EF6\u5939\uFF08\u65E0\u9879\u76EE = \u5168\u5C40\u5DE5\u4F5C\u533A\uFF09\n\u65B0\u9879\u76EE\uFF1A\u8F93\u5165\u540D\u5B57\u2192\u70B9\u300C\u5EFA\u7ACB\u300D\u521B\u5EFA\u6587\u4EF6\u5939\u5E76\u5207\u6362\n\u300C\u6E05\u7406\u300D\uFF1A\u5220\u9664\u8BE5\u9879\u76EE\u5168\u90E8\u6570\u636E\uFF08\u4E0D\u53EF\u6062\u590D\uFF09\n\u4E0D\u586B\u4E14\u4E0D\u5EFA\u7ACB\uFF1A\u5168\u5C40\u6A21\u5F0F\uFF0C\u6240\u6709\u56FE\u7EB8\u6570\u636E\u653E\u4E00\u8D77", "Current drawing project\nPick an existing project to switch over (each project keeps its own data)\nOpen: reveal the project folder in Explorer (no project = global workspace)\nNew project: type a name, then click Create\nClear: delete all data of that project (irreversible)\nLeave empty and do not create: global mode, all drawings share one workspace");
        tipProj.SetToolTip(projectBox, projTip);
        tipProj.SetToolTip(btnOpenProj, projTip);
        tipProj.SetToolTip(btnCreateProj, projTip);
        tipProj.SetToolTip(btnClearProj, projTip);
        projectBox.GotFocus += (_, _) => tipProj.Show(projTip, projectBox, new Point(0, projectBox.Height + 4));
        projectBox.LostFocus += (_, _) => tipProj.Hide(projectBox);
        // 「打开」：资源管理器打开当前项目文件夹（2026-08-17）
        btnOpenProj.Click += async (_, _) =>
        {
            try
            {
                var resp = await hc.GetAsync(relayUrl + "/project");
                var txt = await resp.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("dir", out var dv) && !string.IsNullOrEmpty(dv.GetString()))
                {
                    var dir = dv.GetString()!;
                    if (System.IO.Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", dir);
                        SetStatus(Loc.T("\u25CF \u5DF2\u6253\u5F00: ", "● Opened: ") + dir, Color.FromArgb(166, 227, 161));
                    }
                    else SetStatus(Loc.T("\u25CF \u76EE\u5F55\u4E0D\u5B58\u5728: ", "● Directory not found: ") + dir, Color.FromArgb(249, 226, 175));
                }
                else SetStatus(Loc.T("\u25CF \u65E0\u53EF\u6253\u5F00\u7684\u9879\u76EE\u76EE\u5F55", "● No project directory to open"), Color.FromArgb(249, 226, 175));
            }
            catch (System.Exception ex) { SetStatus(Loc.T("\u25CF \u6253\u5F00\u5931\u8D25: ", "● Open failed: ") + ex.Message, Color.FromArgb(243, 139, 168)); }
        };
        btnCreateProj.Click += async (_, _) =>
        {
            var name = (projectBox.Text ?? "").Trim();
            if (name == "" || name == GlobalModeLabel)
            {
                SetStatus(Loc.T("\u25CF \u8BF7\u5148\u8F93\u5165\u9879\u76EE\u540D", "● Enter a project name first"), Color.FromArgb(249, 226, 175));
                return;
            }
            try
            {
                var payload = new System.Collections.Generic.Dictionary<string, string> { ["project"] = name };
                var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
                var resp = await hc.PostAsync(relayUrl + "/project/create", content);
                var txt = await resp.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    SetStatus(Loc.T("\u25CF \u9879\u76EE\u5DF2\u5EFA\u7ACB: ", "● Project created: ") + name, Color.FromArgb(166, 227, 161));
                    await LoadProjectListAsync();
                    projectBox.Text = name;
                }
                else SetStatus(Loc.T("\u25CF \u5EFA\u7ACB\u5931\u8D25: ", "● Create failed: ") + (doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() : Loc.T("\u672A\u77E5", "unknown")), Color.FromArgb(243, 139, 168));
            }
            catch (System.Exception ex) { SetStatus(Loc.T("\u25CF \u5EFA\u7ACB\u5931\u8D25: ", "● Create failed: ") + ex.Message, Color.FromArgb(243, 139, 168)); }
        };
        // 下拉选中已有项目 → 自动切换（2026-08-17：无需点按钮；2026-08-19: “（全局模式）”=回全局）
        projectBox.SelectedIndexChanged += async (_, _) =>
        {
            if (loadingProjects) return;
            var sel = projectBox.SelectedItem as string;
            if (string.IsNullOrEmpty(sel)) return;
            try
            {
                var projName = sel == GlobalModeLabel ? "" : sel;   // 全局模式 → 空名回全局
                var payload = new System.Collections.Generic.Dictionary<string, string> { ["project"] = projName };
                var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
                await hc.PostAsync(relayUrl + "/project", content);
                SetStatus(projName == "" ? Loc.T("\u25CF \u5DF2\u5207\u6362: \u5168\u5C40\u6A21\u5F0F\uFF08\u65E0\u9879\u76EE\uFF09", "● Switched: global mode (no project)") : Loc.T("\u25CF \u5DF2\u5207\u6362\u9879\u76EE: ", "● Switched project: ") + sel, Color.FromArgb(166, 227, 161));
            }
            catch (System.Exception ex) { SetStatus(Loc.T("\u25CF \u5207\u6362\u5931\u8D25: ", "● Switch failed: ") + ex.Message, Color.FromArgb(243, 139, 168)); }
        };
        btnClearProj.Click += async (_, _) =>
        {
            var name = (projectBox.Text ?? "").Trim();
            if (name == "" || name == GlobalModeLabel)
            {
                var mb = System.Windows.Forms.MessageBox.Show(Loc.T("\u8BF7\u5148\u5728\u4E0A\u65B9\u9009\u62E9/\u8F93\u5165\u8981\u6E05\u7406\u7684\u9879\u76EE\u540D\u3002", "Select or type the name of the project to clear above."), Loc.T("\u6E05\u7406\u9879\u76EE", "Clear project"), System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
                return;
            }
            var confirm = System.Windows.Forms.MessageBox.Show(Loc.T($"\u786E\u8BA4\u6E05\u7406\u9879\u76EE\u300C{name}\u300D\uFF1F\n\u5C06\u5220\u9664\u8BE5\u9879\u76EE\u7684\u5168\u90E8\u6570\u636E\uFF08\u9009\u62E9\u7F13\u5B58 + AI \u4E34\u65F6\u6587\u4EF6\uFF09\uFF0C\u4E0D\u53EF\u6062\u590D\u3002", $"Clear project \"{name}\"?\nAll of its data (selection cache + AI temp files) will be deleted. This cannot be undone."), Loc.T("\u6E05\u7406\u9879\u76EE", "Clear project"), System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning);
            if (confirm != System.Windows.Forms.DialogResult.Yes) return;
            try
            {
                var payload = new System.Collections.Generic.Dictionary<string, string> { ["project"] = name };
                var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
                var resp = await hc.PostAsync(relayUrl + "/project/clear", content);
                var txt = await resp.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    SetStatus(Loc.T("\u25CF \u5DF2\u6E05\u7406: ", "● Cleared: ") + name, Color.FromArgb(166, 227, 161));
                    await LoadProjectListAsync();
                    // 清理的是当前项目 → 回全局
                    projectBox.Text = "";
                }
                else SetStatus(Loc.T("\u25CF \u6E05\u7406\u5931\u8D25: ", "● Clear failed: ") + (doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() : Loc.T("\u672A\u77E5", "unknown")), Color.FromArgb(243, 139, 168));
            }
            catch (System.Exception ex) { SetStatus(Loc.T("\u25CF \u6E05\u7406\u5931\u8D25: ", "● Clear failed: ") + ex.Message, Color.FromArgb(243, 139, 168)); }
        };
        var tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };
        var turnsTip = Loc.T("AI 自动执行的操作次数上限\n0 = 不限（一直干到完成）\n中途可随时点「取消任务」停止", "Max number of AI actions per task\n0 = unlimited (keep going until done)\nYou can click \"Cancel task\" at any time");
        tip.SetToolTip(turnsBox, turnsTip);
        turnsBox.GotFocus += (_, _) => tip.Show(turnsTip, turnsBox, new Point(0, turnsBox.Height + 4));
        turnsBox.LostFocus += (_, _) => tip.Hide(turnsBox);
        btnShow.Click += (_, _) =>
        {
            apiKeyBox.UseSystemPasswordChar = !apiKeyBox.UseSystemPasswordChar;
            btnShow.Text = apiKeyBox.UseSystemPasswordChar ? Loc.T("显示", "Show") : Loc.T("隐藏", "Hide");
        };
        btnSave.Click += async (_, _) => await SaveLlmConfigAsync();
        providerCbo.SelectedIndexChanged += (_, _) =>
        {
            // 服务商切换 → 更新模型列表 + 自定义显示 baseUrl
            var tag = providerCbo.SelectedItem as KeyValuePair<string, string>? ?? default;
            var id = tag.Key;
            baseUrlBox.Visible = (id == "custom");
            modelCbo.Items.Clear();
            var models = GetModelsForProvider(id);
            foreach (var m in models) modelCbo.Items.Add(m);
            if (modelCbo.Items.Count > 0) modelCbo.SelectedIndex = 0;
        };
        // 2026-09-14: 第 6 行——界面语言（自动=跟随系统 / 中文 / English）。选择即写 config.json(locale) 并热重建面板
        var lblLang = new Label { Text = Loc.T("界面语言", "Language"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(166, 227, 161), Font = font };
        langCbo = new ComboBox { Dock = DockStyle.Fill, Margin = new Padding(2), DropDownStyle = ComboBoxStyle.DropDownList, Font = font };
        StyleCombo(langCbo);
        loadingLang = true;
        langCbo.Items.Add(new KeyValuePair<string, string>(Loc.Auto, Loc.T("自动（跟随系统）", "Automatic")));
        langCbo.Items.Add(new KeyValuePair<string, string>(Loc.Zh, "中文"));
        langCbo.Items.Add(new KeyValuePair<string, string>(Loc.En, "English"));
        langCbo.DisplayMember = "Value";
        for (int li = 0; li < langCbo.Items.Count; li++)
            if (((KeyValuePair<string, string>)langCbo.Items[li]).Key == Loc.Current) { langCbo.SelectedIndex = li; break; }
        loadingLang = false;
        langCbo.SelectedIndexChanged += async (_, _) =>
        {
            if (loadingLang) return;   // 回显/重建期间不触发
            if (langCbo.SelectedItem is not KeyValuePair<string, string> lkv) return;
            await SwitchLocaleAsync(lkv.Key);
        };
        var langRow = new TableLayoutPanel { Location = new Point(6, 196), Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 34, ColumnCount = 2, BackColor = Color.FromArgb(38, 39, 56) };
        langRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        langRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        langRow.Controls.Add(lblLang, 0, 0); langRow.Controls.Add(langCbo, 1, 0);
        cfgPanel.Controls.Add(row1); cfgPanel.Controls.Add(row2); cfgPanel.Controls.Add(row3); cfgPanel.Controls.Add(row4);
        cfgPanel.Controls.Add(projRow); cfgPanel.Controls.Add(langRow);
        // 全部子控件引用存起来，折叠时统一隐藏/显示
        cfgChildren = new System.Windows.Forms.Control[] { row1, row2, row3, row4, projRow, langRow };
    }

    private static System.Windows.Forms.Control[] cfgChildren = Array.Empty<System.Windows.Forms.Control>();
    private static bool cfgFolded = false;
    private static ComboBox? langCbo;         // 2026-09-14: 界面语言下拉（自动/中文/English）
    private static bool loadingLang = false;  // 语言下拉回显/重建期间抑制 SelectedIndexChanged

    // 折叠/展开配置区：折叠后只留一条 28px 标题条（输入区获得更多空间）
    private static void ToggleCfgPanel(Button btnFold)
    {
        cfgFolded = !cfgFolded;
        cfgPanel.Height = cfgFolded ? 28 : 234;
        foreach (var ctl in cfgChildren)
        {
            if (ctl == btnFold) continue;
            // 2026-08-08: baseUrlBox 仅 custom 服务商可见，展开时也要按原规则恢复，不能无条件显示
            if (ctl == baseUrlBox)
            {
                ctl.Visible = !cfgFolded && currentProvider == "custom";
                continue;
            }
            ctl.Visible = !cfgFolded;
        }
        btnFold.Text = cfgFolded ? "▼" : "▲";
        // 折叠后配置区里塞一条当前模型摘要，展开时移除
        var old = cfgPanel.Controls.Find("cfgSummary", false).FirstOrDefault();
        if (cfgFolded)
        {
            if (old == null)
            {
                var summary = new Label { Text = "  ⚙ " + (string.IsNullOrEmpty(currentModel) ? Loc.T("未配置", "Not configured") : currentModel), Location = new Point(8, 4), Size = new Size(300, 20),
                    ForeColor = Color.FromArgb(166, 227, 161), Font = new Font("Microsoft YaHei", 8) };
                summary.Name = "cfgSummary";
                cfgPanel.Controls.Add(summary);
            }
            else old.Text = "  ⚙ " + (string.IsNullOrEmpty(currentModel) ? Loc.T("未配置", "Not configured") : currentModel);
        }
        else if (old != null) cfgPanel.Controls.Remove(old);
    }

    // 深色主题 ComboBox：主区 Flat 深色 + 下拉列表自绘深色（避免白色系统控件）
    private static void StyleCombo(ComboBox cbo)
    {
        cbo.FlatStyle = FlatStyle.Flat;
        cbo.BackColor = Color.FromArgb(30, 30, 46);
        cbo.ForeColor = Color.FromArgb(205, 214, 244);
        cbo.DrawMode = DrawMode.OwnerDrawFixed;
        cbo.DrawItem += (s, e) =>
        {
            if (e.Index < 0) return;
            var sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var bg = new SolidBrush(sel ? Color.FromArgb(137, 180, 250) : Color.FromArgb(30, 30, 46)))
                e.Graphics.FillRectangle(bg, e.Bounds);
            var txt = cbo.GetItemText(cbo.Items[e.Index]);
            TextRenderer.DrawText(e.Graphics, txt, cbo.Font, e.Bounds,
                sel ? Color.FromArgb(30, 30, 46) : Color.FromArgb(205, 214, 244),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }

    private static System.Collections.Generic.List<string> GetModelsForProvider(string id)
    {
        foreach (var kv in llmPresets)
            if (kv.Key == id) return kv.Value ?? new System.Collections.Generic.List<string>();
        return new System.Collections.Generic.List<string>();
    }

    private static System.Collections.Generic.List<KeyValuePair<string, System.Collections.Generic.List<string>>> llmPresets = new();
    private static string currentProvider = "";
    private static string currentModel = "";
    private static string currentKey = "";
    private static string currentBaseUrl = "";

    // 拉取服务商预设 + 当前配置（启动时异步）
    private static async System.Threading.Tasks.Task LoadLlmOptionsAsync()
    {
        try
        {
            var resp = await hc.GetAsync(relayUrl + "/llm/options");
            if (!resp.IsSuccessStatusCode) return;
            var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("presets", out var presets)) return;
            llmPresets.Clear();
            providerCbo.Items.Clear();
            foreach (var pr in presets.EnumerateArray())
            {
                var id = pr.GetProperty("id").GetString() ?? "";
                var name = pr.GetProperty("name").GetString() ?? id;
                var models = new System.Collections.Generic.List<string>();
                if (pr.TryGetProperty("models", out var mArr))
                    foreach (var m in mArr.EnumerateArray()) models.Add(m.GetString() ?? "");
                llmPresets.Add(new KeyValuePair<string, System.Collections.Generic.List<string>>(id, models));
                providerCbo.Items.Add(new KeyValuePair<string, string>(id, name));
            }
            providerCbo.DisplayMember = "Value";
            // 回显当前配置
            if (doc.RootElement.TryGetProperty("current", out var cur) && cur.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                currentProvider = cur.GetProperty("provider").GetString() ?? "";
                currentModel = cur.GetProperty("model").GetString() ?? "";
                currentKey = "";  // key 不回显（安全）
                currentBaseUrl = cur.GetProperty("baseUrl").GetString() ?? "";
                for (int i = 0; i < providerCbo.Items.Count; i++)
                    if (((KeyValuePair<string, string>)providerCbo.Items[i]).Key == currentProvider) { providerCbo.SelectedIndex = i; break; }
                if (modelCbo.Items.Count > 0)
                    for (int i = 0; i < modelCbo.Items.Count; i++)
                        if ((string)modelCbo.Items[i] == currentModel) { modelCbo.SelectedIndex = i; break; }
                apiKeyBox.Text = "";
                baseUrlBox.Text = currentBaseUrl;
                // 2026-08-10: 回显轮数上限（0=无限；旧 relay 无此字段则保持默认 20）
                if (cur.TryGetProperty("maxTurns", out var mt))
                    turnsBox.Text = mt.ValueKind == System.Text.Json.JsonValueKind.Number ? mt.GetInt32().ToString() : (mt.GetString() ?? "20");

                // project echo (2026-08-17)
                try
                {
                    var pr = await hc.GetAsync(relayUrl + "/project");
                    var pt = await pr.Content.ReadAsStringAsync();
                    var pj = System.Text.Json.JsonDocument.Parse(pt);
                    if (pj.RootElement.TryGetProperty("project", out var pv))
                        projectBox.Text = pv.GetString() ?? "";
                }
                catch { }
                // 项目列表（2026-08-17：下拉可编辑，选已有/输新名）
                try { await LoadProjectListAsync(); } catch { }
            }
            cfgLoaded = true;
        }
        catch { }
    }

    // 加载已有项目列表到下拉（2026-08-17：GET /projects）
    private static async System.Threading.Tasks.Task LoadProjectListAsync()
    {
        loadingProjects = true;
        try
        {
            var keep = projectBox.Text ?? "";
            projectBox.Items.Clear();
            projectBox.Items.Add(GlobalModeLabel);   // 2026-08-19: 固定项——无项目普通模式
            var resp = await hc.GetAsync(relayUrl + "/projects");
            var txt = await resp.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(txt);
            if (doc.RootElement.TryGetProperty("projects", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    var s = it.GetString();
                    if (!string.IsNullOrEmpty(s)) projectBox.Items.Add(s);
                }
            }
            // 当前无项目 → 显示“（全局模式）”；否则回显当前项目名
            projectBox.Text = string.IsNullOrEmpty(keep) ? GlobalModeLabel : keep;
        }
        finally { loadingProjects = false; }
    }

    // 下拉选中已有项目 → 自动切换（2026-08-17：无需点按钮）
    private static bool loadingProjects = false;
    private static string GlobalModeLabel => Loc.T("（全局模式）", "(Global mode)");   // 2026-08-19: 下拉固定项=无项目普通模式

    // 保存模型+key → POST /llm/config（热更新）
    private static async System.Threading.Tasks.Task SaveLlmConfigAsync()
    {
        if (providerCbo.SelectedItem is not KeyValuePair<string, string> prov) { SetStatus(Loc.T("● 请选服务商", "● Select a provider"), Color.FromArgb(249, 226, 175)); return; }
        var model = modelCbo.SelectedItem as string;
        if (string.IsNullOrEmpty(model)) { SetStatus(Loc.T("● 请选模型", "● Select a model"), Color.FromArgb(249, 226, 175)); return; }
        var key = apiKeyBox.Text.Trim();
        // 2026-08-10: key 可为空——仅改轮数等配置时无需重填（relay 端: 空 key 且无历史配置才报错）
        // 2026-08-10: 轮数校验（空=不改；需 ≥0 整数；0=无限）
        var turnsText = turnsBox.Text.Trim();
        int tv = 20;
        if (turnsText != "")
        {
            if (!int.TryParse(turnsText, out tv) || tv < 0)
            { SetStatus(Loc.T("● 上限需为 ≥0 的数字", "● Limit must be a number ≥ 0"), Color.FromArgb(249, 226, 175)); return; }
        }
        var payload = new System.Collections.Generic.Dictionary<string, string>
        {
            ["provider"] = prov.Key,
            ["model"] = model,
            ["baseUrl"] = prov.Key == "custom" ? baseUrlBox.Text.Trim() : GetBaseUrlFor(prov.Key)
        };
        if (!string.IsNullOrEmpty(key)) payload["apiKey"] = key;
        if (turnsText != "") payload["maxTurns"] = tv.ToString();
        try
        {
            var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            var resp = await hc.PostAsync(relayUrl + "/llm/config", content);

            // project save (2026-08-17)
            try
            {
                var projText = (projectBox.Text ?? "").Trim();
                var projPayload = new System.Collections.Generic.Dictionary<string, string> { ["project"] = projText == GlobalModeLabel ? "" : projText };
                var projContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(projPayload), System.Text.Encoding.UTF8, "application/json");
                await hc.PostAsync(relayUrl + "/project", projContent);
            }
            catch { }
            var txt = await resp.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(txt);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                SetStatus(Loc.T("● 已保存: ", "● Saved: ") + (doc.RootElement.TryGetProperty("model", out var mdl) ? mdl.GetString() : prov.Key + "/" + model), Color.FromArgb(166, 227, 161));
                apiKeyBox.Text = "";  // 保存后清空 key（不回显）
            }
            else SetStatus(Loc.T("● 保存失败: ", "● Save failed: ") + (doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() : Loc.T("未知", "unknown")), Color.FromArgb(243, 139, 168));
        }
        catch (System.Exception e) { SetStatus(Loc.T("● 保存失败: ", "● Save failed: ") + e.Message, Color.FromArgb(243, 139, 168)); }
    }

    private static string GetBaseUrlFor(string id)
    {
        return GetPresetBaseUrl(id);
    }

    private static string GetPresetBaseUrl(string id)
    {
        // 服务商默认 baseUrl（与 relay LLM_PRESETS 一致；custom 用输入框）
        switch (id)
        {
            case "deepseek": return "https://api.deepseek.com/v1";
            case "openai": return "https://api.openai.com/v1";
            case "qwen": return "https://dashscope.aliyuncs.com/compatible-mode/v1";
            case "kimi": return "https://api.moonshot.cn/v1";
            case "zhipu": return "https://open.bigmodel.cn/api/paas/v4";
            case "minimax": return "https://api.minimax.chat/v1";
            case "openrouter": return "https://openrouter.ai/api/v1";
            case "xai": return "https://api.x.ai/v1";
            default: return "";
        }
    }

    private static void SetStatus(string text, Color color)
    {
        // 2026-08-12 M4: 控件句柄销毁后 Invoke 抛异常(定时器线程未处理异常→进程崩)——加 IsHandleCreated 保护
        if (statusLbl == null || !statusLbl.IsHandleCreated) return;
        try
        {
            statusLbl.Invoke(new Action(() => {
                if (statusLbl == null || statusLbl.IsDisposed) return;
                statusLbl.Text = "  " + text;
                statusLbl.ForeColor = color;
            }));
        }
        catch { }
    }

    private static void Add(string who, string t, string? ts = null)
    {
        var prefix = who == "me" ? " \u25b6 " : who == "hank" ? " \u25c0 " : "  ";
        // 时间: 优先传入的（relay 返回的 AI 完成时刻），否则本地当前时刻（用户发送时刻）
        var timeStr = ts ?? DateTime.Now.ToString("HH:mm:ss");
        // 2026-08-08: 首条也加前导换行——"汉克就绪"下移一行，避开状态栏下沿的 1px 裁切
        msgs.AppendText("\n");
        // 时间戳用灰色独立着色，和正文区分（RichTextBox 按 SelectionColor 分段着色）
        var tsColor = who == "me" ? Color.FromArgb(137, 180, 250) : Color.FromArgb(147, 153, 178);
        msgs.SelectionColor = tsColor;
        msgs.AppendText("[" + timeStr + "]");
        msgs.SelectionColor = who == "me" ? Color.FromArgb(249, 226, 175) : Color.FromArgb(205, 214, 244);
        msgs.AppendText(prefix + t);
        msgs.SelectionStart = msgs.TextLength;
        msgs.ScrollToCaret();
    }

    private static void AddSafe(string who, string t, string? ts = null)
    {
        if (msgs != null && msgs.IsHandleCreated)
            msgs.Invoke(new Action(() => Add(who, t, ts)));
    }

    // ISO 时间 → HH:mm:ss（本地时区）
    private static string? FormatRelayTime(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        try { return DateTime.Parse(iso).ToLocalTime().ToString("HH:mm:ss"); }
        catch { return null; }
    }

    private static void StartTimers()
    {
        // 防重：重复 Show（Hide 后再 Show）不再叠加创建 Timer，旧 Timer 先停并释放
        StopTimers();

        statusTimer = new System.Timers.Timer(10000);
        statusTimer.Elapsed += (_, _) => CheckRelay();
        statusTimer.AutoReset = true;
        statusTimer.Start();

        // 2026-08-12: SSE 实时回复（替代 2s 轮询 /replies，断线 3s 自动重连）
        StartRepliesSse();
    }

    // 2026-08-12 M4: 插件卸载时调用——停 Timer + SSE
    public static void StopForUnload()
    {
        try { StopTimers(); } catch { }
        try { StopRepliesSse(); } catch { }
    }

    private static void StopTimers()
    {
        StopRepliesSse();
        if (statusTimer != null)
        {
            statusTimer.Stop();
            statusTimer.Dispose();
            statusTimer = null!;
        }
    }

    private static void CheckRelay()
    {
        try
        {
            // 修复: 改用 /health 只查状态，不再 GET /replies（/replies 是取走即清空，
            // 状态检查会静默吞掉 AI 回复导致面板"发了没反应"）
            var resp = hc.GetAsync(relayUrl + "/status").Result;
            if (resp.IsSuccessStatusCode)
            {
                var json = resp.Content.ReadAsStringAsync().Result;
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var state = doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() : "";
                var mode = doc.RootElement.TryGetProperty("mode", out var md) ? md.GetString() : "";
                var model = doc.RootElement.TryGetProperty("model", out var ml) ? ml.GetString() : "";
                var cad = doc.RootElement.TryGetProperty("cadSession", out var cs) && cs.GetBoolean();
                var progress = doc.RootElement.TryGetProperty("progress", out var pg) ? pg.GetString() : "";
                // 状态栏: ● 在线 | 模式/模型 | CAD ｜实时进度
                var label = "●" + (state == "thinking" ? Loc.T("思考中", "Thinking") : state == "queued" ? Loc.T("排队中", "Queued") : Loc.T("在线", "Online"));
                if (!string.IsNullOrEmpty(mode)) label += " | " + mode + (string.IsNullOrEmpty(model) ? "" : "/" + model);
                if (cad) label += " | CAD";
                statusBase = label;
                progressText = progress ?? "";   // 轮询只作兜底；正常由 SSE progress 事件实时刷新
                RenderStatus();
            }
            else SetStatus(Loc.T("● 异常", "● Error"), Color.FromArgb(249, 226, 175));
        }
        catch { SetStatus(Loc.T("● 离线", "● Offline"), Color.FromArgb(243, 139, 168)); }
    }

    /// <summary>状态栏渲染：base + ｜进度（进度非空时用黄色高亮）。可在任意线程调用（SetStatus 内部 Invoke 回 UI 线程）。</summary>
    private static void RenderStatus()
    {
        var label = string.IsNullOrEmpty(statusBase) ? "●" : statusBase;   // base 未就绪时别只画半截
        if (!string.IsNullOrEmpty(progressText))
        {
            label += " ｜" + (progressText.Length > 18 ? progressText.Substring(0, 18) + "…" : progressText);
            SetStatus(label, Color.FromArgb(249, 226, 175));
        }
        else SetStatus(label, Color.FromArgb(166, 227, 161));
    }

    // 2026-08-12: SSE 实时回复通道（替代 2s 轮询 /replies）
    // relay /replies/stream: 连接即重放积压回复，之后实时推送；30s heartbeat 保活；断线 3s 自动重连
    private static void StartRepliesSse()
    {
        StopRepliesSse();
        sseCts = new System.Threading.CancellationTokenSource();
        var ct = sseCts.Token;
        _ = Task.Run(() => RepliesSseLoop(ct), ct);
    }

    private static void StopRepliesSse()
    {
        var cts = sseCts;
        sseCts = null;
        if (cts != null) { cts.Cancel(); cts.Dispose(); }
    }

    private static async Task RepliesSseLoop(System.Threading.CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, relayUrl + "/replies/stream");
                using var resp = await sseHc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? evt = null;
                var data = new System.Text.StringBuilder();
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break; // 连接关闭 → 外层重连
                    if (line.Length == 0)
                    {
                        // 事件结束
                        if (evt == "reply" && data.Length > 0)
                        {
                            try
                            {
                                var doc = System.Text.Json.JsonDocument.Parse(data.ToString());
                                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                                var time = doc.RootElement.TryGetProperty("time", out var tm) ? FormatRelayTime(tm.GetString()) : null;
                                if (!string.IsNullOrEmpty(text))
                                {
                                    AddSafe("hank", text, time);
                                    SetStatus(Loc.T("● 有回复", "● Replied"), Color.FromArgb(166, 227, 161));
                                }
                            }
                            catch { }
                        }
                        // 2026-09-14: relay 实时进度（空字符串 = 任务结束，清进度）
                        else if (evt == "progress" && data.Length > 0)
                        {
                            try
                            {
                                var doc = System.Text.Json.JsonDocument.Parse(data.ToString());
                                progressText = doc.RootElement.TryGetProperty("text", out var pt) ? pt.GetString() ?? "" : "";
                                RenderStatus();
                            }
                            catch { }
                        }
                        evt = null;
                        data.Clear();
                        continue;
                    }
                    if (line.StartsWith(":")) continue; // 心跳/注释行
                    if (line.StartsWith("event:")) { evt = line.Substring(6).Trim(); continue; }
                    if (line.StartsWith("data:")) { data.Append(line.Substring(5).Trim()); data.Append('\n'); continue; }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
            // 断线重连：等 3s
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    private static async System.Threading.Tasks.Task RestartRelayAsync()
    {
        SetStatus(Loc.T("● 重启中...", "● Restarting..."), Color.FromArgb(249, 226, 175));
        try
        {
            var resp = await hc.PostAsync(relayUrl + "/restart", new StringContent("{}", Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode)
            {
                // relay 退出后 launcher 3s 自动拉起；等它恢复（最多 ~12s）
                for (int i = 0; i < 6; i++)
                {
                    await System.Threading.Tasks.Task.Delay(2000);
                    try
                    {
                        var s = await hc.GetAsync(relayUrl + "/status");
                        if (s.IsSuccessStatusCode) { SetStatus(Loc.T("● 已重启", "● Restarted"), Color.FromArgb(166, 227, 161)); return; }
                    }
                    catch { }
                }
            }
        }
        catch (SysExc) { }
        // relay 彻底无响应 → 本地拉起 launcher（守护）
        try
        {
            // 2026-08-14: 不再硬编码安装路径——通过计划任务拉起守护 launcher（install.ps1 注册的
            // AcBridge-Relay 指向当前安装目录的 wscript+vbs，任意安装路径都正确）
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = "/Run /TN AcBridge-Relay",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            SetStatus(Loc.T("● 守护进程已拉起", "● Daemon started"), Color.FromArgb(166, 227, 161));
        }
        catch (SysExc ex) { SetStatus(Loc.T("● 重启失败: ", "● Restart failed: ") + ex.Message, Color.FromArgb(243, 139, 168)); }
    }

    /// <summary>端口状态诊断对话框（2026-08-13 P3-B）：插件端口从 PluginRuntime（P0 可配置化），relay/mcp 从 config.json。</summary>
    private static void ShowPortStatus()
    {
        try
        {
            var bridge = PluginRuntime.Port;
            var (relay, mcp) = ReadServicePorts();
            var root = LocateProjectRoot();
            using var form = new PortStatusForm(bridge, relay, mcp, root);
            form.ShowDialog();
        }
        catch { }
    }

    private static (int relay, int mcp) ReadServicePorts()
    {
        int relay = 19876, mcp = 3000;
        try
        {
            var root = LocateProjectRoot();
            var cfg = Path.Combine(root, "config.json");
            if (File.Exists(cfg))
            {
                var jo = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfg)) as System.Text.Json.Nodes.JsonObject;
                if (jo != null)
                {
                    if (int.TryParse(PluginRuntime.JsonNodeToString(jo["relayPort"]), out var r) && r > 0) relay = r;
                    if (int.TryParse(PluginRuntime.JsonNodeToString(jo["mcpPort"]), out var m) && m > 0) mcp = m;
                }
            }
        }
        catch { }
        return (relay, mcp);
    }

    private static string LocateProjectRoot()
    {
        try
        {
            var asmDir = Path.GetDirectoryName(typeof(PluginRuntime).Assembly.Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                var root = Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                if (Directory.Exists(root)) return root;
            }
        }
        catch { }
        return Path.GetDirectoryName(typeof(PluginRuntime).Assembly.Location) ?? "";
    }

    private static async Task ClearScope(string scope)
    {
        try
        {
            var json = "{\"scope\":\"" + scope + "\"}";
            await hc.PostAsync(relayUrl + "/clear", new StringContent(json, Encoding.UTF8, "application/json"));
            if (scope == "chat") { msgs.Clear(); Add("", Loc.T("对话已清空", "Chat cleared")); }
            SetStatus(Loc.T("● 已清理: ", "● Cleared: ") + scope, Color.FromArgb(166, 227, 161));
        }
        catch (SysExc) { SetStatus(Loc.T("● 清理失败", "● Clear failed"), Color.FromArgb(243, 139, 168)); }
    }

    private static async void Send()
    {
        if (string.IsNullOrWhiteSpace(input.Text)) return;
        var msg = input.Text.Trim(); input.Clear(); Add("me", msg);
        waitSeq++;
        var mySeq = waitSeq;
        SetStatus(Loc.T("● 发送中...", "● Sending..."), Color.FromArgb(249, 226, 175));
        try
        {
            // 发送前先捕获当前选择集：钉住“用户发指令这一刻”的选择。
            // 必须 await 而非 .Wait()：Wait() 阻塞 UI 线程 → 主线程消息循环停转 →
            // 插件文档队列（靠 Idle 驱动）永远不处理 → 死锁卡死 C3D（2026-08-05 踩坑）
            try { await AcadCommands.SaveSelectionAsync(null); }
            catch { } // 捕获失败不阻塞发送

            var json = "{\"message\":\"" + System.Text.Json.JsonEncodedText.Encode(msg) + "\"}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await hc.PostAsync(relayUrl + "/send", content);
            SetStatus(Loc.T("● 已发送", "● Sent"), Color.FromArgb(166, 227, 161));
        }
        catch (SysExc ex) { Add("", "ERR: " + ex.Message); SetStatus(Loc.T("● 发送失败", "● Send failed"), Color.FromArgb(243, 139, 168)); }
    }
}
