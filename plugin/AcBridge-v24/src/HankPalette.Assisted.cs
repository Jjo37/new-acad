using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Civil3DMcpPlugin;

// 2026-10-22 P2.3: assisted replay UI -- the panel half of the step-by-step confirm flow.
//
// Design constraints learned the hard way:
//   * NO modal dialogs. Our own watchdog posts ESC / WM_CLOSE to owned windows, so a modal
//     confirm box can be closed by the product itself (and cannot be driven by UIA either).
//     Everything happens inline: the grid becomes the plan, the hint bar tells you what is next.
//   * One button, two meanings. Toolbar width is the scarce resource (panel can be 320px wide),
//     so the replay button doubles as "approve" while a session is running, and the clear
//     button becomes "stop" for the session's lifetime.
//   * Never block the UI thread: Http calls are async void + await (no .Wait()/.Result).
//
// The server owns the safety rules (tier A/B/C, remap, ordering). This file only renders and
// forwards user intent; it must never decide that a step is safe by itself.
public static partial class HankPalette
{
    private sealed class AssistedRow
    {
        public int I;
        public string Method = "";
        public string Tier = "";
        public string Params = "";
        public string Desc = "";
        public string Status = "";
        public bool Done;
        public bool Failed;
        public bool Drifted;
    }

    private static readonly List<AssistedRow> assistedRows = new();
    private static string assistedToken = "";
    private static string assistedTaskId = "";
    private static int assistedCursor;
    private static int assistedTotal;
    private static Button toolsWfPlayBtn;

    // 2026-11-02 (P2.5): 信任状态。开回放时向 relay 取一次，不自己推断；分数/会话都可能变。
    private static int assistedTrust;
    private static string assistedTrustMode = "step";
    private static bool assistedWaived;
    private static string assistedModule = "";
    private static string assistedTrustNote = "";

    private static void BuildAssistedColumns()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        toolsGrid.Columns.Add(NewCol(Loc.T("\u6b65", "Step"), 7));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u8fd9\u4e00\u6b65\u5e72\u4ec0\u4e48", "What this step does"), 44));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u98ce\u9669", "Risk"), 12));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u53c2\u6570", "Params"), 38));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u72b6\u6001", "Status"), 22));
    }

    private static void RenderAssistedRows()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        toolsGrid.RowCount = 0;
        var nextI = NextAssistedIndex();
        foreach (var r in assistedRows)
        {
            var status = r.Status;
            if (!r.Done && r.I == nextI) status = Loc.T("\u5f85\u786e\u8ba4", "waiting");
            var what = r.Desc.Length > 0 ? r.Desc : (ActionOf(r.Method) + (r.Params.Length > 0 ? "\uff08" + r.Params + "\uff09" : ""));
            var idx = toolsGrid.Rows.Add(r.I.ToString(), what, TierWord(r.Tier), status);
            var row = toolsGrid.Rows[idx];
            if (r.Failed) row.DefaultCellStyle.ForeColor = Color.FromArgb(243, 139, 168);
            else if (r.Drifted) row.DefaultCellStyle.ForeColor = Color.FromArgb(249, 226, 175);
            else if (!r.Done && r.I == nextI) row.DefaultCellStyle.ForeColor = Color.FromArgb(137, 180, 250);
            if (r.Tier == "C") row.Cells[2].Style.ForeColor = Color.FromArgb(243, 139, 168);
            else if (r.Tier == "B") row.Cells[2].Style.ForeColor = Color.FromArgb(249, 226, 175);
            row.Tag = r;
        }
    }

    private static int NextAssistedIndex()
    {
        // the server is the authority on order; the UI only needs "which row is next" for display
        foreach (var r in assistedRows) if (!r.Done) return r.I;
        return -1;
    }

    private static void UpdateAssistedCount()
    {
        if (toolsCount == null || toolsCount.IsDisposed) return;
        var next = NextAssistedIndex();
        var tier = next > 0 ? TierOf(next) : "";
        var tierWord = tier == "C" ? Loc.T("\uff08\u7834\u574f\u6027\uff09", " (destructive)")
            : (tier == "B" ? Loc.T("\uff08\u53ef\u9006\u5199\uff09", " (reversible write)") : "");
        toolsCount.Text = Loc.T(
            $"assisted \u56de\u653e {assistedCursor}/{assistedTotal} \u00b7 \u4e0b\u4e00\u6b65 {next}{tierWord}",
            $"assisted replay {assistedCursor}/{assistedTotal} \u00b7 next {next}{tierWord}");
        // 2026-11-02 (P2.5): 信任写在计数行里 —— 用户要知道这次为什么一步一问、为什么可以合并。
        if (toolsAssistedActive)
            toolsCount.Text += Loc.T(" · 信任 " + assistedTrust + "/5（" + TrustWord() + "）",
                " · trust " + assistedTrust + "/5 (" + TrustWord() + ")");
    }


    // 2026-11-02 (P2.5): 信任口径全部由服务端决定（tier/分数/豁免都在那边），面板只显示与转发。
    private static async System.Threading.Tasks.Task LoadTrustForTask(string taskId)
    {
        assistedTrust = 0; assistedTrustMode = "step"; assistedWaived = false; assistedModule = ""; assistedTrustNote = "";
        try
        {
            var r = await RelayGetWithSessionAsync("/trust?taskId=" + Uri.EscapeDataString(taskId));
            if (!r.ok) return;
            using var doc = JsonDocument.Parse(r.body);
            var root = doc.RootElement;
            assistedTrust = (int)GetLong(root, "trust");
            assistedTrustMode = GetStr(root, "confirmMode");
            assistedWaived = root.TryGetProperty("waived", out var w) && w.ValueKind == JsonValueKind.True;
            assistedModule = GetStr(root, "module");
            if (root.TryGetProperty("trace", out var tr) && tr.ValueKind == JsonValueKind.Array && tr.GetArrayLength() > 0)
            {
                var last = tr[tr.GetArrayLength() - 1];
                var reason = GetStr(last, "reason");
                var at = GetStr(last, "at");
                if (at.Length >= 16) at = at.Substring(5, 11);   // MM-DDTHH:mm
                assistedTrustNote = reason + " (" + at + ")";
            }
        }
        catch (Exception ex) { PluginLog.Warn("Palette", "trust load failed: " + ex.Message); }
    }

    private static string TrustWord()
    {
        if (assistedWaived) return Loc.T("本会话免确认", "no-ask this session");
        switch (assistedTrustMode)
        {
            case "optional": return Loc.T("免确认（可勾选本会话不再问）", "optional (can tick 'do not ask this session')");
            case "merged": return Loc.T("合并确认", "merged confirm");
            default: return Loc.T("逐步确认", "step by step");
        }
    }

    private static async void ShowTrustTrace(WorkflowRow row)
    {
        if (row == null) { SetToolsHint(Loc.T("先选一条记录", "Select a record first")); return; }
        await LoadTrustForTask(row.Id);
        var head = Loc.T($"信任 {assistedTrust}/5 · {TrustWord()} · 模块 {assistedModule}",
            $"trust {assistedTrust}/5 · {TrustWord()} · module {assistedModule}");
        var tail = assistedTrustNote.Length > 0 ? Loc.T(" · 最近：", " · last: ") + assistedTrustNote : "";
        SetToolsHint(head + tail);
        PluginLog.Info("Palette", "trust trace " + row.Id + " module=" + assistedModule + " trust=" + assistedTrust +
            " mode=" + assistedTrustMode + " waived=" + assistedWaived);
    }

    private static async void ClearTrustForRow(WorkflowRow row)
    {
        if (row == null) { SetToolsHint(Loc.T("先选一条记录", "Select a record first")); return; }
        try
        {
            await LoadTrustForTask(row.Id);
            if (assistedModule.Length == 0) { SetToolsHint(Loc.T("这条记录没有模块归属，无法清零", "This record has no module yet")); return; }
            var body = "{\"module\":\"" + assistedModule + "\"}";
            using var req = new HttpRequestMessage(HttpMethod.Post, relayUrl + "/trust/clear");
            req.Headers.TryAddWithoutValidation("x-cad-session", cadSessionId);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await wfHc.SendAsync(req);
            var txt = await resp.Content.ReadAsStringAsync();
            PluginLog.Info("Palette", "trust cleared: " + assistedModule + " -> " + txt.Substring(0, Math.Min(120, txt.Length)));
            assistedTrust = 0; assistedTrustMode = "step"; assistedWaived = false;
            SetToolsHint(Loc.T("信任已清零：这个模块下次回放回到逐步确认", "Trust cleared: this module is back to step-by-step"));
        }
        catch (Exception ex)
        {
            PluginLog.Warn("Palette", "trust clear failed: " + ex.Message);
            SetToolsHint(Loc.T("清零失败：", "Clear failed: ") + ex.Message);
        }
    }
    private static string TierOf(int i)
    {
        foreach (var r in assistedRows) if (r.I == i) return r.Tier;
        return "";
    }

    // 2026-10-23 (Bro: the hints must exist in both languages and follow the panel language): the
    // relay sends a stable code; the sentence is the UI's job so it can follow Loc.T.
    private static string ReplayBlockedText(string code, string fallback, string? layer = null)
    {
        switch ((code ?? "").Trim().ToLowerInvariant())
        {
            case "external-handle-other-drawing":
                return Loc.T("\u8fd9\u4e00\u6b65\u8981\u7528\u7684\u53e5\u67c4\u4e0d\u662f\u8fd9\u6761\u5de5\u4f5c\u6d41\u81ea\u5df1\u5efa\u7684\uff0c\u800c\u4e14\u56fe\u7eb8\u5df2\u7ecf\u6362\u4e86",
                    "This step reads a handle that the workflow did not create, and the drawing has changed");
            case "failed-in-record":
                return Loc.T("\u8bb0\u5f55\u91cc\u8fd9\u4e00\u6b65\u672c\u6765\u5c31\u5931\u8d25\u4e86\uff0c\u4e0d\u62ff\u6765\u56de\u653e",
                    "This step failed in the record, so it is not replayed");
            case "blocked-or-collapsed":
                return Loc.T("\u8fd9\u4e00\u6b65\u5728\u8bb0\u5f55\u91cc\u88ab\u6298\u53e0\u6216\u62e6\u622a\u4e86",
                    "This step was collapsed or blocked in the record");
            // 2026-11-04（预检前置）：开跑前就拦住，比跑到一半炸掉更好解释
            case "layer-missing":
                return Loc.T("\u8fd9\u6761\u8bb0\u5f55\u8981\u7528\u56fe\u5c42\u300c" + (layer ?? "") + "\u300d\uff0c\u5f53\u524d\u56fe\u91cc\u6ca1\u6709 \u2014\u2014 \u5148\u5efa\u597d\u56fe\u5c42\uff08\u6216\u6362\u5230\u6709\u5b83\u7684\u56fe\uff09\u518d\u56de\u653e",
                    "This record needs layer \"" + (layer ?? "") + "\" which this drawing does not have -- create it (or open a drawing that has it) before replaying");
            case "local-tool":
                return Loc.T("\u8fd9\u662f\u9762\u677f\u81ea\u5e26\u7684\u5de5\u5177\uff0c\u4e0d\u662f CAD \u64cd\u4f5c\uff0c\u6ca1\u6cd5\u56de\u653e",
                    "This is a panel-local tool, not a CAD action, so it cannot be replayed");
            default:
                return fallback ?? "";
        }
    }

    private static void StartAssisted(WorkflowRow row, Dictionary<string, object> bindings)
    {
        if (row == null) { SetToolsHint(Loc.T("\u5148\u9009\u4e00\u6761\u8bb0\u5f55", "Select a record first")); return; }
        if (toolsAssistedActive) return;

        var payload = new Dictionary<string, object> { { "mode", "assisted" } };
        if (bindings != null && bindings.Count > 0) payload["bindings"] = bindings;
        var body = JsonSerializer.Serialize(payload);
        var url = relayUrl + "/workflows/" + Uri.EscapeDataString(row.Id) + "/replay";
        var run = new Func<System.Threading.Tasks.Task>(async () =>
        {
            try
            {
                using var resp = await wfHc.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
                var txt = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;
                // 2026-11-04: a record with no drawing identity is refused by the relay.
                // Say that in plain words instead of dumping the JSON at the user.
                if (GetStr(root, "error") == "record-has-no-identity")
                {
                    SetToolsHint(Loc.T("\u8fd9\u6761\u8bb0\u5f55\u6ca1\u8bb0\u4e0b\u662f\u5728\u54ea\u5f20\u56fe\u91cc\u5f55\u7684\uff0c\u65e0\u6cd5\u8bc1\u660e\u5c31\u662f\u73b0\u5728\u8fd9\u5f20\u56fe\uff0c\u6240\u4ee5\u4e0d\u653e\u56de\u653e\u3002\u8981\u7528\u5b83\uff0c\u5148\u5728\u5f53\u524d\u56fe\u91cd\u65b0\u8dd1\u4e00\u904d\u5f55\u4e00\u6761\u3002",
                        "This record never stored which drawing it was recorded in, so it cannot be proven to belong to the open drawing. Re-record it in this drawing to replay it."));
                    return;
                }
                if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                {
                    SetToolsHint(Loc.T("\u56de\u653e\u5f00\u4e0d\u4e86\uff1a", "Cannot start replay: ") + txt.Substring(0, Math.Min(120, txt.Length)));
                    return;
                }
                assistedToken = GetStr(root, "token");
                assistedTaskId = row.Id;
                assistedRows.Clear();
                assistedCursor = 0;
                assistedTotal = 0;
                // 2026-10-23 (Bro: changing a parameter and replaying "did not work"): the replay was
                // refused because the record was made in another drawing, and the old message did not
                // say which drawing. Say it in plain language instead of the internal rule text.
                if (root.TryGetProperty("documentMismatch", out var dm) && dm.ValueKind == JsonValueKind.Object)
                {
                    var recordedDoc = GetStr(dm, "recorded");
                    var currentDoc2 = GetStr(dm, "current");
                    SetToolsHint(Loc.T(
                        $"\u8fd9\u6761\u8bb0\u5f55\u662f\u5728\u300c{recordedDoc}\u300d\u91cc\u5f55\u7684\uff0c\u73b0\u5728\u8fd9\u5f20\u56fe\u662f\u300c{currentDoc2}\u300d\u2014\u2014\u53e5\u67c4\u5bf9\u4e0d\u4e0a\uff0c\u56de\u5230\u90a3\u5f20\u56fe\u518d\u56de\u653e",
                        $"This record was made in \"{recordedDoc}\", the open drawing is \"{currentDoc2}\" -- the handles belong to the other file, open that one to replay"));
                    return;
                }
                if (root.TryGetProperty("plan", out var plan))
                {
                    if (plan.TryGetProperty("blocked", out var blocked) && blocked.ValueKind == JsonValueKind.Array && blocked.GetArrayLength() > 0)
                    {
                        var first = blocked[0];
                        SetToolsHint(Loc.T("\u4e0d\u80fd\u56de\u653e\uff1a", "Cannot replay: ") + ReplayBlockedText(GetStr(first, "code"), GetStr(first, "why"), GetStr(first, "layer")));
                        return;
                    }
                    if (plan.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in steps.EnumerateArray())
                        {
                            var r = new AssistedRow
                            {
                                I = (int)GetLong(el, "i"),
                                Method = GetStr(el, "method"),
                                Tier = GetStr(el, "tier"),
                                Params = ParamsBrief(el),
                                Desc = StepSentence(el),
                                Status = Loc.T("\u5f85\u6267\u884c", "pending"),
                            };
                            assistedRows.Add(r);
                            assistedTotal++;
                        }
                    }
                }
                if (assistedRows.Count == 0) { SetToolsHint(Loc.T("\u8fd9\u6761\u8bb0\u5f55\u6ca1\u6709\u53ef\u56de\u653e\u7684\u6b65\u9aa4", "This record has no replayable steps")); return; }
                assistedCursor = (int)GetLong(root.TryGetProperty("progress", out var pr) ? pr : default, "cursor");
                if (assistedCursor < 0) assistedCursor = 0;
                if (!toolsViewState.TrySwitch(ToolsView.Workflows)) { PluginLog.Warn("Palette", "assisted replay refused outside the workflow view"); return; }
                toolsViewState.BeginAssisted();
                PluginLog.Info("Palette", "assisted replay started: " + row.Id + " steps=" + assistedTotal);
                UpdateToolsModeButton();
                RebuildToolsColumns();
                RefreshToolsGrid();
                await LoadTrustForTask(row.Id);   // 2026-11-02 (P2.5): 信任决定确认方式
                UpdateToolsCount();
                SetToolsHint(Loc.T("assisted \u56de\u653e\uff1a\u70b9\u300c\u6279\u51c6\u300d\u6267\u884c\u4e0b\u4e00\u6b65\uff0c\u300c\u505c\u6b62\u300d\u6536\u5de5", "assisted replay: Approve runs the next step, Stop ends it"));
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Palette", "assisted start failed: " + ex.Message);
                SetToolsHint(Loc.T("\u56de\u653e\u5f00\u4e0d\u4e86\uff1a", "Cannot start replay: ") + ex.Message);
            }
        });
        _ = run();
    }

    // the relay already phrased this step; use it verbatim (no client-side wording)
    private static string StepSentence(JsonElement step)
    {
        if (!step.TryGetProperty("desc", out var d)) return "";
        return Loc.IsEnglish ? GetStr(d, "en") : GetStr(d, "zh");
    }

    private static string ParamsBrief(JsonElement step)
    {
        if (!step.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object) return "";
        var sb = new StringBuilder();
        foreach (var kv in p.EnumerateObject())
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(kv.Name);
            if (kv.Value.ValueKind == JsonValueKind.String) { sb.Append('=').Append(kv.Value.GetString()); }
            else if (kv.Value.ValueKind == JsonValueKind.Array) { sb.Append("[").Append(kv.Value.GetArrayLength()).Append("]"); }
            else if (kv.Value.ValueKind == JsonValueKind.Number || kv.Value.ValueKind == JsonValueKind.True || kv.Value.ValueKind == JsonValueKind.False) { sb.Append('=').Append(kv.Value.ToString()); }
            if (sb.Length > 60) { sb.Append("\u2026"); break; }
        }
        return sb.ToString();
    }

    private static void ApproveNextStep()
    {
        if (!toolsAssistedActive) return;
        var next = NextAssistedIndex();
        if (next < 0) { FinishAssisted(null); return; }

        var body = "{\"token\":\"" + assistedToken + "\",\"i\":" + next + ",\"approve\":true}";
        var url = relayUrl + "/workflows/" + Uri.EscapeDataString(assistedTaskId) + "/replay/step";
        var run = new Func<System.Threading.Tasks.Task>(async () =>
        {
            try
            {
                using var resp = await wfHc.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
                var txt = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;
                var status = GetStr(root, "status");
                var row = FindAssistedRow(next);
                if (row != null)
                {
                    if (status == "ok")
                    {
                        var drift = root.TryGetProperty("result", out var r0) && r0.TryGetProperty("drift", out var d) && d.ValueKind == JsonValueKind.True;
                        row.Status = drift ? Loc.T("\u6210\u529f\uff08\u6f02\u79fb\uff09", "ok (drift)") : Loc.T("\u6210\u529f", "ok");
                        row.Drifted = drift;
                        row.Done = true;
                    }
                    else if (status == "error")
                    {
                        var code = root.TryGetProperty("result", out var r1) ? GetStr(r1, "errCode") : "";
                        row.Status = Loc.T("\u5931\u8d25", "failed") + (code.Length > 0 ? " " + code : "");
                        row.Failed = true;
                        row.Done = true;
                    }
                    else if (status == "needs-remap")
                    {
                        row.Status = Loc.T("\u53e5\u67c4\u65e0\u6cd5\u91cd\u6620\u5c04", "handle not remappable");
                        row.Failed = true;
                        row.Done = true;
                    }
                    else
                    {
                        row.Status = status;
                    }
                }
                if (root.TryGetProperty("progress", out var pr)) assistedCursor = (int)GetLong(pr, "cursor");
                var done = root.TryGetProperty("done", out var dn) && dn.ValueKind == JsonValueKind.True;
                // 2026-11-04: 中止也要说清这张图上已经改了多少步（半成品不能只报一句失败）
                var executedWrites = (int)GetLong(root, "executedWrites");
                var aborted = GetStr(root, "aborted");
                if (aborted.Length > 0 && executedWrites > 0)
                    aborted += Loc.T($"\uff08\u672c\u6b21\u5df2\u6267\u884c {executedWrites} \u6b65\u5199\u5165\uff0c\u53ef\u5728\u65e5\u5fd7\u6a21\u5f0f\u6309\u8d26\u672c\u64a4\u56de\uff09", $" ({executedWrites} write step(s) were applied -- undo them from the journal)");
                if (done || aborted.Length > 0) { FinishAssisted(aborted.Length > 0 ? aborted : null); return; }
                RefreshToolsGrid();
                UpdateToolsCount();
                // 2026-11-02 (P2.5) 合并确认：信任够高时，这一次批准把后续「非破坏性」步骤一起跑完。
                // 破坏性（C 档）永远单独确认 —— INV1；服务端还会再判一次，这里只是不替用户省那一步。
                if (assistedTrustMode == "merged" || assistedTrustMode == "optional")
                {
                    var nx = NextAssistedIndex();
                    if (nx > 0 && TierOf(nx) != "C")
                    {
                        SetToolsHint(Loc.T("合并确认：继续执行第 " + nx + " 步（遇到破坏性步骤会停下来问）",
                            "merged confirm: continuing at step " + nx + " (stops to ask before a destructive step)"));
                        ApproveNextStep();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Palette", "assisted step failed: " + ex.Message);
                SetToolsHint(Loc.T("\u6b65\u9aa4\u6267\u884c\u5931\u8d25\uff1a", "Step failed: ") + ex.Message);
            }
        });
        _ = run();
    }

    private static AssistedRow FindAssistedRow(int i)
    {
        foreach (var r in assistedRows) if (r.I == i) return r;
        return null;
    }

    private static void FinishAssisted(string aborted)
    {
        // 2026-11-04: two approve requests can both observe "done" -- the second one used to
        // log "assisted replay finished: task=" with an empty id and rebuild the grid again.
        if (!toolsAssistedActive) return;
        assistedToken = "";
        assistedCursor = assistedTotal;
        if (aborted != null) SetToolsHint(Loc.T("\u56de\u653e\u4e2d\u6b62\uff1a", "Replay aborted: ") + aborted);
        else SetToolsHint(Loc.T("\u56de\u653e\u5b8c\u6210", "Replay finished"));
        PluginLog.Info("Palette", "assisted replay finished: task=" + assistedTaskId + " aborted=" + (aborted ?? ""));
        toolsViewState.EndAssisted();
        assistedTaskId = "";
        UpdateToolsModeButton();
        workflowLoaded = false;          // the run may have produced a new record
        RebuildToolsColumns();
        EnsureWorkflowsLoaded(true);
        RefreshToolsGrid();
        UpdateToolsCount();
    }

    private static void StopAssisted()
    {
        if (!toolsAssistedActive) return;
        PluginLog.Info("Palette", "assisted replay stopped by user");
        SetToolsHint(Loc.T("\u5df2\u505c\u6b62\uff08\u540e\u7eed\u6b65\u9aa4\u672a\u6267\u884c\uff09", "Stopped (remaining steps were not run)"));
        toolsViewState.EndAssisted();
        assistedToken = "";
        assistedTaskId = "";
        assistedRows.Clear();
        assistedCursor = 0;
        assistedTotal = 0;
        UpdateToolsModeButton();
        RebuildToolsColumns();
        RefreshToolsGrid();
        UpdateToolsCount();
    }
}
