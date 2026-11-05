using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>
/// Lightweight plugin-side logger that writes timestamped entries to a rotating
/// log file under <c>%LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log</c> and
  /// mirrors entries to the debugger without calling host APIs from worker threads.
///
/// The logger is deliberately dependency-free: a single background writer
/// serializes file writes without blocking Civil 3D. All methods are safe to
/// call before <c>PluginEntry.Initialize</c> runs.
/// </summary>
public static class PluginLog
{
  public enum Level
  {
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
  }

  private const long MaxLogFileBytes = 5L * 1024 * 1024; // 5 MiB per log file
  private const int RotatedBackupsKept = 3;

  private static readonly object FileSync = new();
  private static readonly Lazy<string> LogFilePathLazy = new(BuildLogFilePath);
  private static Level _minimumLevel = ReadLevelFromEnvironment();
  private static string? _lastFileError;

  public static string LogFilePath => LogFilePathLazy.Value;

  public static string? LastFileError => Volatile.Read(ref _lastFileError);

  public static bool IsFileLoggingHealthy => LastFileError == null && File.Exists(LogFilePath);

  public static Level MinimumLevel
  {
    get => _minimumLevel;
    set => _minimumLevel = value;
  }

  public static void Debug(string component, string message, Exception? exception = null)
    => Write(Level.Debug, component, message, exception);

  public static void Info(string component, string message, Exception? exception = null)
    => Write(Level.Info, component, message, exception);

  public static void Warn(string component, string message, Exception? exception = null)
    => Write(Level.Warn, component, message, exception);

  public static void Error(string component, string message, Exception? exception = null)
    => Write(Level.Error, component, message, exception);

  /// <summary>
  /// Convenience for <c>try { ... } catch { /* log and swallow */ }</c> blocks.
  /// Keeps the "best effort" reflection pattern used throughout the plugin
  /// while making failures visible at debug level.
  /// </summary>
  public static void Swallow(string component, string operation, Exception exception)
  {
    Write(Level.Debug, component, $"Ignored error during {operation}: {exception.Message}", exception);
  }

  private static void Write(Level level, string component, string message, Exception? exception)
  {
    if (level < _minimumLevel)
    {
      return;
    }

    var timestamp = DateTimeOffset.UtcNow.ToString("O");
    var line = new StringBuilder()
      .Append('[').Append(timestamp).Append("] ")
      .Append('[').Append(level).Append("] ")
      .Append('[').Append(component).Append("] ")
      .Append(message);

    if (exception != null)
    {
      line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
    }

    var text = line.ToString();

    TryAppendToFile(text, exception);

  }

  private static void TryAppendToFile(string text, Exception? exception)
  {
    try
    {
      var path = LogFilePathLazy.Value;

      lock (FileSync)
      {
        RotateIfNeeded(path);

        using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
        writer.WriteLine(text);

        if (exception?.StackTrace != null)
        {
          writer.WriteLine(exception.StackTrace);
        }

        Volatile.Write(ref _lastFileError, null);
      }
    }
    catch (Exception fileException)
    {
      // Logging itself must never throw into plugin code paths.
      Volatile.Write(
        ref _lastFileError,
        $"{fileException.GetType().Name}: {fileException.Message}");
      System.Diagnostics.Debug.WriteLine(
        $"Civil3D MCP file logging failed: {LastFileError}");
    }
  }

  private static void RotateIfNeeded(string path)
  {
    try
    {
      var info = new FileInfo(path);
      if (!info.Exists || info.Length < MaxLogFileBytes)
      {
        return;
      }

      for (var index = RotatedBackupsKept; index >= 1; index--)
      {
        var older = $"{path}.{index}";
        var newer = index == 1 ? path : $"{path}.{index - 1}";

        if (File.Exists(older))
        {
          File.Delete(older);
        }

        if (File.Exists(newer))
        {
          File.Move(newer, older);
        }
      }
    }
    catch
    {
      // Best-effort rotation; truncating failures are not worth escalating.
    }
  }

  private static string BuildLogFilePath()
  {
    var root = Environment.GetEnvironmentVariable("CIVIL3D_MCP_LOG_DIR")
      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Civil3DMcpPlugin");

    try
    {
      Directory.CreateDirectory(root);
    }
    catch
    {
      // Fall back to temp if LocalAppData is not writable.
      root = Path.Combine(Path.GetTempPath(), "Civil3DMcpPlugin");
      Directory.CreateDirectory(root);
    }

    return Path.Combine(root, "plugin.log");
  }

  private static Level ReadLevelFromEnvironment()
  {
    var raw = Environment.GetEnvironmentVariable("CIVIL3D_MCP_LOG_LEVEL");
    if (string.IsNullOrWhiteSpace(raw))
    {
      return Level.Info;
    }

    return raw.Trim().ToLowerInvariant() switch
    {
      "debug" => Level.Debug,
      "info" => Level.Info,
      "warn" or "warning" => Level.Warn,
      "error" => Level.Error,
      _ => Level.Info,
    };
  }
}
