using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Civil3DMcpPlugin.PluginEntry))]
[assembly: CommandClass(typeof(Civil3DMcpPlugin.PluginEntry))]

namespace Civil3DMcpPlugin;

public sealed class PluginEntry : IExtensionApplication
{
  public void Initialize()
  {
    try
    {
      PluginRuntime.StartServer();

      // 选择监听：跟随文档生命周期，避免 C3D 启动早期文档未就绪导致绑定失败
      App.DocumentManager.DocumentCreated += OnDocumentCreated;
      try
      {
        var doc = App.DocumentManager.MdiActiveDocument;
        if (doc != null) doc.ImpliedSelectionChanged += DrawingCommands.OnSelectionChanged;
      }
      catch { }

      // MCP 懒启动（方案 B）：探测 :3000，没监听才启动。
      // 不在开机自启，随 C3D 首次加载拉起，避免不用 C3D 时白占 ~108MB。
      EnsureMcpRunning();

      // 2026-09-14 上架改造 D1：relay 也由插件自己拉起——商店版不再依赖 install.ps1 注册的计划任务。
      // 端口监听 + launcher 锁文件双重防重复；失败不阻塞 C3D 启动。
      PluginRuntime.EnsureRelayRunning();

      // 2026-09-14：旧版（GitHub/解压版）安装残留检测 → 命令行 + 日志提示（避免双副本抢端口）
      try
      {
        var legacy = PluginRuntime.DetectLegacyInstall();
        if (!string.IsNullOrEmpty(legacy))
        {
          PluginRuntime.LegacyWarning = legacy;
          PluginLog.Warn("PluginEntry", legacy);
          WriteMessage(legacy);
        }
      }
      catch { }

      PluginLog.Info("PluginEntry", $"Civil3D MCP plugin initialized on port {PluginRuntime.Port}. Log file: {PluginLog.LogFilePath}");
      WriteMessage("Civil3D MCP plugin initialized.");

      // C3D 加载完毕后自动显示聊天面板
      App.Idle += OnIdleShowPalette;
    }
    catch (System.Exception ex)
    {
      PluginLog.Error("PluginEntry", "Plugin failed to initialize", ex);
      WriteMessage($"Civil3D MCP plugin failed to initialize: {ex.Message}");
    }
  }

  /// <summary>
  /// MCP 懒启动：探测 127.0.0.1:3000，未监听则启动 sacred-mcp.js（独立进程，不阻塞 C3D 启动）。
  /// 失败不影响插件初始化（MCP 可稍后手动 start-mcp.bat）。
  /// </summary>
  private static void EnsureMcpRunning()
  {
    try
    {
      using (var probe = new System.Net.Sockets.TcpClient())
      {
        var task = probe.ConnectAsync("127.0.0.1", 3000);
        if (task.Wait(500) && probe.Connected)
        {
          PluginLog.Debug("PluginEntry", "MCP already running on :3000, skip lazy start.");
          return;
        }
      }
    }
    catch { /* not listening → start below */ }

    try
    {
      // 2026-09-14: 统一走 PluginRuntime.ProjectRoot()（探测式，兼容 bundle 与 dev/zip 两种布局）
      var root = PluginRuntime.ProjectRoot();
      var mcpScript = System.IO.Path.Combine(root, "server", "sacred-mcp.js");
      if (!System.IO.File.Exists(mcpScript))
      {
        PluginLog.Debug("PluginEntry", $"MCP script not found: {mcpScript}");
        return;
      }
      var nodeCmd = System.IO.Path.Combine(root, "node.exe");
      if (!System.IO.File.Exists(nodeCmd)) nodeCmd = "node";
      var psi = new System.Diagnostics.ProcessStartInfo
      {
        FileName = nodeCmd,
        Arguments = "\"" + mcpScript + "\"",
        WorkingDirectory = System.IO.Path.GetDirectoryName(mcpScript) ?? root,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      System.Diagnostics.Process.Start(psi);
      PluginLog.Info("PluginEntry", $"MCP lazy-started: {nodeCmd} {mcpScript}");
    }
    catch (System.Exception ex)
    {
      PluginLog.Debug("PluginEntry", "MCP lazy start failed: " + ex.Message);
    }
  }

  private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
  {
    try
    {
      var doc = e.Document;
      if (doc == null) return;
      doc.ImpliedSelectionChanged -= DrawingCommands.OnSelectionChanged; // 防重复
      doc.ImpliedSelectionChanged += DrawingCommands.OnSelectionChanged;
      PluginLog.Debug("PluginEntry", "Selection listener attached to document: " + doc.Name);
    }
    catch (System.Exception ex)
    {
      PluginLog.Debug("PluginEntry", "Failed to attach selection listener: " + ex.Message);
    }
  }

  public void Terminate()
  {
    try
    {
      try { App.Idle -= OnIdleShowPalette; } catch { }
      try { App.DocumentManager.DocumentCreated -= OnDocumentCreated; } catch { }
      try { App.DocumentManager.MdiActiveDocument.ImpliedSelectionChanged -= DrawingCommands.OnSelectionChanged; } catch { }
      // 2026-08-12 M4: 卸载时停面板 Timer/SSE, 防后台线程继续跑
      try { HankPalette.StopForUnload(); } catch { }

      PluginLog.Info("PluginEntry", "Civil3D MCP plugin terminated cleanly.");
    }
    catch (System.Exception ex)
    {
      PluginLog.Error("PluginEntry", "Error during plugin termination", ex);
    }
  }

  [CommandMethod("C3DMCPSTART")]
  public void StartCommand()
  {
    PluginRuntime.StartServer();
    WriteMessage($"Civil3D MCP listener started on port {PluginRuntime.Port}.");
  }

  [CommandMethod("C3DMCPSTOP")]
  public void StopCommand()
  {
    PluginRuntime.StopServer();
    WriteMessage("Civil3D MCP listener stopped.");
  }

  [CommandMethod("C3DMCPSTATUS")]
  public void StatusCommand()
  {
    var status = PluginRuntime.GetStatus();
    WriteMessage($"Civil3D MCP listener running: {status.IsRunning}; pending: {status.QueueDepth}; active: {status.OperationInProgress}; current: {status.CurrentOperation ?? "<none>"}");
  }

  private static void OnIdleShowPalette(object? sender, EventArgs e)
  {
    App.Idle -= OnIdleShowPalette;
    try { HankPalette.Show(); } catch { }
  }

  private static void WriteMessage(string message)
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    doc?.Editor.WriteMessage($"\n{message}");
  }
}
