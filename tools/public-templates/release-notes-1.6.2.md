## new-acad v1.6.2（2026-09-23）

> 本版新增**填充（Hatch）能力**并修掉一个颜色丢失的 bug。直接覆盖安装即可，无需改配置，API Key 与设置保留。

### 新增能力

- **填充（Hatch）**：AI 现在能创建、批量创建和**编辑**填充
  - 单环、**多环带岛**（挖孔）、图案填充（SOLID 实体 / ANSI31 等）、**双色渐变**（LINEAR / CYLINDER / SPHERICAL / HEMISPHERICAL / CURVED…）
  - 可调图案比例、角度、颜色、图层、岛样式、关联、原点；**编辑时可整体替换边界**
  - 填充可**被选中**（类型选 HATCH）、可**量面积**、可**读全部属性**（图案/比例/角度/岛样式/面积/环数/渐变名与端点色）
- **描摹结果直接填充**：`traceImage` 新增 `fill` 参数 —— 扁平插画/色块图可以直接**填充着色**画进 CAD（如卡通壁纸 → 一排带色块面）

### 修复

- **逐项颜色此前被静默丢弃**：批量落地矢量路径时，路径自带的颜色（色块描摹的每一块颜色）没有被写入 CAD —— 现在颜色真正生效（支持 RGB 与 ACI 索引两种写法）
- 描摹阈值参数 `inkGamma`（默认不变，0.5 可让白底线稿更干净）
- 面板助手 SOP：补充填充能力说明

### 安装包（推荐）
- `new-acad-setup-v1.6.2.exe` —— 双击向导（中文 / English），填入自己的 LLM API Key

### 绿色版
- `new-acad-v1.6.2.zip` —— 解压后双击 `install.bat`

### 商店版（Autoloader）
- `new-acad.bundle.zip` —— 解压得 `new-acad.bundle`，复制到 `%APPDATA%\Autodesk\ApplicationPlugins\` 即可

**要求**：Windows + 正版 AutoCAD / Civil 3D 2025 / 2026 + 一个 LLM API Key
**License**：MIT ｜ 上游致谢：Civil3D-mcp
