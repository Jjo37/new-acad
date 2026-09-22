# AI 生成 SAC 部件（路径 A）— PoC 结论 2026-08-16

## 结论：链路可行，官方 API 支持，立项开发

## ✅ PoC 实测通过（2026-08-16 晚）
- **CreatePktFile 打包成功**：net8.0-windows + SACRuntime DLL（AssemblyResolve 注册）+ System.IO.Packaging（WindowsDesktop 8.0.10），直接构造 PktStructure（Guid/XamlFile/AtcFile/CfgFile/EnumDataFile/PreviewDataFile/ImageFile/WorkingFolder）调 CreatePktFile → 产出 REBUILT.pkt（23.7KB，结构完整）
- **无 dll 的 pkt 导入成功**：REBUILT.pkt 无 CodeDataFile/dll（CreatePktFile 不编译，只打包），importSACSubassembly 导入成功（handle 703E）→ **C3D 导入时用 XAML 实时编译，不需要预编译 dll**
- **环境坑**：SACRuntime 的 WorkflowEngine 需要 System.Runtime 8.0（.NET 8），System.IO.Packaging 8.0 在 WindowsDesktop 8.0.10；net48 编译不行（缺 System.Runtime 8.0），必须 net8.0-windows；AssemblyResolve 需注册指向 SACRuntime 目录

## 实现链路（插件方法）

目标：AI 描述需求 → 自动生成复杂道路部件（像 Subassembly Composer 一样强大）。

## 核心事实（源码级确认）

### 1. .pkt 文件格式 = ZIP 容器
解包 APWACurbs.pkt 内部结构：
- `<GUID>.xaml`（105KB）— **SAC 工作流源码**（标准 XAML，含参数定义 x:Members + 活动树 InternalVariableDefine/FlowStep/Sequence 等，纯文本可读可生成）
- `<GUID>.dll`（6KB）— XAML 编译产物（类 `Subassembly.APWA_Curbs`，基类 System.Object，方法 CompileCodes/Draw/GetInputParameters/GetLogicalNames——这是 SAC 编译器生成的最终部件类）
- `<GUID>.atc` — 工具注册/参数定义（XML：Params/Side/CurbType/SubBaseDepth + DotNetClass 指向 dll）
- `<GUID>.cfg` — 版本信息（Autodesk Subassembly Composer / ForMatterhorn / 12.0.842.0）
- `<GUID>.emd` — 枚举数据（EnumGroup/EnumItem）
- `<GUID>.pvd` — 预览数据（超高/横坡参数）
- `*.png` — 部件图标
- `[Content_Types].xml`

### 2. C3D 自带完整 SAC 编译链（无需装 SAC）
`D:\Program Files\Autodesk\AutoCAD 2025\C3D\SACRuntime\`：
- `Subassembly.WorkflowEngine.dll` — 核心（FileAccess 打包 API + WorkflowHost 运行时）
- `Subassembly.ActivityLibrary.dll` — SAC 活动库
- `Subassembly.API.dll` / `Subassembly.CivilRuntime.dll`
- `System.Activities.dll` + **Roslyn 全套**（Microsoft.CodeAnalysis.CSharp.*）— 编译器

### 3. 官方打包 API（反射确认，全 public）
```
PktFileAccess.CreatePktFile(PktStructure, pktName) [static]   ← 打包 .pkt
PktFileAccess.OpenPkt(subassemblyFileName) [static]           ← 读 .pkt
AtcFileAccess.CreateAtcFile(outputAtcFile, categoryName, categoryDesc, categoryImage, SAName, SADesc, SAVersion, SAImage, SAHelpFile, ParamCollection, xamlFileName) [static]  ← 生成 .atc
CfgFileAccess.SaveConfiguration(cfgFile, Configuration) [static]
PreviewDataFileAccess.Save(pvdFile, PreviewData) [static]
EnumDataFileAccess.Save(emdFile, EnumData) [static]
```
PktStructure 字段全 public 可写：
AtcFile / CfgFile / XamlFile / ImageFile / HelpFile / EnumDataFile / CodeDataFile / PreviewDataFile / OtherFiles / Guid / WorkingFolder

## 实现链路（插件方法）
1. AI 生成 SAC 项目文件：XAML 工作流 + atc/emd/pvd/cfg 元数据（纯文本）
2. 插件方法 `buildSACSubassembly`：
   - 写入临时 WorkingFolder（xaml/atc/emd/pvd/cfg/png）
   - 调 PktFileAccess.CreatePktFile 打包 .pkt
   - 返回 .pkt 路径
3. 用户/AI 调 `importSACSubassembly` 导入 → 像标准部件用

## 关键问题（下一步需解决）
- ~~xaml → dll 编译~~：**已解决**（PoC 实测：无 dll 的 pkt 导入成功，C3D 导入时用 XAML 实时编译，不需要预编译 dll）
- ~~AI 学 SAC 语法~~：**已完成**（knowledge/sac-xaml-guide.md + 4 精简模板，2026-08-16）
- ~~CodeDataFile 字段~~：**已确认**（pkt 无独立 .code 文件，CreatePktFile 不编译）
- ~~buildSACSubassembly 插件方法~~：**已实现并实测**（2026-08-17 端到端回归 server/test-sac-e2e.js（开发仓脚本，不随分发包）全过：XAML→pkt→导入→建装配→挂装配→参数解析完整）

## 状态（2026-08-17 更新）
**✅ 端到端链路已闭环并固化回归测试**：`server/test-sac-e2e.js`（开发仓脚本，不随分发包；需 C3D+relay 在线）
- buildSACSubassembly（XAML→4KB pkt）→ importSACSubassembly（导入 handle）→ createAssembly（assemblyType 枚举）→ importSACSubassembly + assemblyName（挂装配）→ getAssembly 验证子装配 + 参数完整解析（DitchDepth/DitchWidth/Slopes/Side）
- 注意：同名重复导入会 INTERNAL_ERROR——每次测试用唯一名（回归脚本已做）

## 工作量估计
- 插件 buildSACSubassembly：已完成
- SAC XAML 语法文档 + AI 训练：已完成
- 验证（简单部件→复杂部件逐步）：**进行中——下一步：AI 从需求描述自主生成新部件 XAML（非模板），端到端导入验证**

## 市场价值
"AI 描述需求 → 自动生成复杂道路部件" = 市场独一份（无对标）。
