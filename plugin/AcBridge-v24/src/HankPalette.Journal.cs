using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Civil3DMcpPlugin;

// 2026-10-22: the journal view -- "pick the step that went wrong and revert it".
//
// The engine (UndoJournal / journalList / revertEntry) reverts ONE action and leaves its
// neighbours alone, so the UI only has to show the list and let the user choose. It stays a grid
// inside the tools pane (no modal windows, ADR 0006) and every row carries the sentence the plugin
// recorded, so the operator does not have to read method names.
public static partial class HankPalette
{
    private sealed class JournalRow
    {
        public long Seq;
        public string When = "";
        public string What = "";
        public string Reversible = "";
        public string Status = "";
        public bool CanUndo;
        // ADR 0008: the hover sentence -- why this step cannot be reverted
        public string Tip = "";
    }

    private static readonly List<JournalRow> journalRows = new();
    private static bool journalLoading;

    private static void BuildJournalColumns()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        toolsGrid.Columns.Add(NewCol(Loc.T("\u5e8f\u53f7", "#"), 7));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u65f6\u95f4", "Time"), 12));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u5e72\u4e86\u4ec0\u4e48", "What it did"), 52));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u53ef\u64a4", "Undoable"), 12));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u72b6\u6001", "Status"), 14));
        EnsureJournalTooltips();
    }

    // Bro 2026-10-23: the reason goes in a cell tooltip, not a dialog (ADR 0006 stays zero-modal).
    private static void EnsureJournalTooltips()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        toolsGrid.CellToolTipTextNeeded -= JournalCellToolTip;
        toolsGrid.CellToolTipTextNeeded += JournalCellToolTip;
        toolsGrid.CellMouseDown -= JournalCellMouseDown;
        toolsGrid.CellMouseDown += JournalCellMouseDown;
    }

    // 2026-10-23 (Bro: stale rows had no way out): right-click to revert one or clear the dead ones.
    private static ContextMenuStrip journalMenu;

    private static void JournalCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
    {
        if (!toolsJournalMode || toolsGrid == null || e.Button != MouseButtons.Right || e.RowIndex < 0) return;
        if (journalMenu == null)
        {
            journalMenu = new ContextMenuStrip();
            journalMenu.Items.Add(Loc.T("\u64a4\u56de\u8fd9\u4e00\u6761", "Revert this step"), null, (_, _) => RevertSelectedJournalRow());
            journalMenu.Items.Add(Loc.T("\u6e05\u7406\u64a4\u4e0d\u56de\u7684\u8bb0\u5f55", "Clear entries that cannot be reverted"), null, (_, _) => RequestPruneBlocked());
            journalMenu.Items.Add(Loc.T("\u6e05\u7a7a\u56de\u9000\u6e05\u5355", "Clear the whole journal"), null, (_, _) => RequestClearWholeJournal());
        }
        toolsGrid.ClearSelection();
        toolsGrid.Rows[e.RowIndex].Selected = true;
        journalMenu.Show(toolsGrid, e.Location);
    }

    // 2026-10-23 (Bro: clearing the whole journal is dangerous): a two-click confirmation in the
    // status line instead of a dialog -- ADR 0006 keeps this panel modal-free.
    // 2026-10-28 (P2.6 S4): 状态交给统一的 DestructiveGuard，不再自己攒一对 bool。
    private static void RequestClearWholeJournal()
    {
        if (destructiveGuard.Request("journal-clear-all") != GuardDecision.Confirmed)
        {
            SetToolsHint(Loc.T(
                "\u518d\u70b9\u4e00\u6b21\u300c\u6e05\u7a7a\u56de\u9000\u6e05\u5355\u300d\u786e\u8ba4\uff08\u53ef\u64a4\u7684\u6761\u76ee\u4e5f\u4f1a\u6ca1\uff0c\u64a4\u9500\u80fd\u529b\u6e05\u96f6\uff09",
                "Click Clear the whole journal once more to confirm (reversible entries go too, and the undo ability is gone)"));
            return;
        }
        PruneJournal("all");
    }

    // 2026-10-28 (P2.6 S4): 「清理撤不回的记录」同样是不可逆删除（删了就没有 undo 依据），
    // 以前一句话都不问 —— 现在和清空整本走同一个守卫。
    private static void RequestPruneBlocked()
    {
        if (destructiveGuard.Request("journal-prune-blocked") != GuardDecision.Confirmed)
        {
            SetToolsHint(Loc.T("\u518d\u70b9\u4e00\u6b21\u786e\u8ba4\u6e05\u7406\u64a4\u4e0d\u56de\u7684\u8bb0\u5f55", "Click once more to clean the entries that cannot be reverted"));
            return;
        }
        PruneJournal("blocked");
    }

    private static void PruneJournal(string scope)
    {
        var run = new Func<System.Threading.Tasks.Task>(async () =>
        {
            try
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "journalPrune",
                    ["params"] = new Dictionary<string, object> { ["scope"] = scope },
                });
                using var resp = await wfHc.PostAsync(relayUrl + "/tcp", new StringContent(body, Encoding.UTF8, "application/json"));
                var txt = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                {
                    SetToolsHint(Loc.T("\u6e05\u7406\u5931\u8d25\uff1a", "Cleanup failed: ") + GetStr(err, "message"));
                    return;
                }
                var removed = 0;
                if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object)
                    removed = (int)GetLong(res, "removed");
                journalRows.Clear();
                LoadJournal();
                SetToolsHint(Loc.T($"\u5df2\u6e05\u7406 {removed} \u6761\u64a4\u4e0d\u56de\u7684\u8bb0\u5f55", $"Cleared {removed} entries that could not be reverted"));
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Palette", "journal prune failed: " + ex.Message);
                SetToolsHint(Loc.T("\u6e05\u7406\u5931\u8d25\uff1a", "Cleanup failed: ") + ex.Message);
            }
        });
        _ = run();
    }

    private static void JournalCellToolTip(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (!toolsJournalMode || toolsGrid == null || e.RowIndex < 0) return;
        if (e.RowIndex >= toolsGrid.Rows.Count) return;
        var row = toolsGrid.Rows[e.RowIndex].Tag as JournalRow;
        if (row == null) return;
        e.ToolTipText = row.Tip;
    }

    private static void RenderJournalRows()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        toolsGrid.RowCount = 0;
        foreach (var r in journalRows)
        {
            var idx = toolsGrid.Rows.Add(r.Seq.ToString(), r.When, r.What, r.Reversible, r.Status);
            var row = toolsGrid.Rows[idx];
            if (!r.CanUndo) row.DefaultCellStyle.ForeColor = Color.FromArgb(108, 112, 134);
            row.Tag = r;
        }
    }

    private static void UpdateJournalCount()
    {
        if (toolsCount == null || toolsCount.IsDisposed) return;
        var undoable = 0;
        foreach (var r in journalRows) if (r.CanUndo && r.Status.Length == 0) undoable++;
        toolsCount.Text = Loc.T(
            $"\u8d26\u672c {journalRows.Count} \u6761 \u00b7 \u53ef\u64a4 {undoable} \u6761 \u00b7 \u9009\u4e00\u884c\u70b9\u300c\u56de\u9000\u8fd9\u4e00\u6b65\u300d",
            $"journal {journalRows.Count} entries \u00b7 {undoable} undoable \u00b7 select a row then press Undo this step");
    }

    // 2026-10-23 (Bro: the hint must exist in both languages and follow the panel language):
    // the plugin records an enum (revertBlockedReason); the wording belongs to the UI, so map it
    // here instead of showing the English sentence the plugin stored.
    private static string BlockedReasonText(string reason, string fallback)
    {
        switch ((reason ?? "").Trim().ToLowerInvariant())
        {
            case "no-inverse":
                return Loc.T("\u8fd9\u4e2a\u52a8\u4f5c\u6ca1\u6709\u9006\u64cd\u4f5c\uff08purge / \u811a\u672c / \u5199\u6587\u4ef6\uff09\uff0c\u64a4\u4e0d\u56de\u6765",
                    "This action has no inverse (purge / script / file write), so it cannot be reverted");
            case "unsupported-object":
                return Loc.T("Civil 3D \u5bf9\u8c61\u6ca1\u6cd5\u5feb\u7167\uff0c\u53ea\u6709\u666e\u901a AutoCAD \u56fe\u5143\u80fd\u64a4",
                    "Civil 3D objects cannot be snapshotted; only plain AutoCAD entities can be reverted");
            case "expired":
                return Loc.T("\u5df2\u8d85\u51fa\u64a4\u9500\u7a97\u53e3\uff08\u6761\u76ee\u6216\u5feb\u7167\u6570\u91cf\u4e0a\u9650\uff09",
                    "Outside the undo window (entry or snapshot limit)");
            case "handle-stale":
                return Loc.T("\u4e4b\u524d\u7684\u64a4\u9500\u5df2\u7ecf\u91cd\u5efa\u4e86\u5b83\u5f15\u7528\u7684\u5bf9\u8c61\uff0c\u53e5\u67c4\u5bf9\u4e0d\u4e0a\u4e86",
                    "An earlier revert rebuilt the object this entry referenced, so its handles are stale");
            default:
                return fallback ?? "";
        }
    }

    private static void OpenJournalView()
    {
        if (toolsView == null || toolsView.IsDisposed) return;
        // 2026-10-28 (S1): 视图切换只有一个入口；日志行缓存另行清理
        toolsViewState.TrySwitch(ToolsView.Journal);
        if (!toolsView.Visible) { ToggleToolsView(true); LoadJournal(); return; }
        if (toolsGrid != null && !toolsGrid.IsDisposed) toolsGrid.Rows.Clear();
        UpdateToolsModeButton();
        RebuildToolsColumns();
        LoadJournal();
        UpdateToolsCount();
    }

    private static void CloseJournalView()
    {
        if (toolsViewState.Current != ToolsView.Journal) return;
        toolsViewState.TrySwitch(ToolsView.Stream);
        journalRows.Clear();
        UpdateToolsModeButton();
        RebuildToolsColumns();
        RefreshToolsGrid();
        UpdateToolsCount();
    }

    private static void LoadJournal()
    {
        if (journalLoading) return;
        journalLoading = true;
        var run = new Func<System.Threading.Tasks.Task>(async () =>
        {
            try
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "journalList",
                    ["params"] = new Dictionary<string, object> { ["limit"] = 50 },
                });
                using var resp = await wfHc.PostAsync(relayUrl + "/tcp", new StringContent(body, Encoding.UTF8, "application/json"));
                var txt = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;
                journalRows.Clear();
                if (root.TryGetProperty("result", out var result) && result.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in entries.EnumerateArray())
                    {
                        // ADR 0008 contract first, old "reversible"/"reason" as fallback
                        var canUndo = e.TryGetProperty("revertible", out var rv)
                            ? rv.ValueKind == JsonValueKind.True
                            : (e.TryGetProperty("reversible", out var oldRv) && oldRv.ValueKind == JsonValueKind.True);
                        var blockedReason = GetStr(e, "revertBlockedReason");
                        var blockedText = GetStr(e, "revertBlockedText");
                        if (blockedText.Length == 0) blockedText = GetStr(e, "reason");
                        var stale = blockedReason == "expired" || blockedReason == "handle-stale";
                        var r = new JournalRow
                        {
                            Seq = GetLong(e, "seq"),
                            What = JournalSentence(e),
                            CanUndo = canUndo,
                        };
                        r.Reversible = canUndo
                            ? Loc.T("\u53ef\u64a4", "yes")
                            : (stale ? Loc.T("\u5df2\u5931\u6548", "stale") : Loc.T("\u4e0d\u53ef\u64a4", "no"));
                        r.Tip = canUndo
                            ? Loc.T("\u70b9\u300c\u56de\u9000\u8fd9\u4e00\u6b65\u300d\u53ea\u64a4\u8fd9\u4e00\u6761\uff0c\u65c1\u8fb9\u7684\u52a8\u4f5c\u4e0d\u52a8",
                                "Reverting undoes only this step; its neighbours stay untouched")
                            : BlockedReasonText(blockedReason, blockedText);
                        var at = GetStr(e, "at");
                        r.When = at.Length >= 16 ? at.Substring(11, 5) : at;
                        r.Status = "";
                        journalRows.Add(r);
                    }
                }
                PluginLog.Info("Palette", "journal loaded: " + journalRows.Count + " entries");
                RefreshToolsGrid();
                UpdateToolsCount();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Palette", "journal load failed: " + ex.Message);
                SetToolsHint(Loc.T("\u8bfb\u8d26\u672c\u5931\u8d25\uff1a", "Cannot read the journal: ") + ex.Message);
            }
            finally
            {
                journalLoading = false;
            }
        });
        _ = run();
    }

    private static string JournalSentence(JsonElement e)
    {
        var summary = GetStr(e, "summary");
        if (summary.Length > 0) return summary;
        return GetStr(e, "method");
    }

    private static JournalRow SelectedJournalRow()
    {
        if (toolsGrid == null || toolsGrid.SelectedRows.Count == 0) return null;
        return toolsGrid.SelectedRows[0].Tag as JournalRow;
    }

    // revert exactly the selected row
    private static void RevertSelectedJournalRow()
    {
        var row = SelectedJournalRow();
        if (row == null) { SetToolsHint(Loc.T("\u5148\u9009\u4e00\u884c", "Select a row first")); return; }
        if (!row.CanUndo) { SetToolsHint(Loc.T("\u64a4\u4e0d\u56de\uff1a", "Cannot revert: ") + row.Tip); return; }

        var run = new Func<System.Threading.Tasks.Task>(async () =>
        {
            try
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "revertEntry",
                    ["params"] = new Dictionary<string, object> { ["seq"] = row.Seq },
                });
                using var resp = await wfHc.PostAsync(relayUrl + "/tcp", new StringContent(body, Encoding.UTF8, "application/json"));
                var txt = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                {
                    SetToolsHint(Loc.T("\u56de\u9000\u5931\u8d25\uff1a", "Revert failed: ") + GetStr(err, "message"));
                    return;
                }
                var res = root.TryGetProperty("result", out var rr) ? rr : default;
                // 2026-10-23: a revert that moved nothing must not look like success
                var okFlag = !(res.ValueKind == JsonValueKind.Object && res.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False);
                if (!okFlag)
                {
                    var why = res.ValueKind == JsonValueKind.Object ? GetStr(res, "reason") : "";
                    SetToolsHint(Loc.T("\u672a\u64a4\u9500\uff1a", "Nothing reverted: ") + why);
                    journalRows.Remove(row);
                    LoadJournal();
                    return;
                }
                var restored = res.ValueKind == JsonValueKind.Object && res.TryGetProperty("restoredHandles", out var rh) && rh.ValueKind == JsonValueKind.Array ? rh.GetArrayLength() : 0;
                var erased = res.ValueKind == JsonValueKind.Object && res.TryGetProperty("erasedHandles", out var eh) && eh.ValueKind == JsonValueKind.Array ? eh.GetArrayLength() : 0;
                PluginLog.Info("Palette", "journal revert seq=" + row.Seq + " restored=" + restored + " erased=" + erased);
                SetToolsHint(Loc.T($"\u5df2\u56de\u9000\u7b2c {row.Seq} \u6b65\uff08\u6062\u590d {restored} \u00b7 \u5220\u9664 {erased}\uff09",
                    $"Reverted step {row.Seq} (restored {restored}, erased {erased})"));
                journalRows.Remove(row);
                RefreshToolsGrid();
                UpdateToolsCount();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Palette", "journal revert failed: " + ex.Message);
                SetToolsHint(Loc.T("\u56de\u9000\u5931\u8d25\uff1a", "Revert failed: ") + ex.Message);
            }
        });
        _ = run();
    }
}
