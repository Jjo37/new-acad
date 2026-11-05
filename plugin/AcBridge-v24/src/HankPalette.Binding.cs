using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Civil3DMcpPlugin;

// 2026-10-22 P2.4: binding form -- one row per recorded step, parameters as "key=value, key=value".
//
// Bro's requirements after the first real run:
//   * he never wants to type a command to bring this up (a button must do it)
//   * "18 rows of raw keys is unreadable" -> one row per STEP, all of that step's numbers/names
//     on one editable line, with the step's action spelled out in plain language
//   * edits must survive a repaint (CellValueChanged writes back; list auto-refresh is paused)
//   * no modal windows (ADR 0006)
public static partial class HankPalette
{
    private sealed class SlotRow
    {
        public string Step = "";
        public string Title = "";                 // plain-language action
        public string Text = "";                  // editable "key=value, key=value"
        public string Note = "";
        public List<string> Keys = new();         // expected keys, in order
        public List<string> Ids = new();          // slot id per key
        public List<bool> Numeric = new();
    }

    private static readonly List<SlotRow> bindingRows = new();
    private static string bindingTaskId = "";
    private static string bindingHint = "";
    private static bool bindingEventsAttached;

    private static readonly Dictionary<string, string> ActionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["createSphere"] = "\u753b\u7403",
        ["createCylinder"] = "\u753b\u5706\u67f1",
        ["createCircle"] = "\u753b\u5706",
        ["createLineSegment"] = "\u753b\u7ebf",
        ["createLayer"] = "\u5efa\u56fe\u5c42",
        ["createPolyline"] = "\u753b\u591a\u6bb5\u7ebf",
        ["createCogoPoints"] = "\u5efa\u70b9",
        ["offsetEntity"] = "\u504f\u79fb",
        ["deleteEntities"] = "\u5220\u9664\u56fe\u5143",
        ["getEntitiesByLayer"] = "\u6e05\u70b9\u56fe\u5c42\u91cc\u7684\u56fe\u5143",
        ["getEntityInfo"] = "\u8bfb\u56fe\u5143\u4fe1\u606f",
        ["listMethods"] = "\u67e5\u65b9\u6cd5\u8868",
        ["moveEntity"] = "\u79fb\u52a8",
        ["copyEntity"] = "\u590d\u5236",
        ["setEntitiesColor"] = "\u6539\u989c\u8272",
        ["booleanUnion"] = "\u5408\u5e76",
        ["assertChecks"] = "\u6821\u9a8c",
        ["verifyAssemblyGeometry"] = "\u6821\u9a8c\u88c5\u914d",
    };

    private static string ActionOf(string method)
    {
        string w;
        return ActionWords.TryGetValue(method ?? "", out w) ? w : (method ?? "");
    }

    private static string TierWord(string tier)
    {
        if (tier == "A") return Loc.T("\u53ea\u8bfb", "read");
        if (tier == "C") return Loc.T("\u7834\u574f\u6027", "destructive");
        return Loc.T("\u53ef\u64a4\u9500", "reversible");
    }

    private static void BuildBindingColumns()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        toolsGrid.Columns.Add(NewCol(Loc.T("\u6b65", "Step"), 7));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u8fd9\u4e00\u6b65\u5e72\u4ec0\u4e48", "What this step does"), 24));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u53c2\u6570\uff08\u53ef\u6539\uff09", "Parameters (editable)"), 46));
        toolsGrid.Columns.Add(NewCol(Loc.T("\u539f\u8bb0\u5f55", "Recorded"), 20));
        AttachBindingGridEvents();
    }

    private static void AttachBindingGridEvents()
    {
        if (bindingEventsAttached || toolsGrid == null || toolsGrid.IsDisposed) return;
        bindingEventsAttached = true;
        toolsGrid.CellValueChanged += (_, e) =>
        {
            if (!toolsBindingActive || e.RowIndex < 0 || e.ColumnIndex != 2) return;
            if (e.RowIndex >= bindingRows.Count) return;
            var text = toolsGrid.Rows[e.RowIndex].Cells[2].Value as string ?? "";
            bindingRows[e.RowIndex].Text = text.Trim();
        };
        toolsGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (toolsGrid.IsCurrentCellDirty) toolsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
    }

    private static void RenderBindingRows()
    {
        if (toolsGrid == null || toolsGrid.IsDisposed) return;
        toolsGrid.RowCount = 0;
        foreach (var r in bindingRows)
        {
            var idx = toolsGrid.Rows.Add(r.Step, r.Title, r.Text, r.Note);
            var row = toolsGrid.Rows[idx];
            row.Cells[0].ReadOnly = true;
            row.Cells[1].ReadOnly = true;
            row.Cells[3].ReadOnly = true;
            row.Tag = r;
        }
    }

    private static void UpdateBindingCount()
    {
        if (toolsCount == null || toolsCount.IsDisposed) return;
        toolsCount.Text = Loc.T(
            $"\u8981\u586b {bindingRows.Count} \u4e2a\u6b65\u9aa4 \u00b7 \u6539\u5b8c\u70b9\u300c\u5f00\u59cb\u56de\u653e\u300d \u00b7 {bindingHint}",
            $"fill {bindingRows.Count} step(s) \u00b7 edit then press Start \u00b7 {bindingHint}");
    }

    private static void StartAssistedFlow(WorkflowRow row)
    {
        if (row == null) { SetToolsHint(Loc.T("\u5148\u9009\u4e00\u6761\u8bb0\u5f55", "Select a record first")); return; }
        if (toolsAssistedActive || toolsBindingActive) return;

        var url = relayUrl + "/workflows/" + Uri.EscapeDataString(row.Id);
        var run = new Func<System.Threading.Tasks.Task>(async () =>
        {
            try
            {
                using var resp = await wfHc.GetAsync(url);
                var txt = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;
                if (!root.TryGetProperty("record", out var rec))
                {
                    SetToolsHint(Loc.T("\u8bfb\u4e0d\u5230\u8fd9\u6761\u8bb0\u5f55\uff1a", "Cannot read the record: ") + txt.Substring(0, Math.Min(120, txt.Length)));
                    return;
                }
                bindingRows.Clear();
                var descMap = BuildStepDescriptions(rec);
                if (rec.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                    bindingRows.AddRange(BuildSlotRows(slots, descMap));

                if (bindingRows.Count == 0)
                {
                    StartAssisted(row, null);
                    return;
                }
                bindingTaskId = row.Id;
                if (!toolsViewState.TrySwitch(ToolsView.Workflows)) { PluginLog.Warn("Palette", "binding form refused outside the workflow view"); return; }
                toolsViewState.BeginBinding();
                bindingHint = Loc.T("\u6bcf\u884c\u662f\u4e00\u6b65\uff0c\u683c\u5f0f key=\u503c", "one row per step, key=value");
                PluginLog.Info("Palette", "binding form opened: " + row.Id + " steps=" + bindingRows.Count);
                UpdateToolsModeButton();
                RebuildToolsColumns();
                RefreshToolsGrid();
                UpdateToolsCount();
                SetToolsHint(Loc.T("\u6539\u5b8c\u53c2\u6570\u70b9\u300c\u5f00\u59cb\u56de\u653e\u300d\uff1b\u60f3\u653e\u5f03\u70b9\u300c\u505c\u6b62\u300d",
                    "Edit the parameters, then press Start replay"));
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Palette", "binding form failed: " + ex.Message);
                SetToolsHint(Loc.T("\u6253\u5f00\u7ed1\u5b9a\u8868\u5931\u8d25\uff1a", "Cannot open the binding form: ") + ex.Message);
            }
        });
        _ = run();
    }

    // step index -> the sentence the relay generated (zh or en per the UI language)
    private static Dictionary<string, string> BuildStepDescriptions(JsonElement rec)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!rec.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return map;
        foreach (var s in steps.EnumerateArray())
        {
            if (!s.TryGetProperty("describe", out var d)) continue;
            var text = Loc.IsEnglish ? GetStr(d, "en") : GetStr(d, "zh");
            if (text.Length == 0) continue;
            map[GetLong(s, "i").ToString()] = text;
        }
        return map;
    }

    // One row per step: "x=0, y=0, z=8, radius=8, layer=TANGHULU_BALL"
    private static List<SlotRow> BuildSlotRows(JsonElement slots, Dictionary<string, string> descMap)
    {
        var order = new List<string>();
        var byStep = new Dictionary<string, SlotRow>(StringComparer.Ordinal);

        foreach (var el in slots.EnumerateArray())
        {
            var step = FirstUsageStep(el);
            SlotRow row;
            if (!byStep.TryGetValue(step, out row))
            {
                string sentence;
                row = new SlotRow { Step = Loc.T($"\u7b2c{step}\u6b65", $"step {step}"), Title = descMap.TryGetValue(step, out sentence) ? sentence : ActionOf(FirstUsageMethod(el)) };
                byStep[step] = row;
                order.Add(step);
            }
            var key = GetStr(el, "key");
            var value = el.TryGetProperty("value", out var v) ? v.ToString() : "";
            row.Keys.Add(key);
            row.Ids.Add(GetStr(el, "id"));
            row.Numeric.Add(GetStr(el, "type") == "number");
            if (row.Text.Length > 0) row.Text += ", ";
            row.Text += key + "=" + value;
        }

        var rows = new List<SlotRow>();
        foreach (var k in order) rows.Add(byStep[k]);
        return rows;
    }

    private static string FirstUsageStep(JsonElement slot)
    {
        if (slot.TryGetProperty("usages", out var usages) && usages.ValueKind == JsonValueKind.Array && usages.GetArrayLength() > 0)
            return GetLong(usages[0], "step").ToString();
        return "?";
    }

    private static string FirstUsageMethod(JsonElement slot)
    {
        if (slot.TryGetProperty("usages", out var usages) && usages.ValueKind == JsonValueKind.Array && usages.GetArrayLength() > 0)
            return GetStr(usages[0], "method");
        return "";
    }

    private static void SubmitBindings()
    {
        if (!toolsBindingActive) return;
        var values = new Dictionary<string, object>();
        var bad = new List<string>();

        for (var i = 0; i < bindingRows.Count; i++)
        {
            var row = bindingRows[i];
            var cellText = (toolsGrid != null && i < toolsGrid.Rows.Count) ? (toolsGrid.Rows[i].Cells[2].Value as string) : null;
            var text = (cellText ?? row.Text ?? "").Trim();
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in text.Split(','))
            {
                var seg = part.Trim();
                if (seg.Length == 0) continue;
                var eq = seg.IndexOf('=');
                if (eq <= 0) { bad.Add(row.Step + ": \u9700\u8981 key=\u503c"); continue; }
                seen[seg.Substring(0, eq).Trim()] = seg.Substring(eq + 1).Trim();
            }
            for (var k = 0; k < row.Keys.Count; k++)
            {
                var key = row.Keys[k];
                string raw;
                if (!seen.TryGetValue(key, out raw) || raw.Length == 0) { bad.Add(row.Step + " " + key); continue; }
                if (row.Numeric[k])
                {
                    double n;
                    if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n))
                    { bad.Add(row.Step + " " + key + "=" + raw); continue; }
                    values[row.Ids[k]] = n;
                }
                else values[row.Ids[k]] = raw;
            }
        }

        if (bad.Count > 0)
        {
            SetToolsHint(Loc.T("\u8fd9\u4e9b\u586b\u5f97\u4e0d\u5bf9\uff1a", "Fix these first: ") + string.Join("\uff1b", bad.ToArray()));
            return;
        }

        var row0 = new WorkflowRow { Id = bindingTaskId };
        toolsViewState.EndBinding();
        PluginLog.Info("Palette", "binding submitted: " + bindingTaskId + " values=" + values.Count);
        StartAssisted(row0, values);
    }

    private static void StopBinding()
    {
        if (!toolsBindingActive) return;
        toolsViewState.EndBinding();
        bindingTaskId = "";
        bindingRows.Clear();
        bindingHint = "";
        UpdateToolsModeButton();
        RebuildToolsColumns();
        RefreshToolsGrid();
        UpdateToolsCount();
        SetToolsHint(Loc.T("\u5df2\u53d6\u6d88\u7ed1\u5b9a", "Binding cancelled"));
    }
}
