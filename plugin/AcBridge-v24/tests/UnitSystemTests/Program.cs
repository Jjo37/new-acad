// UnitSystem 纯函数单测（零 CAD 依赖）—— 用法： dotnet run --project plugin/AcBridge-v24/tests/UnitSystemTests
// 退出码：0 全过；1 有失败。
using Civil3DMcpPlugin;

int pass = 0, fail = 0;
void Check(string name, bool ok)
{
  if (ok) { pass++; return; }
  fail++;
  Console.WriteLine("  FAIL  " + name);
}
void Near(string name, double got, double want, double tol = 1e-9) => Check(name + $" (got {got}, want {want})", Math.Abs(got - want) <= tol);

// 请求级单位覆盖解析
Check("override metric", UnitSystem.TryParseOverride("metric", out var m1) && m1);
Check("override imperial", UnitSystem.TryParseOverride("imperial", out var m2) && !m2);
Check("override 公制", UnitSystem.TryParseOverride("公制", out var m3) && m3);
Check("override 英制", UnitSystem.TryParseOverride("英制", out var m4) && !m4);
Check("override ft", UnitSystem.TryParseOverride("ft", out var m5) && !m5);
Check("override us", UnitSystem.TryParseOverride("us", out var mu) && !mu);
Check("override si", UnitSystem.TryParseOverride("si", out var m6) && m6);
Check("override meters", UnitSystem.TryParseOverride("meters", out var mm) && mm);
Check("override ' METRIC ' (trim+case)", UnitSystem.TryParseOverride("  METRIC ", out var m7) && m7);
Check("override auto → 未命中", !UnitSystem.TryParseOverride("auto", out _));
Check("override null → 未命中", !UnitSystem.TryParseOverride(null, out _));
Check("override 空串 → 未命中", !UnitSystem.TryParseOverride("", out _));

// 公式常数（v1.8.2 单位自适应）
Near("LengthFactor SI", UnitSystem.LengthFactor(true), 1.0);
Near("LengthFactor 英制 = 3.28084", UnitSystem.LengthFactor(false), 3.280839895013123);
Near("Gravity SI = 9.81", UnitSystem.Gravity(true), 9.81);
Near("Gravity 英制 = 32.2", UnitSystem.Gravity(false), 32.2);
Near("Manning SI = 1.0", UnitSystem.ManningConstant(true), 1.0);
Near("Manning 英制 = 1.486", UnitSystem.ManningConstant(false), 1.486);
Near("Manning 比值 = 1.486", UnitSystem.ManningConstant(false) / UnitSystem.ManningConstant(true), 1.486);
Check("VelocityUnits SI", UnitSystem.VelocityUnits(true) == "m/s");
Check("VelocityUnits 英制", UnitSystem.VelocityUnits(false) == "ft/s");
Check("FlowUnits SI", UnitSystem.FlowUnits(true) == "m³/s");
Check("FlowUnits 英制", UnitSystem.FlowUnits(false) == "cfs");

// Insunits 名 → 标签
Check("LinearUnits Meters", UnitSystem.LinearUnits("Meters") == "meters");
Check("LinearUnits Feet", UnitSystem.LinearUnits("Feet") == "feet");
Check("LinearUnits US Survey Feet → feet", UnitSystem.LinearUnits("US Survey Feet") == "feet");
Check("LinearUnits Millimeters → other", UnitSystem.LinearUnits("Millimeters") == "other");
Check("LinearUnits null → other", UnitSystem.LinearUnits(null) == "other");
Check("VolumeUnits Meters", UnitSystem.VolumeUnits("Meters") == "cubic meters");
Check("VolumeUnits Feet", UnitSystem.VolumeUnits("Feet") == "cubic feet");
Check("VolumeUnits US Survey Feet", UnitSystem.VolumeUnits("US Survey Feet") == "cubic feet");
Check("VolumeUnits 未知 → cubic units", UnitSystem.VolumeUnits("Yards") == "cubic units");

bool Throws(Action a) { try { a(); return false; } catch { return true; } }
bool EndsAssisted(PaletteViewState v) { v.EndAssisted(); return !v.AssistedActive && !v.BindingActive; }

// ---------- P2.6 S1: 工具页视图状态只有一个归属 ----------
// 这些断言对应 2026-10-23 的两个真实 bug：首点「调用流」先画成工作流再切模式；
// 从日志视图返回后仍显示日志列。
var vs = new PaletteViewState();
Check("S1 初始视图是 Stream", vs.Current == ToolsView.Stream);
Check("S1 Stream -> Catalog 允许", vs.TrySwitch(ToolsView.Catalog) && vs.Current == ToolsView.Catalog);
Check("S1 Catalog -> Workflows 允许", vs.TrySwitch(ToolsView.Workflows) && vs.Current == ToolsView.Workflows);
Check("S1 原地切换 = 成功且状态不变", vs.TrySwitch(ToolsView.Workflows) && vs.Current == ToolsView.Workflows);
Check("S1 每个视图都能回到 Stream", new[] { ToolsView.Catalog, ToolsView.Workflows, ToolsView.Journal }.All(t => { var v = new PaletteViewState(); return v.TrySwitch(t) && v.TrySwitch(ToolsView.Stream) && v.Current == ToolsView.Stream; }));

// 切换必须清掉上一个视图的临时状态（"离开视图必须清干净"）
var vs2 = new PaletteViewState();
vs2.TrySwitch(ToolsView.Workflows);
vs2.BeginBinding();
Check("S1 切换离开 Workflows 会清掉 binding", vs2.TrySwitch(ToolsView.Journal) && !vs2.BindingActive);
var vs2b = new PaletteViewState();
vs2b.TrySwitch(ToolsView.Workflows);
vs2b.BeginAssisted();
Check("S1 切换离开 Workflows 会清掉 assisted", vs2b.TrySwitch(ToolsView.Journal) && !vs2b.AssistedActive);

// 子状态只能出现在正确的位置
// 2026-10-28 接线修正：参数表是回放**之前**的编辑态（改完参数再点"开始回放"），
// 所以它不要求 assisted 会话；但回放进行中不能再改表，开始回放会把表收起。
var vs3 = new PaletteViewState();
Check("S1 非 Workflows 视图不允许开回放会话", Throws(() => vs3.BeginAssisted()));
Check("S1 非 Workflows 视图不允许开参数表", Throws(() => vs3.BeginBinding()));
vs3.TrySwitch(ToolsView.Workflows);
Check("S1 工作流视图里可以先开参数表", !Throws(() => vs3.BeginBinding()) && vs3.BindingActive);
Check("S1 开始回放会收起参数表", !Throws(() => vs3.BeginAssisted()) && vs3.AssistedActive && !vs3.BindingActive);
Check("S1 回放进行中不能再开参数表", Throws(() => vs3.BeginBinding()));
Check("S1 结束回放会收起参数表", EndsAssisted(vs3));

// 转换表完整性：4 个视图两两都能切（当前版本），且自己到自己是恒等
Check("S1 转换表覆盖 4x4", new[] { ToolsView.Stream, ToolsView.Catalog, ToolsView.Workflows, ToolsView.Journal }
  .All(a => new[] { ToolsView.Stream, ToolsView.Catalog, ToolsView.Workflows, ToolsView.Journal }
    .All(b => PaletteViewState.IsAllowed(a, b))));

// ---------- P2.6 S4: 破坏性动作只有一个确认守卫 ----------
// 对应 2026-10-23 的两个真实缺口：「清空回退清单」一键到底；「清计划」「清撤不回的记录」一句确认都没有。
var fakeNow = new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Local);
var guard = new DestructiveGuard(() => fakeNow, 8);
Check("S4 第一次请求只瞄准（Armed）", guard.Request("journal-clear-all") == GuardDecision.Armed && guard.IsArmed("journal-clear-all"));
Check("S4 TTL 内第二次请求 → Confirmed", guard.Request("journal-clear-all") == GuardDecision.Confirmed);
Check("S4 Confirmed 后立即复位", !guard.IsArmed("journal-clear-all") && guard.ArmedAction == "");
Check("S4 第三次请求重新 Armed（确认只能消费一次）", guard.Request("journal-clear-all") == GuardDecision.Armed);
fakeNow = fakeNow.AddSeconds(9);
Check("S4 超过 TTL 不再有效（重来一次）", guard.Request("journal-clear-all") == GuardDecision.Armed && guard.IsArmed("journal-clear-all"));
Check("S4 未瞄准的动作 SecondsLeft 为 null", guard.SecondsLeft("never-armed") == null);
guard.Reset();
Check("S4 Reset 后没有任何动作被瞄准", guard.ArmedAction == "" && !guard.IsArmed("journal-clear-all"));
Check("S4 瞄准 A 不能放行 B（任一时刻只有一个 Armed）", guard.Request("plan-clear") == GuardDecision.Armed && guard.Request("project-clear") == GuardDecision.Armed && guard.ArmedAction == "project-clear");
Check("S4 切换瞄准目标后 A 已失效", !guard.IsArmed("plan-clear"));
Check("S4 空 id → Blocked 且不改变已有瞄准（fail-closed）", guard.Request("") == GuardDecision.Blocked && guard.Request("   ") == GuardDecision.Blocked && guard.ArmedAction == "project-clear");
Check("S4 Request 不抛（null 视为空）", guard.Request(null!) == GuardDecision.Blocked);
var guard1s = new DestructiveGuard(() => fakeNow, 1);
guard1s.Request("x");
fakeNow = fakeNow.AddSeconds(2);
Check("S4 1 秒 TTL 两秒后失效（重新瞄准）", guard1s.Request("x") == GuardDecision.Armed);


// ---------- P2.6 S6: 换图/关图的生命周期处置 ----------
// 对应真实缺口：UndoJournal 只在 MCP 的 newDrawing/openDrawing 里被清，用户自己切图就绕过了。
Check("S6 同一个文档对象：什么都不清（重复激活不能白扔 undo 历史）",
  new Func<bool>(() => { var p = DocLifecyclePolicy.DecideOnActivation(true, "A.dwg", "A.dwg"); return !p.ClearJournal && !p.DropSelection && p.Reason == "same-document"; })());
Check("S6 换图 + 快照同名：清日志，保留选择集",
  new Func<bool>(() => { var p = DocLifecyclePolicy.DecideOnActivation(false, "A.dwg", "A.dwg"); return p.ClearJournal && !p.DropSelection; })());
Check("S6 换图 + 快照来自别的图：日志和选择集都清",
  new Func<bool>(() => { var p = DocLifecyclePolicy.DecideOnActivation(false, "A.dwg", "B.dwg"); return p.ClearJournal && p.DropSelection && p.Reason == "switch-stale-selection"; })());
Check("S6 名字大小写不同不算跨图",
  new Func<bool>(() => !DocLifecyclePolicy.DecideOnActivation(false, "a.DWG", "A.dwg").DropSelection)());
Check("S6 快照来源未知（旧格式）：按最坏情况丢",
  new Func<bool>(() => DocLifecyclePolicy.DecideOnActivation(false, "", "B.dwg").DropSelection)());
Check("S6 当前文档名读不出来：日志清 + 快照丢（fail-safe）",
  new Func<bool>(() => { var p = DocLifecyclePolicy.DecideOnActivation(false, "A.dwg", ""); return p.ClearJournal && p.DropSelection && p.Reason == "switch-unknown-document"; })());
Check("S6 换图必然清日志（句柄是 per-drawing 的）",
  new Func<bool>(() => DocLifecyclePolicy.DecideOnActivation(false, null!, null!).ClearJournal)());


// ---------- P2.6 S7: 时间只有一种存法 ----------
// 对应真实事故：工作流 id / 目录用本地时间拼，换时区后新记录沉底、prune 先删最新。
var fixedUtc = new DateTime(2026, 10, 31, 23, 30, 0, DateTimeKind.Utc);
Check("S7 存法是 UTC ISO（带 Z）", PluginTime.StoredNow().EndsWith("Z") && PluginTime.IsStoredUtc(PluginTime.StoredNow()));
Check("S7 校验器识破本地偏移", !PluginTime.IsStoredUtc("2026-10-31T23:30:00+08:00"));
Check("S7 校验器识破缺 Z", !PluginTime.IsStoredUtc("2026-10-31T23:30:00.000"));
Check("S7 校验器识破空值", !PluginTime.IsStoredUtc("") && !PluginTime.IsStoredUtc(null));
Check("S7 校验器接受合法存法", PluginTime.IsStoredUtc("2026-10-31T23:30:00.000Z"));
Check("S7 日期戳是 UTC（跨时区一致）", PluginTime.DateStamp(fixedUtc) == "20261031" && PluginTime.DateStamp(fixedUtc.ToLocalTime()) == "20261031");
Check("S7 文件时间戳是 UTC", PluginTime.FileStamp(fixedUtc) == "20261031-233000");
Check("S7 trace 文件名从 UTC 派生", PluginTime.TraceFileName(fixedUtc) == "trace-20261031.jsonl");
Check("S7 展示用本地时间且格式固定", System.Text.RegularExpressions.Regex.IsMatch(PluginTime.LocalDisplay(fixedUtc), @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"));
Check("S7 展示（不含秒）格式也固定", System.Text.RegularExpressions.Regex.IsMatch(PluginTime.LocalDisplay(fixedUtc, false), @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$"));

// 错误码映射（纯函数）—— 2026-10-29：能力缺口曾经硬编码 -32603（= JSON-RPC "internal error"，
// 跟真崩溃同号），导致靠数字码判读的客户端分不清"缺 Civil 3D"和"插件崩了"。
Check("错误码 CIVIL_NOT_INSTALLED 走映射表得 -32000", JsonRpcProtocol.NumericErrorCode("CIVIL3D.CIVIL_NOT_INSTALLED") == -32000);
Check("错误码 CIVIL_NOT_INSTALLED 不再撞 INTERNAL_ERROR(-32603)", JsonRpcProtocol.NumericErrorCode("CIVIL3D.CIVIL_NOT_INSTALLED") != -32603);
Check("错误码 NO_DRAWING 仍是 -32001", JsonRpcProtocol.NumericErrorCode("CIVIL3D.NO_DRAWING") == -32001);
Check("错误码 INVALID_INPUT 仍是 -32602", JsonRpcProtocol.NumericErrorCode("CIVIL3D.INVALID_INPUT") == -32602);
Check("未知域码回退 -32000", JsonRpcProtocol.NumericErrorCode("CIVIL3D.SOMETHING_NEW") == -32000);

Console.WriteLine($"UnitSystemTests: {pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
