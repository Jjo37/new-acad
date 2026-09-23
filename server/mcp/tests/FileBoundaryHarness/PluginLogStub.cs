namespace Civil3DMcpPlugin;

public static class PluginLog
{
  public static void Debug(string component, string message) { }
  public static void Warn(string component, string message) { }
  public static void Error(string component, string message, Exception? exception = null) { }
}
