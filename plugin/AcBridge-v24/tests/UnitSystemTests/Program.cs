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

Console.WriteLine($"UnitSystemTests: {pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
