using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// HostModeProbe — v1.11.0-fix
// Purpose: prove that the AutoCAD-only command handlers of Civil3DMcpPlugin can be JIT-compiled in
// a process where the Civil 3D managed assemblies (AeccDbMgd / AeccPressurePipesMgd / AecBaseMgd)
// do not exist. A JIT-time type resolution failure is thrown at the CALL SITE, so the callee can
// never catch it — that is exactly how the plugin silently died in plain AutoCAD.
//
// Scope note: only leaf handlers are prepared here. Shared plumbing (CivilExecution 3-arg core,
// CommandDispatcher) is covered by the source audit in tools/audit-host-modes.js, because
// preparing AutoCAD types outside AutoCAD can fault in native signature resolution.
//
// Exit code 0 = clean; 1 = a CAD-path handler still needs Civil 3D.

var pluginDll = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "Civil3DMcpPlugin.dll");
if (!File.Exists(pluginDll))
{
  Console.Error.WriteLine("plugin dll not found: " + pluginDll);
  return 2;
}

var asm = Assembly.LoadFrom(pluginDll);

var civilSupport = asm.GetType("Civil3DMcpPlugin.CivilSupport");
var availableProp = civilSupport?.GetProperty("Available", BindingFlags.Public | BindingFlags.Static);
var detailProp = civilSupport?.GetProperty("Detail", BindingFlags.Public | BindingFlags.Static);
bool civilAvailable = (bool)(availableProp?.GetValue(null) ?? true);
Console.WriteLine("[env] civilAvailable=" + civilAvailable + " detail=" + detailProp?.GetValue(null));
if (civilAvailable)
{
  Console.Error.WriteLine("[FAIL] this probe must run WITHOUT Civil assemblies; AeccDbMgd was resolvable.");
  return 2;
}

var cadTypes = new[]
{
  "AcadCommands", "HatchCommands", "EditCommands", "FileFormatCommands", "PdfCommands",
  "TraceCommands", "TraceQueryCommands", "SummarizeCommands", "StmCommands", "UndoJournal",
  "JobCommands", "MassHaulCommands", "UndoJournalCommands"
};

var drawingSafeMembers = new HashSet<string>(StringComparer.Ordinal)
{
  "GetDrawingInfoAsync", "SaveDrawingAsync", "NewDrawingAsync", "OpenDrawingAsync",
  "UndoDrawingAsync", "RedoDrawingAsync", "ListTextsAsync", "ListSelectedTextsAsync",
  "GetSelectedCivilObjectsInfoAsync", "ListCivilObjectTypesAsync", "CreateCircleAsync",
  "CreateEllipseAsync", "BuildDrawingInfoCore", "BuildCivilObjectTypes", "BuildSelectedCivilObjects"
};

int jitted = 0, failed = 0, skipped = 0;
var failures = new List<string>();

void TryJit(MethodBase m, string label)
{
  try
  {
    var mi = m as MethodInfo;
    var ci = m as ConstructorInfo;
    if (mi != null)
    {
      if (mi.IsAbstract || mi.ContainsGenericParameters) { skipped++; return; }
      RuntimeHelpers.PrepareMethod(mi.MethodHandle);
    }
    else if (ci != null)
    {
      if (ci.ContainsGenericParameters) { skipped++; return; }
      RuntimeHelpers.PrepareMethod(ci.MethodHandle);
    }
    else { skipped++; return; }
    jitted++;
  }
  catch (Exception ex)
  {
    failed++;
    var root = ex;
    while (root.InnerException != null) root = root.InnerException;
    failures.Add(label + " -> " + root.GetType().Name + ": " + root.Message);
  }
}

Type[] types;
try
{
  types = asm.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
  types = ex.Types.Where(t => t != null).ToArray()!;
  Console.WriteLine("[env] skipped " + (ex.Types.Length - types.Length) + " types whose metadata needs Civil 3D");
}

var consts = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

foreach (var type in types)
{
  if (type.Name == "DrawingCommands")
  {
    foreach (var m in type.GetMethods(consts).Cast<MethodBase>().Concat(type.GetConstructors(consts)))
      if (drawingSafeMembers.Contains(m.Name)) TryJit(m, "DrawingCommands." + m.Name);
    continue;
  }
  if (!cadTypes.Contains(type.Name)) continue;

  // include compiler-generated lambdas (nested closures) so lambda bodies are proven too
  foreach (var t in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
  {
    foreach (var m in t.GetMethods(consts).Cast<MethodBase>().Concat(t.GetConstructors(consts)))
      TryJit(m, t.Name + "." + m.Name);
  }
}

Console.WriteLine("[jit] ok=" + jitted + " failed=" + failed + " skipped=" + skipped);
foreach (var f in failures.Take(25)) Console.WriteLine("  FAIL " + f);

// 3. Regression for the v1.11.0-fix2 bug: listMethods used to collapse to the hardcoded
// AdditionalRegisteredNames (23 entries) because Assembly.GetTypes() throws
// ReflectionTypeLoadException here and the exception was swallowed.
int listed = -1;
string? listError = null;
try
{
  var dispatcherType = asm.GetType("Civil3DMcpPlugin.CommandDispatcher");
  var listMethod = dispatcherType?.GetMethod("ListMethodsAsync", BindingFlags.NonPublic | BindingFlags.Static);
  if (listMethod == null) listError = "ListMethodsAsync not found";
  else
  {
    var task = listMethod.Invoke(null, null) as Task<object?>;
    if (task == null) listError = "unexpected return type";
    else
    {
      task.GetAwaiter().GetResult();
      if (task.Result is System.Collections.IDictionary d && d.Contains("count"))
        listed = Convert.ToInt32(d["count"]);
      else listError = "no count in payload";
    }
  }
}
catch (Exception ex)
{
  var root = ex;
  while (root.InnerException != null) root = root.InnerException;
  listError = root.GetType().Name + ": " + root.Message;
}
Console.WriteLine("[list] listMethods count=" + listed + (listError == null ? "" : " error=" + listError));
if (listed < 300)
{
  Console.Error.WriteLine("[FAIL] listMethods must expose the full method list on a plain-AutoCAD host (got " + listed + ").");
  return 1;
}

if (failed > 0)
{
  Console.Error.WriteLine("[FAIL] AutoCAD-only handlers still depend on Civil 3D assemblies.");
  return 1;
}
Console.WriteLine("[PASS] all AutoCAD-only handlers JIT clean without Civil 3D assemblies.");
return 0;
