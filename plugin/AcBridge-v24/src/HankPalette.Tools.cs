using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Civil3DMcpPlugin;

// 2026-10-21: 面板「工具」页签 —— 调用流 + 方法目录。
// 数据来源：relay 的 SSE `event: tool`（llm-agent 每次 cadCall/mcpCall 发 start/end 两拍）。
// 设计约束（都是踩过的雷）：
//   1) UI 线程只做增量落表，事件解析在 SSE 线程完成 → 用 ConcurrentQueue 过手，Timer 200ms 合并刷新
//   2) 绝不在 UI 线程 .Wait()/.Result（会锁死 CAD）
//   3) 只显示摘要：方法/耗时/字节/错误码。结果体永远不进 UI
//   4) 表格列宽用 Fill + 字号 8，窄面板（320px）下不固定列宽
public static partial class HankPalette
{
    private sealed class ToolEventRow
    {
        public long Seq;
        public DateTime At;
        public string Phase = "";
        public string Method = "";
        public string Via = "";
        public long Ms;
        public long Bytes;
        public bool Ok = true;
        public string Err = "";
    }

    private sealed class MethodRow
    {
        public string Name = "";
        public string Desc = "";
        public int Calls;
        public long LastMs;
        public int Fails;
        public string LastErr = "";
    }

    private static readonly List<ToolEventRow> toolRows = new();
    private static readonly System.Collections.Generic.Dictionary<string, MethodRow> methodRows =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<ToolEventRow> toolInbox = new();

    private static System.Windows.Forms.Timer toolsTimer;
    // 2026-10-21: 用 3 行表格布局，彻底避免 Dock 叠在一起   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
    private static TableLayoutPanel toolsView;
    private static DataGridView toolsGrid;
    private static TextBox toolsSearch;
    private static CheckBox toolsFailedOnly;
    private static Label toolsCount;
    private static Button toolsToggleBtn;
    private static Button toolsModeBtn;
    private static bool toolsCatalogMode;
    private static bool toolsDirty;
    private static bool toolsFirstOpen;
    private static Button toolsUndoBtn;   // first show in this session -> open the records view
    private static bool toolsCatalogLoading;

    // ---------------- 构建 ----------------
    private static void BuildToolsView(Control host)
    {
        // 2026-10-21: 防重入——面板重建时旧 toolsView 必须先摘掉，否则每开关一次多一层
        if (toolsView != null && !toolsView.IsDisposed)
        {
            try { host.Controls.Remove(toolsView); toolsView.Dispose(); } catch (System.Exception ex) { PluginLog.Warn("Palette", "dispose old tools view: " + ex.Message); }
        }
        // 2026-10-21: 之前用 Panel + Dock(Top/Fill/Bottom) 三个兄弟控件，实机出现"最上面一行被状态栏压住"。
        // Dock 布局在嵌套容器里会互相抢空间，改成 TableLayoutPanel 固定三行 —— 行不重叠，数学上不可能盖住。
        toolsView = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Visible = false,
            BackColor = Color.FromArgb(30, 30, 46),
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0, 2, 0, 0),
        };
        toolsView.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        // 2026-10-21: 行高改成 AutoSize —— 固定像素在 160% DPI 下会被放大后的字号顶破（按钮字被切）。
        // 工具条高度在下面按 PreferredSize 重算   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
        toolsView.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
        // 表格   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
        toolsView.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        // 计数条   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
        toolsView.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 2026-10-21: 不要用 AutoSize + Dock=Top 的组合 —— 它会和外层 AutoSize 行互相触发布局循环，
        // 把 UI 线程占死（实测表现：CAD 永远"不空闲"，插件全部操作超时）。
        // 行高改成构建时按控件首选高度算一次（PreferredSize 已含 DPI 缩放）。
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            Padding = new Padding(4, 3, 4, 2),
            Margin = new Padding(0),
            BackColor = Color.FromArgb(49, 50, 68),
        };
        // 2026-10-21: 按钮列用 AutoSize（按控件首选宽度），不用固定像素 ——
        // 固定像素不跟随系统 DPI 缩放，160% 下字体变大就会把"方法目录"切掉。
        // 搜索框吃掉剩余   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // 导出（仅工作流视图可见）   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        toolsSearch = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 4, 3),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(30, 30, 46),
            ForeColor = Color.FromArgb(205, 214, 244),
            Font = new Font("Microsoft YaHei", 8),
            PlaceholderText = Loc.T("搜索方法名…", "Search method…"),
        };
        toolsSearch.TextChanged += (_, _) => { toolsDirty = true; RefreshToolsGrid(); };

        toolsFailedOnly = new CheckBox
        {
            Text = Loc.T("只看失败", "Failed"),
            AutoSize = true,
            Margin = new Padding(4, 6, 4, 6),
            ForeColor = Color.FromArgb(243, 139, 168),
            Font = new Font("Microsoft YaHei", 8),
        };
        toolsFailedOnly.CheckedChanged += (_, _) => { toolsDirty = true; RefreshToolsGrid(); };

        toolsModeBtn = new Button
        {
            Text = Loc.T("方法目录", "Catalog"),
            AutoSize = true,
            Margin = new Padding(2, 4, 2, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(49, 50, 68),
            ForeColor = Color.FromArgb(137, 180, 250),
            Font = new Font("Microsoft YaHei", 8, FontStyle.Bold),
        };
        toolsModeBtn.Click += (_, _) =>
        {
            // 2026-10-21 P2.4: 三个视图循环 —— 调用流 → 方法目录 → 工作流（记录）
            LeaveJournalMode();
            if (toolsAssistedActive) { SetToolsHint(Loc.T("\u5148\u300c\u505c\u6b62\u300d\u5f53\u524d\u56de\u653e\u518d\u5207\u89c6\u56fe", "Stop the replay before switching views")); return; }
            if (toolsBindingActive) { SetToolsHint(Loc.T("\u5148\u5b8c\u6210\u6216\u53d6\u6d88\u5f53\u524d\u7ed1\u5b9a", "Finish or cancel the binding first")); return; }
            if (toolsWorkflowMode) { toolsWorkflowMode = false; toolsCatalogMode = true; }
            else if (toolsCatalogMode) { toolsCatalogMode = false; toolsWorkflowMode = true; }
            else { toolsCatalogMode = true; }
            UpdateToolsModeButton();
            RebuildToolsColumns();
            if (toolsCatalogMode) EnsureCatalogLoaded();
            if (toolsWorkflowMode) EnsureWorkflowsLoaded(true);
            RefreshToolsGrid();
            UpdateToolsCount();
        };

        var toolsClear = new Button
        {
            Text = Loc.T("清空", "Clear"),
            AutoSize = true,
            Margin = new Padding(2, 4, 2, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(49, 50, 68),
            ForeColor = Color.FromArgb(205, 214, 244),
            Font = new Font("Microsoft YaHei", 8),
        };
        // 2026-10-21: 工作流视图里"清空"没有意义（记录在磁盘上），但别的视图里它必须清会话计数。
        // 同一个按钮按视图换语义 —— 否则就是"点了没反应的死按钮"（Bro 实测反馈）。
        toolsClear.Click += (_, _) =>
        {
            // 真正清掉记录（移到 _trash，可恢复）   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
            if (toolsAssistedActive) { StopAssisted(); return; }
            if (toolsBindingActive) { StopBinding(); return; }
            // 2026-10-23 (Bro: the stale journal rows could not be cleared): in the journal view
            // "Clear" used to reset the tool-event history, which is not what that table shows.
            // It now prunes the entries that can never be reverted (expired / no inverse /
            // unsupported). Reversible entries stay -- clearing them would throw the undo away.
            if (toolsJournalMode) PruneJournal("blocked");
            else if (toolsWorkflowMode) ClearWorkflowsClicked();
            else ResetToolHistory();
        };
        toolsClearBtn = toolsClear;

        // 2026-10-21 P2.4: 按钮上写"当前视图"，三态循环时用户不会猜错（旧版写的是"下一个"）。
        UpdateToolsModeButton();

        // 2026-10-21: 导出做成可见按钮，不藏在右键菜单里 —— 右键菜单在自动化里点不到，
        // 用户也未必会去右键。只在工作流视图显示。
        toolsWfExportBtn = new Button
        {
            Text = Loc.T("导出", "Export"),
            AutoSize = true,
            Visible = false,
            Margin = new Padding(2, 4, 2, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(49, 50, 68),
            ForeColor = Color.FromArgb(166, 227, 161),
            Font = new Font("Microsoft YaHei", 8),
        };
        toolsWfExportBtn.Click += (_, _) => ExportWorkflow(SelectedWorkflow() ?? FirstWorkflowRow());

        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // 2026-10-22 P2.3: one button, two meanings -- start assisted replay, then approve each step.
        toolsWfPlayBtn = new Button
        {
            Text = Loc.T("\u56de\u653e", "Replay"),
            AutoSize = true,
            Visible = false,
            Margin = new Padding(2, 4, 2, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(49, 50, 68),
            ForeColor = Color.FromArgb(249, 226, 175),
            Font = new Font("Microsoft YaHei", 8, FontStyle.Bold),
        };
        toolsWfPlayBtn.Click += (_, _) =>
        {
            if (toolsAssistedActive) ApproveNextStep();
            else if (toolsBindingActive) SubmitBindings();
            else StartAssistedFlow(SelectedWorkflow() ?? FirstWorkflowRow());
        };

        // 2026-10-22: revert the newest change made through the plugin (per-action journal).
        toolsUndoBtn = new Button
        {
            Text = Loc.T("\u64a4\u56de\u4e0a\u6b21", "Undo last"),
            AutoSize = true,
            Visible = false,
            Margin = new Padding(2, 4, 2, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(49, 50, 68),
            ForeColor = Color.FromArgb(243, 139, 168),
            Font = new Font("Microsoft YaHei", 8),
        };
        toolsUndoBtn.Click += (_, _) => { if (toolsJournalMode) RevertSelectedJournalRow(); else OpenJournalView(); };

        bar.Controls.Add(toolsSearch, 0, 0);
        bar.Controls.Add(toolsFailedOnly, 1, 0);
        bar.Controls.Add(toolsModeBtn, 2, 0);
        bar.Controls.Add(toolsWfPlayBtn, 3, 0);
        bar.Controls.Add(toolsWfExportBtn, 4, 0);
        bar.Controls.Add(toolsClear, 5, 0);
        bar.Controls.Add(toolsUndoBtn, 6, 0);

        toolsCount = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = Color.FromArgb(38, 39, 56),
            ForeColor = Color.FromArgb(108, 112, 134),
            Font = new Font("Microsoft YaHei", 7),
            Text = "",
        };

        toolsGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 22,
            BackgroundColor = Color.FromArgb(30, 30, 46),
            BorderStyle = BorderStyle.None,
            Margin = new Padding(0),
            GridColor = Color.FromArgb(49, 50, 68),
            EnableHeadersVisualStyles = false,
            Font = new Font("Microsoft YaHei", 8),
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 30, 46),
                ForeColor = Color.FromArgb(205, 214, 244),
                SelectionBackColor = Color.FromArgb(49, 50, 68),
                SelectionForeColor = Color.FromArgb(249, 226, 175),
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(38, 39, 56),
                ForeColor = Color.FromArgb(166, 227, 161),
                Font = new Font("Microsoft YaHei", 8, FontStyle.Bold),
            },
        };

        toolsView.Controls.Add(bar, 0, 0);
        toolsView.Controls.Add(toolsGrid, 0, 1);
        toolsView.Controls.Add(toolsCount, 0, 2);
        host.Controls.Add(toolsView);
        // 2026-10-21: 与消息区互换（同一个容器内），不抢整块面板   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
        toolsView.BringToFront();

        // 2026-10-21: 行高 = 最高控件首选高度 + 上下边距（14）。DPI 160% 下按钮首选高度会自己变大，
        // 这里跟着一起变，不会再出现"字被切"或"压到表格"。
        int barNeed = Math.Max(toolsSearch.PreferredSize.Height, Math.Max(toolsModeBtn.PreferredSize.Height, Math.Max(toolsFailedOnly.PreferredSize.Height, toolsClear.PreferredSize.Height)));
        toolsView.RowStyles[0].Height = Math.Max(30f, barNeed + 14f);
        bar.Height = (int)toolsView.RowStyles[0].Height;
        PluginLog.Info("Palette", "tools row height = " + (int)toolsView.RowStyles[0].Height + " (barNeed=" + barNeed + ")");

        // 双击回放 / 右键导出（只在工作流视图生效）   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
        AttachWorkflowGridEvents();
        RebuildToolsColumns();
        UpdateToolsCount();

        // 2026-10-21: 一次性布局探针 —— 按钮/表格"碰撞"这类问题必须能被日志量出来，不靠眼睛看截图。
        bool toolsProbed = false;
        toolsView.Layout += (_, _) =>
        {
            if (toolsProbed || toolsGrid == null || toolsGrid.IsDisposed || toolsView.Height < 120 || toolsView.Width < 200) return;
            toolsProbed = true;
            var need = toolsModeBtn.PreferredSize.Width;
            PluginLog.Info("Palette", "tools layout probe: barH=" + bar.Height + " barBottom=" + bar.Bottom +
                " gridTop=" + toolsGrid.Top + " gridH=" + toolsGrid.Height +
                " viewW=" + toolsView.Width + " viewH=" + toolsView.Height +
                " modeW=" + toolsModeBtn.Width + "/need " + need +
                (toolsModeBtn.Width < need ? " CLIPPED" : " ok"));
            PluginLog.Info("Palette", "tools toolbar probe: searchW=" + toolsSearch.Width + (toolsSearch.Width < 60 ? " SEARCH-SQUEEZED" : " ok"));
        };
    }

    private static void UpdateToolsModeButton()
    {
        if (toolsModeBtn == null || toolsModeBtn.IsDisposed) return;
        toolsModeBtn.Text = toolsWorkflowMode
            ? Loc.T("工作流", "Workflows")
            : (toolsCatalogMode ? Loc.T("方法目录", "Catalog") : Loc.T("调用流", "Stream"));
        toolsSearch.PlaceholderText = toolsWorkflowMode
            ? Loc.T("搜索任务…", "Search task…")
            : Loc.T("搜索方法名…", "Search method…");
        if (toolsFailedOnly != null && !toolsFailedOnly.IsDisposed) toolsFailedOnly.Visible = !toolsWorkflowMode;
        if (toolsWfExportBtn != null && !toolsWfExportBtn.IsDisposed) toolsWfExportBtn.Visible = toolsWorkflowMode && !toolsAssistedActive;
        if (toolsUndoBtn != null && !toolsUndoBtn.IsDisposed)
            toolsUndoBtn.Visible = toolsJournalMode || (!toolsWorkflowMode && !toolsCatalogMode && !toolsAssistedActive && !toolsBindingActive);
        if (toolsUndoBtn != null && !toolsUndoBtn.IsDisposed)
            toolsUndoBtn.Text = toolsJournalMode ? Loc.T("\u56de\u9000\u8fd9\u4e00\u6b65", "Undo this step") : Loc.T("\u64a4\u56de\u4e0a\u6b21", "Undo last");

        if (toolsWfPlayBtn != null && !toolsWfPlayBtn.IsDisposed)
        {
            toolsWfPlayBtn.Visible = toolsWorkflowMode;
            toolsWfPlayBtn.Text = toolsAssistedActive ? Loc.T("\u6279\u51c6", "Approve")
                : (toolsBindingActive ? Loc.T("\u5f00\u59cb\u56de\u653e", "Start replay") : Loc.T("\u56de\u653e", "Replay"));
            toolsWfPlayBtn.BackColor = toolsAssistedActive ? Color.FromArgb(249, 226, 175) : Color.FromArgb(49, 50, 68);
            toolsWfPlayBtn.ForeColor = toolsAssistedActive ? Color.FromArgb(30, 30, 46) : Color.FromArgb(249, 226, 175);
        }
        if (toolsClearBtn != null && !toolsClearBtn.IsDisposed)
            // 两个视图都叫"清空"，但清的対象不同   — 单独一行：i18n 棘轮只看代码行的中文，行尾注释会被算成未包装
            toolsClearBtn.Text = toolsAssistedActive
                ? Loc.T("\u505c\u6b62", "Stop")
                : (toolsJournalMode ? Loc.T("\u6e05\u7406\u5931\u6548", "Clear stale") : Loc.T("\u6e05\u7a7a", "Clear"));
    }

    // 2026-10-23 (Bro: the call-stream view still showed the journal table): RebuildToolsColumns
    // checks toolsJournalMode FIRST, and neither the call-stream nor the workflow entry cleared
    // it, so after visiting the journal those views kept painting journal columns and rows.
    // Drop just the flag (CloseJournalView would repaint twice) before switching.
    private static void LeaveJournalMode()
    {
        if (!toolsJournalMode) return;
        toolsJournalMode = false;
        journalRows.Clear();
    }

    // 2026-10-23 (Bro): explicit entry point to the call-stream view (tcp channel column).
    private static void OpenCallStreamView()
    {
        if (toolsView == null || toolsView.IsDisposed) return;
        // set the mode BEFORE showing the pane, so the first open cannot paint workflow first
        LeaveJournalMode();
        toolsCatalogMode = false;
        toolsWorkflowMode = false;
        if (!toolsView.Visible) { ToggleToolsView(true); return; }
        if (toolsGrid != null && !toolsGrid.IsDisposed) toolsGrid.Rows.Clear();
        UpdateToolsModeButton();
        RebuildToolsColumns();
        RefreshToolsGrid();
        UpdateToolsCount();
    }

    // A replay session with a live token keeps running; everything else is UI-only state that
    // must not survive closing the view (it made the next open show the old table).
    // Explicit entry point for the HANK_TOOLS command: show the pane, drop leftover modes, redraw.
    // Status-bar button target: show the pane already in the workflow view, freshly rebuilt.
    private static void OpenWorkflowView()
    {
        if (toolsView == null || toolsView.IsDisposed) return;
        LeaveJournalMode();
        toolsCatalogMode = false;
        toolsWorkflowMode = true;
        if (!toolsView.Visible) { ToggleToolsView(true); EnsureWorkflowsLoaded(true); return; }
        if (toolsGrid != null && !toolsGrid.IsDisposed) toolsGrid.Rows.Clear();
        UpdateToolsModeButton();
        RebuildToolsColumns();
        EnsureWorkflowsLoaded(true);
        RefreshToolsGrid();
        UpdateToolsCount();
    }

    private static void EnsureToolsViewVisible()
    {
        if (toolsView == null || toolsView.IsDisposed) return;
        if (!toolsView.Visible) ToggleToolsView();
        ResetTransientToolModes();
        if (toolsWorkflowMode) EnsureWorkflowsLoaded(true);
        RefreshToolsGrid();
        UpdateToolsCount();
    }

    private static void ResetTransientToolModes()
    {
        if (toolsAssistedActive && string.IsNullOrEmpty(assistedToken))
        {
            toolsAssistedActive = false;
            assistedRows.Clear();
        }
        if (toolsBindingActive)
        {
            toolsBindingActive = false;
            bindingTaskId = "";
            bindingRows.Clear();
            bindingHint = "";
        }
        if (toolsGrid != null && !toolsGrid.IsDisposed) toolsGrid.Rows.Clear();
        UpdateToolsModeButton();
        RebuildToolsColumns();
    }

    private static void RebuildToolsColumns()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        toolsGrid.Rows.Clear();
        toolsGrid.ReadOnly = true;
        toolsGrid.Columns.Clear();
        if (toolsAssistedActive) { BuildAssistedColumns(); return; }
        if (toolsBindingActive) { BuildBindingColumns(); return; }
        if (toolsJournalMode) { BuildJournalColumns(); return; }
        if (toolsWorkflowMode) { BuildWorkflowColumns(); toolsDirty = true; return; }
        if (toolsCatalogMode)
        {
            toolsGrid.Columns.Add(NewCol(Loc.T("方法", "Method"), 34));
            toolsGrid.Columns.Add(NewCol(Loc.T("说明", "Description"), 32));
            toolsGrid.Columns.Add(NewCol(Loc.T("调用", "Calls"), 12));
            toolsGrid.Columns.Add(NewCol(Loc.T("最近", "Last"), 11));
            toolsGrid.Columns.Add(NewCol(Loc.T("失败", "Fails"), 11));
        }
        else
        {
            toolsGrid.Columns.Add(NewCol(Loc.T("时间", "Time"), 15));
            toolsGrid.Columns.Add(NewCol(Loc.T("方法", "Method"), 40));
            toolsGrid.Columns.Add(NewCol(Loc.T("通道", "Via"), 11));
            toolsGrid.Columns.Add(NewCol(Loc.T("耗时", "Took"), 13));
            toolsGrid.Columns.Add(NewCol(Loc.T("\u5b57\u7b26", "Chars"), 12));
            toolsGrid.Columns.Add(NewCol(Loc.T("状态", "Status"), 20));
        }
        toolsDirty = true;
    }

    private static DataGridViewTextBoxColumn NewCol(string header, int fillWeight) => new()
    {
        HeaderText = header,
        FillWeight = fillWeight,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        Resizable = DataGridViewTriState.True,
    };

    // ---------------- 显隐 ----------------
    // 2026-10-23 (Bro: the first click on the call-stream button jumped): the parameter lets an
    // explicit entry point keep its own view. Without it the first open forced the workflow
    // mode, painted workflow columns, started an async workflow fetch and was then replaced by
    // the call stream -- a visible jump on the very first click only.
    private static void ToggleToolsView(bool explicitMode = false)
    {
        if (toolsView == null || toolsView.IsDisposed) return;
        toolsView.Visible = !toolsView.Visible;
        // 2026-10-21: 工具页签只替换"消息区"，输入框和配置区留着 —— 边看调用边能继续说话。
        // 两个 Dock=Fill 兄弟同时可见会抢空间，所以显式互斥（这是上次"点了只变色"的根因）。
        if (msgs != null && !msgs.IsDisposed) msgs.Visible = !toolsView.Visible;
        if (toolsView.Visible && !toolsFirstOpen)
        {
            // 2026-10-22 (Bro: "the old interface with the tcp channel column"): the first open
            // must land on the workflow records, not on the legacy call-stream table.
            toolsFirstOpen = true;
            if (!explicitMode)
            {
                toolsCatalogMode = false;
                toolsWorkflowMode = true;
            }
        }
        if (toolsView.Visible)
        {
            try { toolsView.BringToFront(); } catch (System.Exception ex) { PluginLog.Warn("Palette", "bring tools view to front: " + ex.Message); }
            // 2026-10-22 (Bro: "first click shows the old interface"): the mode flags and the grid
            // columns can disagree after a session was left open, so the first paint showed the
            // previous mode's table. Reset the transient modes and rebuild columns on every show;
            // a live replay session keeps its state.
            ResetTransientToolModes();
        }
        if (toolsToggleBtn != null && !toolsToggleBtn.IsDisposed)
        {
            toolsToggleBtn.BackColor = toolsView.Visible ? Color.FromArgb(137, 180, 250) : Color.FromArgb(49, 50, 68);
            toolsToggleBtn.ForeColor = toolsView.Visible ? Color.FromArgb(30, 30, 46) : Color.FromArgb(137, 180, 250);
        }
        if (toolsView.Visible)
        {
            if (toolsCatalogMode) EnsureCatalogLoaded();
            if (toolsWorkflowMode) EnsureWorkflowsLoaded(true);
            toolRows.TrimExcess();
            RefreshToolsGrid();
            UpdateToolsCount();
        }
    }

    // ---------------- 事件入口（SSE 线程）----------------
    // 只做解析 + 入队，绝不碰控件。
    private static void OnToolEventSse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var row = new ToolEventRow
            {
                Seq = GetLong(r, "seq"),
                At = DateTime.Now,
                Phase = GetStr(r, "phase"),
                Method = GetStr(r, "method"),
                Via = GetStr(r, "via"),
                Ms = GetLong(r, "ms"),
                Bytes = GetLong(r, "bytes"),
                Ok = !(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False),
                Err = GetStr(r, "errCode"),
            };
            if (row.Method.Length > 0) toolInbox.Enqueue(row);
        }
        catch (System.Exception ex)
        {
            PluginLog.Warn("Palette", "failed to parse a tool event: " + ex.Message);
        }
    }

    private static string GetStr(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static long GetLong(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    // ---------------- 定时合并（UI 线程）----------------
    private static void StartToolsTimer()
    {
        StopToolsTimer();
        toolsTimer = new System.Windows.Forms.Timer { Interval = 200 };
        toolsTimer.Tick += (_, _) => DrainToolInbox();
        toolsTimer.Start();
    }

    private static void StopToolsTimer()
    {
        if (toolsTimer == null) return;
        try { toolsTimer.Stop(); toolsTimer.Dispose(); } catch (System.Exception ex) { PluginLog.Warn("Palette", "stop tools timer: " + ex.Message); }
        toolsTimer = null;
    }

    private static void DrainToolInbox()
    {
        // 2026-10-22 (Bro: the workflow view stopped updating): while the workflow view stays open
        // reloads the list. Refresh it at most every 10s while that view is visible.
        if (toolsWorkflowMode && toolsView != null && !toolsView.IsDisposed && toolsView.Visible
            && !workflowLoading && !toolsAssistedActive && !toolsBindingActive && (DateTime.Now - workflowLoadedAt).TotalSeconds > 10)
            EnsureWorkflowsLoaded(true);

        var drained = 0;
        while (drained < 300 && toolInbox.TryDequeue(out var row))
        {
            drained++;
            toolRows.Add(row);
            var m = GetOrAddMethod(row.Method);
            if (row.Phase == "end")
            {
                m.Calls++;
                m.LastMs = row.Ms;
                if (!row.Ok)
                {
                    m.Fails++;
                    m.LastErr = row.Err;
                }
            }
            toolsDirty = true;
        }
        if (toolRows.Count > 500) toolRows.RemoveRange(0, toolRows.Count - 500);
        if (!toolsDirty) return;
        toolsDirty = false;
        UpdateToolsCount();
        if (toolsView != null && !toolsView.IsDisposed && toolsView.Visible) RefreshToolsGrid();
    }

    // ---------------- 渲染 ----------------
    // injected character count (formatResult output), not the raw plugin payload size:
    // listMethods returns ~41K chars raw but the model only sees ~1.7K after local filtering.
    private static string FormatChars(long n)
    {
        if (n <= 0) return "";
        if (n < 1000) return n.ToString();
        return (n / 1000.0).ToString(n < 100000 ? "0.0" : "0") + "K";
    }

    private static long ToolCharsTotal()
    {
        long n = 0;
        foreach (var r in toolRows) n += r.Bytes;
        return n;
    }

    private static void UpdateToolsCount()
    {
        if (toolsCount == null || toolsCount.IsDisposed) return;
        if (toolsAssistedActive) { UpdateAssistedCount(); return; }
        if (toolsBindingActive) { UpdateBindingCount(); return; }
        if (toolsJournalMode) { UpdateJournalCount(); return; }
        var calls = 0;
        var fails = 0;
        foreach (var m in methodRows.Values) { calls += m.Calls; fails += m.Fails; }
        if (toolsWorkflowMode) { UpdateWorkflowCount(); return; }
        toolsCount.Text = toolsCatalogMode
            ? Loc.T($"方法 {methodRows.Count} · 已调用 {calls} · 失败 {fails}", $"{methodRows.Count} methods · called {calls} · failed {fails}")
            : Loc.T($"\u4e8b\u4ef6 {toolRows.Count} \u00b7 \u5931\u8d25 {fails} \u00b7 \u6ce8\u5165 {FormatChars(ToolCharsTotal())} \u5b57\u7b26", $"{toolRows.Count} events \u00b7 failed {fails} \u00b7 {FormatChars(ToolCharsTotal())} chars injected");
    }

    private static void RefreshToolsGrid()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        if (toolsAssistedActive) { RenderAssistedRows(); return; }
        if (toolsBindingActive) { toolsGrid.ReadOnly = false; RenderBindingRows(); return; }
        if (toolsJournalMode) { toolsGrid.ReadOnly = true; RenderJournalRows(); return; }
        var q = (toolsSearch?.Text ?? "").Trim();
        var failOnly = toolsFailedOnly != null && toolsFailedOnly.Checked;
        toolsGrid.SuspendLayout();
        try
        {
            if (toolsWorkflowMode)
            {
                RenderWorkflowRows(q, failOnly);
            }
            else if (toolsCatalogMode)
            {
                var rows = methodRows.Values
                    .Where(m => MatchQuery(m.Name, m.Desc, q) && (!failOnly || m.Fails > 0))
                    .OrderByDescending(m => m.Calls)
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(600)
                    .ToList();
                toolsGrid.RowCount = 0;
                foreach (var m in rows)
                {
                    var idx = toolsGrid.Rows.Add(m.Name, m.Desc, m.Calls.ToString(), m.LastMs > 0 ? m.LastMs + "ms" : "", m.Fails > 0 ? m.Fails.ToString() : "");
                    if (m.Fails > 0) toolsGrid.Rows[idx].DefaultCellStyle.ForeColor = Color.FromArgb(243, 139, 168);
                }
            }
            else
            {
                var rows = toolRows
                    .Where(r => MatchQuery(r.Method, "", q) && (!failOnly || !r.Ok))
                    .TakeLast(300)
                    .Reverse()
                    .ToList();
                toolsGrid.RowCount = 0;
                foreach (var r in rows)
                {
                    // 2026-10-23 (Bro): tell the user up front when a step has no inverse
                    var statusCell = r.Ok ? (r.Phase == "end" ? "✓" : "▶") : "✗ " + r.Err;
                    if (r.Phase == "end" && UndoJournal.HasNoInverse(r.Method))
                        statusCell += " · " + Loc.T("不可撤", "no undo");
                    var idx = toolsGrid.Rows.Add(
                        r.At.ToString("HH:mm:ss"),
                        r.Method,
                        r.Via,
                        r.Phase == "end" ? r.Ms + "ms" : "…",
                        r.Bytes > 0 ? FormatChars(r.Bytes) : "",
                        statusCell);
                    if (!r.Ok) toolsGrid.Rows[idx].DefaultCellStyle.ForeColor = Color.FromArgb(243, 139, 168);
                }
            }
        }
        catch (System.Exception ex)
        {
            PluginLog.Warn("Palette", "tools grid refresh failed: " + ex.Message);
        }
        finally
        {
            toolsGrid.ResumeLayout();
        }
    }

    private static bool MatchQuery(string a, string b, string q)
    {
        if (q.Length == 0) return true;
        return a.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
            || (b.Length > 0 && b.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static MethodRow GetOrAddMethod(string name)
    {
        if (!methodRows.TryGetValue(name, out var m))
        {
            m = new MethodRow { Name = name };
            methodRows[name] = m;
        }
        return m;
    }

    // ---------------- 重置（会话被清空时） ----------------
    // 2026-10-21: relay 在 /clear(chat|all) 时广播 tool_reset —— 面板必须跟着清，
    // 不然"清空会话"之后工具页签还挂着上一轮的调用记录（Bro 实机反馈）。
    private static void ResetToolHistory(bool notifyRelay = true)
    {
        try
        {
            toolRows.Clear();
            foreach (var m in methodRows.Values) { m.Calls = 0; m.LastMs = 0; m.Fails = 0; m.LastErr = ""; }
            while (toolInbox.TryDequeue(out _)) { /* drop queued events from the cleared scope */ }
            toolsDirty = true;
            RefreshToolsGrid();
            UpdateToolsCount();
            PluginLog.Info("Palette", "tool history reset");
            // 2026-10-23 (Bro): clearing only the panel left the relay ring buffer intact, so the
            // next SSE reconnect replayed everything the user had just cleared. Tell the relay,
            // unless this reset came FROM the relay (tool_reset), which would loop.
            if (notifyRelay) NotifyRelayToolReset();
        }
        catch (System.Exception ex) { PluginLog.Warn("Palette", "tools reset failed: " + ex.Message); }
    }

    private static void NotifyRelayToolReset()
    {
        try
        {
            var body = "{\"scope\":\"tools\"}";
            _ = wfHc.PostAsync(relayUrl + "/clear", new StringContent(body, Encoding.UTF8, "application/json"));
        }
        catch (System.Exception ex) { PluginLog.Warn("Palette", "tool reset notify failed: " + ex.Message); }
    }

    // ---------------- 方法目录（listMethods，420 条）----------------
    private static async void EnsureCatalogLoaded()
    {
        if (toolsCatalogLoading) return;
        var need = methodRows.Count < 10;
        if (!need) return;
        toolsCatalogLoading = true;
        try
        {
            var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"listMethods\",\"params\":{}}";
            using var resp = await hc.PostAsync(relayUrl + "/tcp", new StringContent(body, Encoding.UTF8, "application/json"));
            var txt = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(txt);
            if (!doc.RootElement.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Object)
            {
                PluginLog.Warn("Palette", "listMethods returned no result: " + txt.Substring(0, Math.Min(160, txt.Length)));
                return;
            }
            JsonElement methodsEl;
            if (res.TryGetProperty("methods", out methodsEl) && methodsEl.ValueKind == JsonValueKind.Array)
            {
                JsonElement descEl = default;
                var hasDesc = res.TryGetProperty("descriptions", out descEl) && descEl.ValueKind == JsonValueKind.Object;
                foreach (var el in methodsEl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    var name = el.GetString() ?? "";
                    if (name.Length == 0) continue;
                    var m = GetOrAddMethod(name);
                    if (hasDesc && descEl.TryGetProperty(name, out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var s = d.GetString() ?? "";
                        m.Desc = s.Length > 140 ? s.Substring(0, 140) + "…" : s;
                    }
                }
                toolsDirty = true;
                UpdateToolsCount();
                if (toolsView != null && toolsView.Visible) RefreshToolsGrid();
                PluginLog.Info("Palette", "tool catalog loaded: " + methodRows.Count + " methods");
            }
        }
        catch (System.Exception ex)
        {
            PluginLog.Warn("Palette", "catalog load failed: " + ex.Message);
        }
        finally
        {
            toolsCatalogLoading = false;
        }
    }
}
