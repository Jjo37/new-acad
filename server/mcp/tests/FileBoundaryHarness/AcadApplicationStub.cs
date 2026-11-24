// 2026-11-18 P32: FileBoundary.cs 在 P6（2026-11-16）加了 DocumentRoots()，直接读
// Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument.Database.Filename。
// 本 harness 故意不引用 accoremgd（要能脱离 AutoCAD 跑），于是 dotnet build 报 CS0103。
// 这里给最小替身：只提供那条链路上用到的成员，测试可以自己塞一个假的文档路径。
namespace Autodesk.AutoCAD.ApplicationServices.Core
{
  internal static class Application
  {
    internal static StubDocumentManager? DocumentManager { get; set; }
  }

  internal sealed class StubDocumentManager
  {
    internal StubDocument? MdiActiveDocument { get; set; }
  }

  internal sealed class StubDocument
  {
    internal StubDatabase? Database { get; set; }
  }

  internal sealed class StubDatabase
  {
    internal string? Filename { get; set; }
  }
}
