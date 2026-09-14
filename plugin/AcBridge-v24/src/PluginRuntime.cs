using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public sealed record PluginStatus(
  bool IsRunning,
  bool OperationInProgress,
  string? CurrentOperation,
  int QueueDepth,
  int QueueCapacity,
  long? CurrentOperationStartedAtUnixMs,
  string? CurrentRequestId);

public sealed class JsonRpcDispatchException : Exception
{
  public JsonRpcDispatchException(string code, string message) : base(message)
  {
    Code = code;
  }

  public string Code { get; }
}

public static class PluginRuntime
{
  public const int DefaultPort = 8080;

  /// <summary>
  /// 实际绑定端口（2026-08-13 P0 端口可配置化）：
  /// 优先级 = 环境变量 CIVIL3D_BRIDGE_PORT → 项目根 config.json bridgePort → 默认 8080。
  /// relay 已从 config.json 读 bridgePort，插件读同一文件 → 用户改一处全对齐。
  /// </summary>
  public static int Port { get; } = ResolvePort();

  private static int ResolvePort()
  {
    var env = Environment.GetEnvironmentVariable("CIVIL3D_BRIDGE_PORT");
    if (int.TryParse(env, out var envPort) && envPort is > 0 and < 65536) return envPort;
    try
    {
      var asmDir = Path.GetDirectoryName(typeof(PluginRuntime).Assembly.Location);
      if (!string.IsNullOrEmpty(asmDir))
      {
        var cfgPath = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "config.json"));
        if (File.Exists(cfgPath))
        {
          var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath));
          if (json is System.Text.Json.Nodes.JsonObject jo)
          {
            var raw = JsonNodeToString(jo["bridgePort"]);
            if (int.TryParse(raw, out var cfgPort) && cfgPort is > 0 and < 65536) return cfgPort;
          }
        }
      }
    }
    catch { }
    return DefaultPort;
  }

  public const int DefaultRelayPort = 19876;

  /// <summary>旧版安装残留提示（空串表示无）。2026-09-14 商店版冲突防护。</summary>
  public static string LegacyWarning { get; set; } = "";

  /// <summary>
  /// relay 端口（2026-09-14 上架改造 D1）：与 install.ps1 / 面板读同一份 config.json。
  /// 优先级 = 环境变量 CIVIL3D_RELAY_PORT → config.json relayPort → 默认 19876。
  /// </summary>
  public static int RelayPort { get; } = ResolveRelayPort();

  private static int ResolveRelayPort()
  {
    var env = Environment.GetEnvironmentVariable("CIVIL3D_RELAY_PORT");
    if (int.TryParse(env, out var envPort) && envPort is > 0 and < 65536) return envPort;
    try
    {
      var asmDir = Path.GetDirectoryName(typeof(PluginRuntime).Assembly.Location);
      if (!string.IsNullOrEmpty(asmDir))
      {
        var cfgPath = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "config.json"));
        if (File.Exists(cfgPath))
        {
          var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath));
          if (json is System.Text.Json.Nodes.JsonObject jo)
          {
            var raw = JsonNodeToString(jo["relayPort"]);
            if (int.TryParse(raw, out var cfgPort) && cfgPort is > 0 and < 65536) return cfgPort;
          }
        }
      }
    }
    catch { }
    return DefaultRelayPort;
  }

  /// <summary>
  /// Root directory (holds node.exe / server / config.json). Two layouts must work:
  ///   1) Autoloader bundle: DLL at &lt;bundle&gt;\Contents\Windows\ -&gt; root = Contents (one level up)
  ///   2) dev / zip layout: DLL at &lt;root&gt;\plugin\AcBridge-v24\ -&gt; root = two levels up
  /// Probe for server\panel-relay.js / node.exe instead of guessing.
  /// 2026-09-14: bundle test showed the root was one level off, so MCP/relay reported "script not found".
  /// </summary>
  public static string ProjectRoot()
  {
    try
    {
      var asmDir = Path.GetDirectoryName(typeof(PluginRuntime).Assembly.Location);
      if (string.IsNullOrEmpty(asmDir)) return "";
      var candidates = new[]
      {
        Path.GetFullPath(Path.Combine(asmDir, "..")),
        Path.GetFullPath(Path.Combine(asmDir, "..", "..")),
      };
      foreach (var cand in candidates)
      {
        if (File.Exists(Path.Combine(cand, "server", "panel-relay.js"))) return cand;
      }
      foreach (var cand in candidates)
      {
        if (File.Exists(Path.Combine(cand, "node.exe"))) return cand;
      }
      foreach (var cand in candidates)
      {
        if (Directory.Exists(Path.Combine(cand, "server"))) return cand;
      }
      return candidates[1];
    }
    catch { return ""; }
  }

  /// <summary>
  /// 旧版安装残留检测（2026-09-14，商店版冲突防护）。
  /// 旧 GitHub / 解压版 install.ps1 会往 CAD 支持路径写 LISP 自载（Hank.lsp / acad.lsp），
  /// 开机 NETLOAD 另一个副本的 DLL —— 会与本版抢端口。这里扫支持路径，若钩子指向的不是
  /// 当前加载的程序集，就返回提示文本（无残留则空串）。永不抛出。
  /// </summary>
  public static string DetectLegacyInstall()
  {
    try
    {
      var asm = typeof(PluginRuntime).Assembly.Location ?? "";
      if (asm.Length == 0) return "";
      var asmSlash = asm.Replace('\\', '/');
      var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      var autodesk = Path.Combine(appData, "Autodesk");
      if (!Directory.Exists(autodesk)) return "";
      foreach (var product in Directory.GetDirectories(autodesk))
      {
        var candidates = new System.Collections.Generic.List<string>();
        candidates.Add(Path.Combine(product, "Support"));
        try { foreach (var lang in Directory.GetDirectories(product)) candidates.Add(Path.Combine(lang, "Support")); } catch { }
        foreach (var sup in candidates)
        {
          if (!Directory.Exists(sup)) continue;
          foreach (var lsp in new[] { "Hank.lsp", "acad.lsp", "acaddoc.lsp" })
          {
            var p = Path.Combine(sup, lsp);
            if (!File.Exists(p)) continue;
            string text;
            try { text = File.ReadAllText(p); } catch { continue; }
            var looksOurs = text.IndexOf("Civil3DMcpPlugin", StringComparison.OrdinalIgnoreCase) >= 0
                         || text.IndexOf("Hank.lsp", StringComparison.OrdinalIgnoreCase) >= 0
                         || text.IndexOf("NETLOAD", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!looksOurs) continue;
            var pointsToUs = text.IndexOf(asm, StringComparison.OrdinalIgnoreCase) >= 0
                          || text.IndexOf(asmSlash, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!pointsToUs)
            {
              return "Detected a legacy new-acad install: " + p + " auto-loads another copy of the plugin at CAD startup, which can conflict with this one. Please uninstall the old version (remove Hank.lsp/acad.lsp from the CAD support path or delete the old install folder) and restart CAD.";
            }
          }
        }
      }
      return "";
    }
    catch (System.Exception ex) { return ""; }
  }

  /// <summary>TCP 试连（探测 relay / MCP 是否在监听）。永不抛出。</summary>
  public static bool IsPortListening(int port, int timeoutMs = 400)
  {
    try
    {
      using var probe = new System.Net.Sockets.TcpClient();
      var task = probe.ConnectAsync("127.0.0.1", port);
      return task.Wait(timeoutMs) && probe.Connected;
    }
    catch { return false; }
  }

  /// <summary>
  /// relay 懒启动 / 自愈（2026-09-14 上架改造 D1）。
  /// 背景：此前 relay 靠 install.ps1 注册的计划任务开机自启；商店版不能依赖计划任务，
  /// 改为插件加载时自己拉起守护 launcher（server/relay-launcher.js，自带崩溃重启）。
  /// 防重复：① 端口已在监听 → 跳过；② launcher 锁文件在 30s 内被写过 → 说明别人正在启动 → 跳过。
  /// 永不抛出（失败只记日志），不阻塞 C3D 启动。
  /// </summary>
  public static bool EnsureRelayRunning()
  {
    try
    {
      if (IsPortListening(RelayPort))
      {
        PluginLog.Debug("Runtime", $"relay already listening on :{RelayPort}, skip start");
        return true;
      }
      var root = ProjectRoot();
      if (string.IsNullOrEmpty(root)) return false;
      var lockFile = Path.Combine(root, "server", "relay-launcher.lock");
      try
      {
        if (File.Exists(lockFile))
        {
          // 判据优先看「锁里的 PID 是否还活着」（launcher 自写自检，最准）；
          // 其次看锁是否刚被写过（覆盖 launcher 刚启动、PID 尚未来得及被我们看到的窗口）。
          // 注：锁文件只在 launcher 启动时写一次，所以「锁很旧」并不代表 launcher 已死。
          var alive = false;
          if (int.TryParse(File.ReadAllText(lockFile).Trim(), out var lockPid) && lockPid > 0)
          {
            try { using var _probe = System.Diagnostics.Process.GetProcessById(lockPid); alive = true; }
            catch { alive = false; }
          }
          var fresh = (DateTime.UtcNow - File.GetLastWriteTimeUtc(lockFile)).TotalSeconds < 30;
          if (alive || fresh)
          {
            PluginLog.Debug("Runtime", $"relay launcher already present (pid={lockPid} alive={alive} fresh={fresh}), skip start");
            return false;
          }
        }
      }
      catch { }
      var launcher = Path.Combine(root, "server", "relay-launcher.js");
      if (!File.Exists(launcher))
      {
        PluginLog.Debug("Runtime", $"relay launcher not found: {launcher}");
        return false;
      }
      var nodeCmd = Path.Combine(root, "node.exe");
      if (!File.Exists(nodeCmd)) nodeCmd = "node";
      var psi = new System.Diagnostics.ProcessStartInfo
      {
        FileName = nodeCmd,
        Arguments = "\"" + launcher + "\"",
        WorkingDirectory = Path.GetDirectoryName(launcher) ?? root,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      System.Diagnostics.Process.Start(psi);
      PluginLog.Info("Runtime", $"relay launcher started: {nodeCmd} {launcher}");
      return true;
    }
    catch (System.Exception ex)
    {
      PluginLog.Debug("Runtime", "relay start failed: " + ex.Message);
      return false;
    }
  }

  private static readonly object Sync = new();
  private static RpcTcpServer? _server;
  private static readonly AsyncLocal<string?> CurrentRequestOperation = new();
  private static readonly AsyncLocal<string?> CurrentRequestId = new();
  private static readonly AsyncLocal<CancellationToken> CurrentRequestCancellation = new();
  private static readonly AsyncLocal<string?> CurrentExpectedDrawingIdentity = new();
  private static readonly AsyncLocal<bool> CurrentRequestIsJob = new();   // 2026-08-19 方案3: 当前请求是否后台 job（长超时通道）
  private const int MaxQueuedHostOperations = 64;
  private static int _queueDepth;
  private static int _activeOperations;
  private static string? _currentOperation;
  private static string? _currentRequestId;
  private static long? _currentOperationStartedAtUnixMs;

  public static void StartServer()
  {
    lock (Sync)
    {
      if (_server != null)
      {
        return;
      }

      _server = new RpcTcpServer(Port, HandleRawRequestAsync);
      _server.Start();
    }
  }

  public static void StopServer()
  {
    lock (Sync)
    {
      _server?.Stop();
      _server = null;
      _currentOperation = null;
      _activeOperations = 0;
      _queueDepth = 0;
      _currentRequestId = null;
      _currentOperationStartedAtUnixMs = null;
    }
  }

  public static PluginStatus GetStatus()
  {
    lock (Sync)
    {
      return new PluginStatus(
        _server != null,
        _activeOperations > 0,
        _currentOperation,
        _queueDepth,
        MaxQueuedHostOperations,
        _currentOperationStartedAtUnixMs,
        _currentRequestId);
    }
  }

  public static async Task<string> HandleRawRequestAsync(string rawRequest, CancellationToken cancellationToken)
  {
    JsonNode? parsed;
    try
    {
      parsed = JsonNode.Parse(rawRequest);
    }
    catch (Exception ex)
    {
      return JsonRpcProtocol.SerializeError(null, -32700, "CIVIL3D.INVALID_JSON", $"Invalid JSON request: {ex.Message}");
    }

    if (parsed is not JsonObject request)
    {
      return JsonRpcProtocol.SerializeError(null, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must be an object.");
    }

    var id = request["id"]?.DeepClone();
    if (request["jsonrpc"] is not JsonValue versionValue
      || !versionValue.TryGetValue<string>(out var version)
      || version != "2.0")
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must specify jsonrpc='2.0'.");
    }

    var method = request["method"] is JsonValue methodValue
      && methodValue.TryGetValue<string>(out var methodText)
      ? methodText
      : null;

    if (string.IsNullOrWhiteSpace(method))
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request is missing a string method.");
    }

    if (request["params"] != null && request["params"] is not JsonObject)
    {
      return JsonRpcProtocol.SerializeError(id, -32602, "CIVIL3D.INVALID_INPUT", "JSON-RPC params must be an object when provided.");
    }
    var parameters = request["params"] as JsonObject;

    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    CurrentRequestOperation.Value = method;
    CurrentRequestId.Value = id?.ToJsonString();
    CurrentRequestCancellation.Value = cancellationToken;

    var timer = System.Diagnostics.Stopwatch.StartNew();
    try
    {
      PluginLog.Debug("Dispatch", $"-> {method} [{CurrentRequestId.Value ?? "no-id"}]");
      var result = await CommandDispatcher.DispatchAsync(method, parameters, cancellationToken);
      PluginLog.Debug("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] ok durationMs={timer.ElapsedMilliseconds}");
      return JsonRpcProtocol.SerializeResult(id, result);
    }
    catch (JsonRpcDispatchException ex)
    {
      // Domain-level errors are part of the contract; record at info so they
      // show up in diagnostics without looking like runtime faults.
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] dispatch error {ex.Code} durationMs={timer.ElapsedMilliseconds}: {ex.Message}");
      return JsonRpcProtocol.SerializeError(id, JsonRpcProtocol.NumericErrorCode(ex.Code), ex.Code, ex.Message);
    }
    catch (OperationCanceledException)
    {
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] cancelled durationMs={timer.ElapsedMilliseconds}");
      return JsonRpcProtocol.SerializeError(id, -32010, "CIVIL3D.CANCELLED", $"Operation '{method}' was cancelled.");
    }
    catch (Exception ex)
    {
      PluginLog.Error("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] unhandled failure durationMs={timer.ElapsedMilliseconds} category={ex.GetType().Name}", ex);
      return JsonRpcProtocol.SerializeError(id, -32603, "CIVIL3D.INTERNAL_ERROR", "The Civil 3D plugin encountered an unexpected error.");
    }
    finally
    {
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
    }
  }

  internal static CancellationToken GetCurrentRequestCancellationToken() => CurrentRequestCancellation.Value;

  internal static string GetCurrentRequestOperation() => CurrentRequestOperation.Value ?? "Civil 3D operation";

  internal static string? GetCurrentRequestId() => CurrentRequestId.Value;

  internal static string? GetActiveDrawingIdentity()
  {
    var document = App.DocumentManager.MdiActiveDocument;
    return GetDrawingIdentity(document);
  }

  internal static string? GetDrawingIdentity(Autodesk.AutoCAD.ApplicationServices.Document? document)
  {
    if (document == null) return null;
    var fileName = document.Database.Filename;
    return string.IsNullOrWhiteSpace(fileName) ? document.Name : fileName;
  }

  internal static string? GetExpectedDrawingIdentity() => CurrentExpectedDrawingIdentity.Value;

  internal static bool GetCurrentRequestIsJob() => CurrentRequestIsJob.Value;   // 2026-08-19 方案3

  internal static async Task<T> RunWithRequestContextAsync<T>(
    string operation,
    string requestId,
    CancellationToken cancellationToken,
    string? expectedDrawingIdentity,
    Func<Task<T>> action,
    bool isJob = false)   // 2026-08-19 方案3: job 上下文标记（长超时）
  {
    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    var previousExpectedDrawingIdentity = CurrentExpectedDrawingIdentity.Value;
    var previousIsJob = CurrentRequestIsJob.Value;
    CurrentRequestOperation.Value = operation;
    CurrentRequestId.Value = requestId;
    CurrentRequestCancellation.Value = cancellationToken;
    CurrentExpectedDrawingIdentity.Value = expectedDrawingIdentity;
    CurrentRequestIsJob.Value = isJob;
    try
    {
      return await action();
    }
    finally
    {
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
      CurrentRequestIsJob.Value = previousIsJob;
      CurrentExpectedDrawingIdentity.Value = previousExpectedDrawingIdentity;
    }
  }

  internal static void QueueHostOperation()
  {
    lock (Sync)
    {
      if (_queueDepth >= MaxQueuedHostOperations)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          $"Civil 3D host queue is full ({MaxQueuedHostOperations} operations). Retry after current work completes.");
      }

      _queueDepth++;
    }
  }

  internal static void StartHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
      _activeOperations++;
      _currentOperation = GetCurrentRequestOperation();
      _currentRequestId = GetCurrentRequestId();
      _currentOperationStartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
  }

  internal static void CancelQueuedHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
    }
  }

  internal static void CompleteHostOperation()
  {
    lock (Sync)
    {
      _activeOperations = Math.Max(0, _activeOperations - 1);
      if (_activeOperations == 0)
      {
        _currentOperation = null;
        _currentRequestId = null;
        _currentOperationStartedAtUnixMs = null;
      }
    }
  }

  public static object? GetParameter(JsonObject? parameters, string name)
  {
    if (parameters == null)
    {
      return null;
    }

    return parameters.TryGetPropertyValue(name, out var value) ? value : null;
  }

  public static string GetRequiredString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    var stringValue = JsonNodeToString(value);
    if (stringValue == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"参数 '{name}' 期望类型 string 实际值 {value.ToJsonString()}");
    }

    if (string.IsNullOrWhiteSpace(stringValue))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be a non-empty string.");
    }

    return stringValue;
  }

  public static double GetRequiredDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return CoerceDouble(value, name) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"参数 '{name}' 期望类型 number 实际值 {value.ToJsonString()}");
  }

  public static int GetRequiredInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return CoerceInt(value, name) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be an integer.");
  }

  public static string? GetOptionalString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null) return null;
    try { return value.GetValue<string?>(); }
    catch (InvalidOperationException)
    {
      // 容错：数字/布尔转字符串（AI 传参类型不严格时避免崩溃）
      if (value is JsonValue jv)
      {
        if (jv.TryGetValue<double>(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (jv.TryGetValue<bool>(out var b)) return b ? "1" : "0";
      }
      throw;
    }
  }

  public static double? GetOptionalDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : CoerceDouble(value, name);
  }

  public static int? GetOptionalInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null) return null;
    try { return value.GetValue<int>(); }
    catch (InvalidOperationException)
    {
      return CoerceInt(value, name);
    }
  }

  /// <summary>
  /// 类型容错：bool→1/0，字符串数字→int，double→int。避免 AI 传参类型不严格导致崩溃。
  /// </summary>
  private static int? CoerceInt(JsonNode value, string name)
  {
    try
    {
      if (value is JsonValue jv)
      {
        if (jv.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var n)) return n;
        if (jv.TryGetValue<double>(out var d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
      }
    }
    catch { }
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"参数 '{name}' 期望类型 integer 实际值 {value.ToJsonString()}");
  }

  /// <summary>
  /// 类型容错：bool→1/0，字符串数字→double，int→double。与 CoerceInt 同级。
  /// </summary>
  private static double? CoerceDouble(JsonNode value, string name)
  {
    try
    {
      if (value is JsonValue jv)
      {
        if (jv.TryGetValue<bool>(out var b)) return b ? 1.0 : 0.0;
        if (jv.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        if (jv.TryGetValue<double>(out var dd)) return dd;
      }
    }
    catch { }
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"参数 '{name}' 期望类型 number 实际值 {value.ToJsonString()}");
  }

  public static bool? GetOptionalBool(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null) return null;
    try { return value.GetValue<bool>(); }
    catch (InvalidOperationException)
    {
      // 容错：数字/字符串→bool（AI 传参类型不严格时避免崩溃）
      if (value is JsonValue jv)
      {
        if (jv.TryGetValue<double>(out var d)) return d != 0;
        if (jv.TryGetValue<string>(out var s))
        {
          if (bool.TryParse(s, out var b)) return b;
          if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
          if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        }
      }
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"参数 '{name}' 期望类型 boolean 实际值 {value.ToJsonString()}");
    }
  }

  /// <summary>
  /// 嵌套节点安全解析：缺 key / 类型错 → CIVIL3D.INVALID_INPUT（带参数名），不抛裸 InvalidOperationException。
  /// 用于 point["x"] 等数组元素/子对象字段解析。
  /// </summary>
  public static double GetRequiredDoubleFromNode(JsonNode? value, string name)
  {
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"参数 '{name}' 期望类型 number 实际值 null");
    }
    return CoerceDouble(value, name) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"参数 '{name}' 期望类型 number 实际值 {value.ToJsonString()}");
  }

  /// <summary>
  /// 嵌套节点安全解析：缺 key / 类型错 → CIVIL3D.INVALID_INPUT（带参数名），不抛裸 InvalidOperationException。
  /// </summary>
  public static int GetRequiredIntFromNode(JsonNode? value, string name)
  {
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"参数 '{name}' 期望类型 integer 实际值 null");
    }
    return CoerceInt(value, name) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"参数 '{name}' 期望类型 integer 实际值 {value.ToJsonString()}");
  }

  /// <summary>
  /// 数组参数容错解析（2026-08-13）：接受 JsonArray / 逗号分隔字符串 / 单值字符串，返回去空 trim 后的数组。
  /// AI 传参类型不严格（数组 vs 字符串）时避免崩溃。参数缺失或解析后为空返回 null。
  /// </summary>
  public static string[]? GetOptionalStringArray(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null) return null;
    var items = new List<string>();
    if (value is JsonArray arr)
    {
      foreach (var it in arr)
      {
        var s = JsonNodeToString(it);
        if (!string.IsNullOrWhiteSpace(s)) items.Add(s!.Trim());
      }
    }
    else
    {
      var s = JsonNodeToString(value);
      if (!string.IsNullOrWhiteSpace(s))
      {
        foreach (var part in s.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
          if (!string.IsNullOrWhiteSpace(part)) items.Add(part);
        }
      }
    }
    return items.Count > 0 ? items.ToArray() : null;
  }

  /// <summary>
  /// 任意 JsonNode → string 容错（数字/布尔转字符串），用于数组元素等非命名参数。
  /// </summary>
  public static string? JsonNodeToString(JsonNode? n)
  {
    if (n == null) return null;
    try { return n.GetValue<string>(); }
    catch (InvalidOperationException)
    {
      if (n is JsonValue jv)
      {
        if (jv.TryGetValue<double>(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (jv.TryGetValue<bool>(out var b)) return b ? "1" : "0";
      }
      return null;
    }
  }

}
