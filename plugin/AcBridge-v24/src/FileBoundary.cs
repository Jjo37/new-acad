using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Authoritative filesystem boundary for caller-supplied import and export paths.
/// Paths are canonicalized, restricted to configured roots, and checked against
/// a command-specific extension allowlist before Civil 3D or System.IO sees them.
/// </summary>
internal static class FileBoundary
{
  private const string SharedRootsVariable = "CIVIL3D_FILE_ROOTS";
  private const string ImportRootsVariable = "CIVIL3D_IMPORT_ROOTS";
  private const string ExportRootsVariable = "CIVIL3D_EXPORT_ROOTS";
  private const uint FileFlagBackupSemantics = 0x02000000;
  private const uint FileFlagOpenReparsePoint = 0x00200000;
  private const int FileAttributeTagInfoClass = 9;

  private static readonly Lazy<string[]> ImportRoots = new(() => LoadRoots(ImportRootsVariable));
  private static readonly Lazy<string[]> ExportRoots = new(() => LoadRoots(ExportRootsVariable));

  // 2026-08-13: 敏感路径黑名单（L1 硬拒）——readTextFile/listDirectory 读通道用，命中即拒
  // 覆盖: 配置文件(含密钥)/环境变量/SSH私钥/证书/云凭据/通用敏感命名/版本库目录
  private static readonly System.Text.RegularExpressions.Regex SensitivePathRegex = new(
    @"(^|[\\/])(config\.json|appsettings\.json|web\.config|\.npmrc|\.piprc|secrets\.json|\.env(\.[a-z0-9_]+)?)([\\/]|$)" +
    @"|\.(pem|key|pfx|p12|p8|crt)$" +
    @"|(^|[\\/])id_(rsa|dsa|ecdsa|ed25519)([\\/]|$)" +
    @"|(^|[\\/])\.(git|svn|hg)([\\/]|$)" +
    @"|(^|[\\/])(\.aws|gcloud|azure|credentials|credentials\.json)([\\/]|$)" +
    @"|(^|[\\/])(\.kube)([\\/]|$)" +
    @"|(secret|token|password|passwd|shadow|api[_-]?key|apikey|auth)",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

  // 2026-08-13: 系统敏感区（L2 硬拒）——本机系统凭据/浏览器登录态
  private static readonly System.Text.RegularExpressions.Regex SystemSensitivePathRegex = new(
    @"(^|[\\/])system32[\\/]config([\\/]|$)|shadow$|^passwd$" +
    @"|(^|[\\/])(Cookies|Login Data|Local State)([\\/]|$)",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

  /// <summary>
  /// 2026-08-13: 敏感路径检查（读通道）——L1 凭据/密钥/版本库 + L2 系统敏感区，命中抛 PATH_NOT_ALLOWED。
  /// 项目根（DLL 上溯两级）本身不在此禁（插件运行必需），但 config.json 已由 L1 覆盖。
  /// </summary>
  public static void AssertReadablePath(string canonicalPath)
  {
    if (SensitivePathRegex.IsMatch(canonicalPath) || SystemSensitivePathRegex.IsMatch(canonicalPath))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.PATH_NOT_ALLOWED",
        $"Sensitive file is not readable: {canonicalPath}");
    }
  }

  public static string ResolveImportPath(string rawPath, params string[] allowedExtensions)
  {
    var path = ResolvePath(rawPath, ImportRoots.Value, "import", allowedExtensions);
    if (!File.Exists(path))
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Import file was not found: {path}");
    }

    return path;
  }

  public static string ResolveExportPath(
    string rawPath,
    bool overwrite,
    params string[] allowedExtensions)
  {
    var path = ResolvePath(rawPath, ExportRoots.Value, "export", allowedExtensions);
    if (File.Exists(path) && !overwrite)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"Output file already exists: {path}. Set overwrite=true to replace it explicitly.");
    }

    return path;
  }

  public static string WriteAllTextAtomic(
    string rawPath,
    string content,
    Encoding encoding,
    bool overwrite,
    params string[] allowedExtensions)
  {
    var path = ResolveExportPath(rawPath, overwrite, allowedExtensions);
    var directory = Path.GetDirectoryName(path)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Output path must include a directory.");
    using var directoryLock = LockExportDirectoryChain(directory);

    var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    try
    {
      File.WriteAllText(tempPath, content, encoding);
      File.Move(tempPath, path, overwrite);
      return path;
    }
    catch (IOException) when (!overwrite && File.Exists(path))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"Output file already exists: {path}. Set overwrite=true to replace it explicitly.");
    }
    catch (JsonRpcDispatchException)
    {
      throw;
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to write output file '{path}': {exception.Message}");
    }
    finally
    {
      try
      {
        if (File.Exists(tempPath))
        {
          File.Delete(tempPath);
        }
      }
      catch
      {
        // Preserve the original operation result. Stale temp files use a
        // hidden, collision-resistant name and can be removed later.
      }
    }
  }

  private static string ResolvePath(
    string rawPath,
    IReadOnlyCollection<string> roots,
    string operation,
    IReadOnlyCollection<string> allowedExtensions)
  {
    if (string.IsNullOrWhiteSpace(rawPath))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"A non-empty {operation} path is required.");
    }

    if (!Path.IsPathFullyQualified(rawPath))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"The {operation} path must be absolute: {rawPath}");
    }

    string canonicalPath;
    try
    {
      canonicalPath = Path.GetFullPath(rawPath);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Invalid {operation} path: {exception.Message}");
    }

    // 2026-08-12: 放开全盘读写——用户数据位置不可预知(桌面/D盘/NAS), roots 不再强制。
    // 安全护栏保留: 覆盖需显式 overwrite / 绝对路径 / 规范化 / reparse 防穿越 / 扩展名白名单。
    // roots 语义变为"额外放行的目录"(兼容已设 CIVIL3D_*_ROOTS 环境变量的用户)。
    var matchedRoot = roots.FirstOrDefault(root => IsWithinRoot(canonicalPath, root));
    if (matchedRoot != null)
    {
      // 命中配置根 → 从该根开始检查 reparse(原有行为)
      RejectReparsePointTraversal(canonicalPath, matchedRoot);
    }
    else
    {
      // 未命中 → 全盘放行, 从驱动器根开始全路径防穿越检查(更强)
      RejectReparsePointTraversal(canonicalPath, null);
    }

    var allowed = allowedExtensions
      .Select(NormalizeExtension)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var extension = Path.GetExtension(canonicalPath);
    if (allowed.Count > 0 && !allowed.Contains(extension))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_TYPE_NOT_ALLOWED",
        $"Extension '{extension}' is not allowed for this operation. Allowed extensions: {string.Join(", ", allowed.Order())}.");
    }

    return canonicalPath;
  }

  private static string[] LoadRoots(string operationVariable)
  {
    var configured = Environment.GetEnvironmentVariable(operationVariable);
    if (string.IsNullOrWhiteSpace(configured))
    {
      configured = Environment.GetEnvironmentVariable(SharedRootsVariable);
    }

    var roots = SplitRoots(configured).ToList();
    if (roots.Count == 0)
    {
      // 默认根：项目内 exchange\export（导出）/ exchange\import（导入），
      // 统一临时文件位置，方便用户清理（2026-08-05）。环境变量仍可覆盖。
      var projectRoot = LocateProjectRoot();
      if (projectRoot != null)
      {
        var sub = operationVariable == ExportRootsVariable ? "export" : "import";
        var dir = Path.Combine(projectRoot, "exchange", sub);
        try { Directory.CreateDirectory(dir); } catch { }
        roots.Add(dir);
        // 2026-08-12: 选择集快照目录 exchange\_out 也纳入导出根（_sel.json 走 FileBoundary 原子写）
        if (operationVariable == ExportRootsVariable)
        {
          var outDir = Path.Combine(projectRoot, "exchange", "_out");
          try { Directory.CreateDirectory(outDir); } catch { }
          roots.Add(outDir);
        }
      }
      else
      {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
          roots.Add(documents);
        }
      }
    }

    return roots
      .Select(Path.GetFullPath)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  /// <summary>
  /// 定位项目根目录：DLL 位于 &lt;root&gt;\plugin\AcBridge-v24\，上溯两级。
  /// </summary>
  private static string? LocateProjectRoot()
  {
    // 2026-09-15: 统一探测式根目录（bundle 下导出/导入根会落错一级 → _sel.json 写到 bundle\exchange 而非 Contents\exchange）
    try
    {
      var root = PluginRuntime.ProjectRoot();
      return string.IsNullOrEmpty(root) ? null : root;
    }
    catch { return null; }
  }

  private static IEnumerable<string> SplitRoots(string? configured)
  {
    if (string.IsNullOrWhiteSpace(configured))
    {
      yield break;
    }

    foreach (var root in configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      if (!Path.IsPathFullyQualified(root))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_CONFIGURATION",
          $"Configured filesystem root must be absolute: {root}");
      }

      yield return root;
    }
  }

  private static bool IsWithinRoot(string path, string root)
  {
    var relative = Path.GetRelativePath(root, path);
    return !Path.IsPathRooted(relative)
      && !string.Equals(relative, "..", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
  }

  private static void RejectReparsePointTraversal(string path, string? root)
  {
    // 2026-08-12: root 可空——null 表示全盘模式: 从驱动器根开始全路径检查(覆盖整条路径)
    if (root == null)
    {
      var driveRoot = Path.GetPathRoot(path);
      if (string.IsNullOrEmpty(driveRoot)) return;
      var fullRelative = Path.GetRelativePath(driveRoot, path);
      if (fullRelative == ".") return;
      var probe = driveRoot;
      foreach (var segment in fullRelative.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
        StringSplitOptions.RemoveEmptyEntries))
      {
        probe = Path.Combine(probe, segment);
        if (!Directory.Exists(probe) && !File.Exists(probe))
        {
          break;
        }
        try
        {
          if ((File.GetAttributes(probe) & FileAttributes.ReparsePoint) != 0)
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.PATH_NOT_ALLOWED",
              $"Filesystem links and junctions are not allowed in caller-supplied paths: {probe}");
          }
        }
        catch (JsonRpcDispatchException) { throw; }
        catch (Exception exception)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.FILE_IO_ERROR",
            $"Unable to validate filesystem path '{probe}': {exception.Message}");
        }
      }
      return;
    }

    var relative = Path.GetRelativePath(root, path);
    if (relative == ".")
    {
      return;
    }

    var current = root;
    foreach (var segment in relative.Split(
      [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
      StringSplitOptions.RemoveEmptyEntries))
    {
      current = Path.Combine(current, segment);
      if (!Directory.Exists(current) && !File.Exists(current))
      {
        break;
      }

      try
      {
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.PATH_NOT_ALLOWED",
            $"Filesystem links and junctions are not allowed in caller-supplied paths: {current}");
        }
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception exception)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.FILE_IO_ERROR",
          $"Unable to validate filesystem path '{current}': {exception.Message}");
      }
    }
  }

  private static string NormalizeExtension(string extension) =>
    extension.StartsWith('.') ? extension : $".{extension}";

  private static DirectoryChainLock LockExportDirectoryChain(string directory)
  {
    var canonicalDirectory = Path.GetFullPath(directory);
    // 2026-08-12 全盘放开: 命中配置根从根锁起(原有); 未命中(桌面/D盘)从目标目录自身锁起
    var matchedRoot = ExportRoots.Value
      .Where(root => IsWithinRoot(canonicalDirectory, root))
      .OrderByDescending(root => root.Length)
      .FirstOrDefault();

    var lockStart = matchedRoot ?? canonicalDirectory;
    if (!Directory.Exists(lockStart))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_CONFIGURATION",
        $"Configured export root does not exist: {lockStart}");
    }

    var handles = new List<SafeFileHandle>();
    try
    {
      var current = Path.GetFullPath(lockStart);
      handles.Add(OpenLockedDirectory(current));
      var relative = Path.GetRelativePath(current, canonicalDirectory);
      if (relative != ".")
      {
        foreach (var segment in relative.Split(
          [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
          StringSplitOptions.RemoveEmptyEntries))
        {
          var next = Path.Combine(current, segment);
          if (!Directory.Exists(next))
          {
            // The parent handle excludes FILE_SHARE_DELETE, so it cannot be
            // swapped for a junction between creation and the next handle open.
            Directory.CreateDirectory(next);
          }
          handles.Add(OpenLockedDirectory(next));
          current = next;
        }
      }
      return new DirectoryChainLock(handles);
    }
    catch
    {
      foreach (var handle in handles) handle.Dispose();
      throw;
    }
  }

  private static SafeFileHandle OpenLockedDirectory(string path)
  {
    var handle = CreateFileW(
      path,
      0,
      FileShare.ReadWrite,
      IntPtr.Zero,
      FileMode.Open,
      FileFlagBackupSemantics | FileFlagOpenReparsePoint,
      IntPtr.Zero);
    if (handle.IsInvalid)
    {
      var error = new Win32Exception(Marshal.GetLastWin32Error());
      handle.Dispose();
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to lock filesystem directory '{path}': {error.Message}");
    }

    if (!GetFileInformationByHandleEx(
      handle,
      FileAttributeTagInfoClass,
      out var information,
      (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
    {
      var error = new Win32Exception(Marshal.GetLastWin32Error());
      handle.Dispose();
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to inspect filesystem directory '{path}': {error.Message}");
    }

    if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
    {
      handle.Dispose();
      throw new JsonRpcDispatchException(
        "CIVIL3D.PATH_NOT_ALLOWED",
        $"Filesystem links and junctions are not allowed in caller-supplied paths: {path}");
    }
    return handle;
  }

  private sealed class DirectoryChainLock(List<SafeFileHandle> handles) : IDisposable
  {
    public void Dispose()
    {
      for (var index = handles.Count - 1; index >= 0; index--)
        handles[index].Dispose();
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct FileAttributeTagInfo
  {
    public FileAttributes FileAttributes;
    public uint ReparseTag;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(
    string fileName,
    uint desiredAccess,
    FileShare shareMode,
    IntPtr securityAttributes,
    FileMode creationDisposition,
    uint flagsAndAttributes,
    IntPtr templateFile);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandleEx(
    SafeFileHandle file,
    int fileInformationClass,
    out FileAttributeTagInfo fileInformation,
    uint bufferSize);
}
