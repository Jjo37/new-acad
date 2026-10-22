using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Civil3DMcpPlugin;

// 2026-10-21 P2.4: 面板「工具」页签的第三视图 —— 工作流记录。
// 为什么放在面板里：记录如果只能靠命令行 GET /workflows 才看得见，等于没有。
// 这里只做三件事：看、导出、dry-run 回放。assisted 回放**不在面板里**（写操作必须走逐档确认，等 1.11.0）。
// 约束沿用 Tools.cs：UI 线程不 .Wait()/.Result；所有用户可见文案走 Loc.T（i18n 棘轮会查）。
public static partial class HankPalette
{
    private sealed class WorkflowRow
    {
        public string Id = "";
        public string Ask = "";
        public string Created = "";
        public string Kind = "";
        public int Steps;
        public long Chars;
        public long CharsPeak;
        public double Score;
        public bool Ok = true;
    }

    private static readonly List<WorkflowRow> workflowRows = new();
    private static bool toolsWorkflowMode;          // 第三视图开关（与 toolsCatalogMode 互斥）
    private static bool workflowLoading;
    private static bool workflowLoaded;
    private static DateTime workflowLoadedAt = DateTime.MinValue;
    private static ContextMenuStrip workflowMenu;
    private static Button toolsWfExportBtn;
    private static Button toolsClearBtn;

    // 回放/导出可能跑几十秒（13 步只读任务实测 ~1s，但换图后可能更慢），默认 3s 的 hc 会超时 —— 单独一个客户端。
    private static readonly HttpClient wfHc = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

    // ---------------- 列 ----------------
    private static void BuildWorkflowColumns()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        toolsGrid.Columns.Add(NewCol(Loc.T("时间", "Time"), 16));
        toolsGrid.Columns.Add(NewCol(Loc.T("任务", "Task"), 44));
        toolsGrid.Columns.Add(NewCol(Loc.T("步数", "Steps"), 9));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u5b57\u7b26", "Chars"), 11));
        toolsGrid.Columns.Add(NewCol(Loc.T("评分", "Score"), 9));
        toolsGrid.Columns.Add(NewCol(Loc.T("结果", "Result"), 12));
        toolsGrid.Columns.Add(NewCol(Loc.T("编号", "ID"), 18));
    }

    // ---------------- 渲染 ----------------
    // 2026-10-22 (Bro): the list showed UTC. createdAt is "...T03:15:05.728Z" and the old code
    // only sliced the string, so 11:15 local rendered as 03:15. Parse it and convert to local.
    private static string FormatLocalTime(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        DateTime dt;
        if (DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out dt))
            return dt.ToLocalTime().ToString("MM-dd HH:mm");
        return iso.Length >= 16 ? iso.Substring(5, 11).Replace('T', ' ') : iso;
    }

    private static void RenderWorkflowRows(string q, bool failOnly)
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        var rows = workflowRows
            .Where(r => (q.Length == 0 || MatchQuery(r.Ask, r.Id, q)) && (!failOnly || !r.Ok))
            .ToList();
        toolsGrid.RowCount = 0;
        foreach (var r in rows)
        {
            var idx = toolsGrid.Rows.Add(
                r.Created,
                r.Ask.Length > 80 ? r.Ask.Substring(0, 80) + "…" : r.Ask,
                r.Steps.ToString(),
                FormatChars(r.Chars),
                r.Score.ToString("0.00"),
                r.Ok ? Loc.T("成功", "ok") : Loc.T("失败", "failed"),
                r.Id);
            if (!r.Ok) toolsGrid.Rows[idx].DefaultCellStyle.ForeColor = Color.FromArgb(243, 139, 168);
            toolsGrid.Rows[idx].Tag = r;
        }
    }

    private static void UpdateWorkflowCount()
    {
        if (toolsCount == null || toolsCount.IsDisposed) return;
        var hint = Loc.T("双击=回放，右键=导出", "double-click = replay, right-click = export");
        toolsCount.Text = Loc.T($"工作流 {workflowRows.Count} 条 · {hint}", $"{workflowRows.Count} workflows · {hint}");
    }

    // ---------------- 加载 ----------------
    private static async void EnsureWorkflowsLoaded(bool force = false)
    {
        if (workflowLoading) return;
        if (workflowLoaded && !force) return;
        workflowLoading = true;
        try
        {
            using var resp = await wfHc.GetAsync(relayUrl + "/workflows?limit=100");
            var txt = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(txt);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                PluginLog.Warn("Palette", "workflow list returned no items: " + txt.Substring(0, Math.Min(160, txt.Length)));
                return;
            }
            workflowRows.Clear();
            foreach (var el in items.EnumerateArray())
            {
                var r = new WorkflowRow
                {
                    Id = GetStr(el, "taskId"),
                    Ask = GetStr(el, "ask"),
                    Kind = GetStr(el, "kind"),
                    Steps = (int)GetLong(el, "steps"),
                    Ok = !(el.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False),
                };
                if (el.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number) r.Score = sc.GetDouble();
                r.Chars = GetLong(el, "chars");
                r.CharsPeak = GetLong(el, "charsPeak");
                r.Created = FormatLocalTime(GetStr(el, "createdAt"));
                workflowRows.Add(r);
            }
            workflowLoaded = true;
            workflowLoadedAt = DateTime.Now;
            toolsDirty = true;
            PluginLog.Info("Palette", "workflow list loaded: " + workflowRows.Count + " records");
            if (toolsView != null && !toolsView.IsDisposed && toolsView.Visible)
            {
                RefreshToolsGrid();
                UpdateToolsCount();
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn("Palette", "workflow list load failed: " + ex.Message);
        }
        finally
        {
            workflowLoading = false;
        }
    }

    // ---------------- 行内操作 ----------------
    private static void AttachWorkflowGridEvents()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;

        toolsGrid.CellDoubleClick += (_, e) =>
        {
            if (!toolsWorkflowMode || e.RowIndex < 0) return;
            if (toolsGrid.Rows[e.RowIndex].Tag is WorkflowRow r) ReplayWorkflow(r);
        };

        toolsGrid.CellMouseDown += (_, e) =>
        {
            if (!toolsWorkflowMode || e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            if (toolsGrid.Rows[e.RowIndex].Tag is WorkflowRow r)
            {
                toolsGrid.ClearSelection();
                toolsGrid.Rows[e.RowIndex].Selected = true;
                ShowWorkflowMenu(r, e.Location);
            }
        };
    }

    private static void ShowWorkflowMenu(WorkflowRow row, Point at)
    {
        if (workflowMenu == null)
        {
            workflowMenu = new ContextMenuStrip();
            workflowMenu.Items.Add(Loc.T("导出（JSON + Markdown）", "Export (JSON + Markdown)"), null, (_, _) => ExportWorkflow(SelectedWorkflow()));
            workflowMenu.Items.Add(Loc.T("dry-run 回放（只读）", "Dry-run replay (read-only)"), null, (_, _) => ReplayWorkflow(SelectedWorkflow()));
            workflowMenu.Items.Add(Loc.T("刷新列表", "Refresh"), null, (_, _) => EnsureWorkflowsLoaded(true));
            workflowMenu.Items.Add(new ToolStripSeparator());
            workflowMenu.Items.Add(Loc.T("删除这条（移到 _trash）", "Delete this record (to _trash)"), null, (_, _) => PurgeWorkflows(false));
        }
        workflowMenu.Show(toolsGrid, at);
    }

    private static WorkflowRow FirstWorkflowRow()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed || toolsGrid.Rows.Count == 0) return null;
        return toolsGrid.Rows[0].Tag as WorkflowRow;
    }

    private static WorkflowRow SelectedWorkflow()
    {
        if (toolsGrid == null || toolsGrid.SelectedRows.Count == 0) return null;
        return toolsGrid.SelectedRows[0].Tag as WorkflowRow;
    }

    private static async void ExportWorkflow(WorkflowRow row)
    {
        if (row == null) return;
        try
        {
            var body = "{\"format\":\"both\"}";
            using var resp = await wfHc.PostAsync(relayUrl + "/workflows/" + Uri.EscapeDataString(row.Id) + "/export",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var txt = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var err = GetStr(root, "error");
                SetToolsHint(Loc.T("导出失败：" + (err.Length > 0 ? err : txt.Substring(0, Math.Min(120, txt.Length))), "Export failed: " + (err.Length > 0 ? err : txt.Substring(0, Math.Min(120, txt.Length)))));
                return;
            }
            var files = new List<string>();
            if (root.TryGetProperty("written", out var w) && w.ValueKind == JsonValueKind.Array)
                foreach (var f in w.EnumerateArray()) if (f.ValueKind == JsonValueKind.String) files.Add(f.GetString() ?? "");
            PluginLog.Info("Palette", "workflow exported: " + row.Id + " -> " + string.Join(", ", files));
            var first = files.Count > 0 ? files[0] : "";
            var dir = first.Length > 0 ? System.IO.Path.GetDirectoryName(first) : "";
            SetToolsHint(Loc.T("已导出到 " + dir, "Exported to " + dir));
        }
        catch (Exception ex)
        {
            PluginLog.Warn("Palette", "workflow export failed: " + ex.Message);
            SetToolsHint(Loc.T("导出失败：" + ex.Message, "Export failed: " + ex.Message));
        }
    }

    private static async void ReplayWorkflow(WorkflowRow row)
    {
        if (row == null) return;
        try
        {
            var body = "{\"mode\":\"dry-run\"}";
            using var resp = await wfHc.PostAsync(relayUrl + "/workflows/" + Uri.EscapeDataString(row.Id) + "/replay",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var txt = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            var ran = (int)GetLong(root, "ran");
            var matched = (int)GetLong(root, "matched");
            var mismatched = (int)GetLong(root, "mismatched");
            var errors = (int)GetLong(root, "errors");
            var skipped = (int)GetLong(root, "skipped");
            var noFp = (int)GetLong(root, "noFingerprint");
            var okAll = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;

            PluginLog.Info("Palette", "workflow replay " + row.Id + ": ok=" + okAll + " ran=" + ran + " match=" + matched + " mismatch=" + mismatched + " err=" + errors + " skipped=" + skipped + " noFp=" + noFp);
            // 2026-10-21: 不用 MessageBox —— 我们自己的看门狗会向有属主的模态窗口投 ESC/WM_CLOSE，
            // 弹窗既可能被自动关掉、也测不了。结果写进底部提示条：非阻塞、UIA 能读到。
            SetToolsHint(okAll
                ? Loc.T($"回放通过：跑 {ran} · 一致 {matched} · 跳过(写) {skipped}", $"Replay passed: {ran} ran · {matched} match · {skipped} skipped (writes)")
                : Loc.T($"回放未通过：不一致 {mismatched} · 出错 {errors} · 一致 {matched}/{ran}", $"Replay failed: {mismatched} mismatch · {errors} error · {matched}/{ran} matched"));
        }
        catch (Exception ex)
        {
            PluginLog.Warn("Palette", "workflow replay failed: " + ex.Message);
            SetToolsHint(Loc.T("回放失败：" + ex.Message, "Replay failed: " + ex.Message));
        }
    }

    // 结果写进底部提示条（非阻塞）。完整信息在 plugin.log 与 relay.log 里。
    // 2026-10-21: 不做确认框。理由：清空只是把文件移到 _trash（可恢复），
    // 代价不对称 —— 误点的代价是"再点一次恢复"，而确认框的代价是每次都要多点一下，
    // 而且模态框会撞上看门狗、也挡死自动化（实测过两轮）。
    private static void ClearWorkflowsClicked()
    {
        PurgeWorkflows(true);
    }

    private static async void PurgeWorkflows(bool all)
    {
        try
        {
            var row = SelectedWorkflow();
            if (!all && row == null) { SetToolsHint(Loc.T("先选一条记录再删", "Select a record first")); return; }
            var ids = all ? "[]" : "[\"" + row.Id + "\"]";
            using var resp = await wfHc.PostAsync(relayUrl + "/workflows/purge",
                new StringContent("{\"ids\":" + ids + "}", Encoding.UTF8, "application/json"));
            var txt = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(txt);
            var moved = (int)GetLong(doc.RootElement, "moved");
            PluginLog.Info("Palette", "workflow purge: moved " + moved + " record(s) to _trash");
            workflowLoaded = false;
            EnsureWorkflowsLoaded(true);
            SetToolsHint(Loc.T($"已清掉 {moved} 条（在 _trash，可恢复）", $"Cleared {moved} record(s) (in _trash, recoverable)"));
        }
        catch (Exception ex)
        {
            PluginLog.Warn("Palette", "workflow purge failed: " + ex.Message);
            SetToolsHint(Loc.T("清空失败：" + ex.Message, "Clear failed: " + ex.Message));
        }
    }

    private static void SetToolsHint(string text)
    {
        try
        {
            if (toolsCount == null || toolsCount.IsDisposed) return;
            toolsCount.Text = text;
        }
        catch (Exception ex) { PluginLog.Warn("Palette", "hint update failed: " + ex.Message); }
    }
}
