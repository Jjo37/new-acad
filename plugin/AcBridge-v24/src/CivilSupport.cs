namespace Civil3DMcpPlugin;

/// <summary>
/// Civil 3D availability gate (2026-10-27, v1.11.0-fix).
/// The plugin now also runs in plain AutoCAD 2025/2026, where AeccDbMgd (the Civil 3D managed
/// API) does not exist at all. Two rules follow:
///   1. AutoCAD-only code paths must keep Autodesk.Civil types out of their signatures —
///      a JIT-time type resolution failure cannot be caught by the method that declares it.
///   2. Civil 3D paths must go through RequireDocument(), the single place allowed to touch them.
/// </summary>
public static class CivilSupport
{
  private static readonly object Gate = new();
  private static bool? _available;
  private static string? _detail;

  /// <summary>True when AeccDbMgd resolves in this process, i.e. the host is Civil 3D.</summary>
  public static bool Available
  {
    get
    {
      Probe();
      return _available!.Value;
    }
  }

  /// <summary>Probe detail for diagnostics: assembly version, or the failure reason.</summary>
  public static string? Detail
  {
    get
    {
      Probe();
      return _detail;
    }
  }

  private static void Probe()
  {
    if (_available.HasValue) return;
    lock (Gate)
    {
      if (_available.HasValue) return;
      try
      {
        var loaded = Civil3DCompatibility.IsCivilAssemblyLoadable(out var detail);
        _detail = detail;
        _available = loaded;
        if (!loaded)
        {
          try { PluginLog.Warn("CivilSupport", "Civil 3D API not available in this host -> plain-AutoCAD mode. " + _detail); } catch { }
        }
      }
      catch (Exception ex)
      {
        _detail = ex.GetType().Name + ": " + ex.Message;
        _available = false;
      }
    }
  }

  public static string NotInstalledMessage => Loc.T(
    "这个方法是 Civil 3D 专有的（路线 / 曲面 / 廊道 / 管网等）。当前宿主是纯 AutoCAD，没有 Civil 3D 接口。要用它请安装 Civil 3D 2025/2026；绘图、图层、块、文件读写等普通能力不受影响。",
    "This method is Civil 3D-specific (alignments / surfaces / corridors / pipe networks). The current host is plain AutoCAD without the Civil 3D API. Install Civil 3D 2025/2026 to use it; drawing, layers, blocks and file I/O keep working.");

  /// <summary>Civil 3D only. Only reachable from handlers that plain AutoCAD cannot call.</summary>
  public static Autodesk.Civil.ApplicationServices.CivilDocument RequireDocument()
  {
    if (!Available) throw new JsonRpcDispatchException("CIVIL3D.CIVIL_NOT_INSTALLED", NotInstalledMessage);
    return Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument
      ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
  }

  /// <summary>
  /// True when the exception is the "Civil assembly missing" family raised by a JIT-time type
  /// resolution failure. Such failures surface at the call site, never inside the callee.
  /// </summary>
  public static bool IsMissingCivilAssembly(Exception? ex)
  {
    for (var e = ex; e != null; e = e.InnerException)
    {
      var name = e.GetType().Name;
      if (name != "FileNotFoundException" && name != "TypeLoadException" &&
          name != "FileLoadException" && name != "MissingMethodException" &&
          name != "TypeInitializationException") continue;
      var msg = e.Message ?? "";
      if (msg.Contains("AeccDbMgd") || msg.Contains("AeccPressurePipesMgd") ||
          msg.Contains("AecBaseMgd") || msg.Contains("Autodesk.Civil"))
        return true;
    }
    return false;
  }

  /// <summary>Host product name for diagnostics — plain AutoCAD reports e.g. "AutoCAD 2025".</summary>
  public static string HostProduct()
  {
    try { return Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("PRODUCT")?.ToString() ?? ""; }
    catch { return ""; }
  }
}
