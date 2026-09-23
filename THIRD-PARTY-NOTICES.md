# 第三方组件与许可说明 (Third-Party Notices)

本仓库包含或依赖以下第三方作品。各自版权归原作者所有，按各自许可证条款使用。

## 1. Civil3D-mcp（上游开源项目）

- 项目：**Civil3D-mcp**
- 许可证：**MIT License**，https://github.com/Sacred-G/Civil3D-mcp
- 本仓库引用位置：`server/mcp/`（TypeScript 源码 + 编译产物 `build/`）
- 许可证原文：见 `server/mcp/LICENSE`（未经修改，随源码一并保留）
- 说明：本仓库在其基础上做了定制与扩展（如 `hankDomain`、方法描述规范化、本地化等）。
  依 MIT 条款，上游版权声明与许可证原文予以保留。

## 2. Autodesk 相关

- 本项目**非 Autodesk 官方产品**，与 Autodesk 无隶属或背书关系。
- 运行需自行安装并授权正版 **AutoCAD / Civil 3D**（2024 / 2025 / 2026）。
- 本仓库**不包含、不分发**任何 Autodesk 二进制文件（`AcDbMgd` / `AecBaseMgd` / `AeccDbMgd` 等
  托管程序集仅作为编译引用，已在 `.gitignore` 中排除 `C_References/`）。

## 3. npm 依赖

- `server/mcp/` 下的依赖及其许可证以 `server/mcp/package.json` / `package-lock.json` 为准。
- 本项目自有代码（插件 C#、relay、面板）不引入额外第三方运行时依赖。

## 4. 本仓库自身

- 本项目采用 **MIT License**，见 `LICENSE`。
