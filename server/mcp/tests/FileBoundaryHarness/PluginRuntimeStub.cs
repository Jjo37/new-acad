namespace Civil3DMcpPlugin;

// 2026-10-20 审计 F7：本套件曾因引用了不存在的旧目录而无法编译；修好路径后又暴露出
// 「桩落后于真实源码」——PluginLog 新增了 Info，FileBoundary 新增了对 PluginRuntime.ProjectRoot() 的依赖。
// 这里补齐最小桩，目的仅让边界逻辑可在无 CAD 环境跑起来（不做安装目录探测）。
public static class PluginRuntime
{
  // 返回空串 = 探测不到项目根 -> FileBoundary.LocateProjectRoot() 返回 null -> 回落到环境变量配置的根。
  // 这正是本套件期望的行为（测试用 CIVIL3D_IMPORT_ROOTS / CIVIL3D_EXPORT_ROOTS 指定根）。
  public static string ProjectRoot() => string.Empty;
}
