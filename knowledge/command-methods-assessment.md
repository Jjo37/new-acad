# 12 个 command 方法评估（2026-08-04）

> 结论：**全部维持 command + 25s 超时兜底，不投入重写**（评估见下）
> 扫描：`node tools/scan-lisp-commands.js`（12 方法 12 处 command 调用）

## 清单与评估

| 方法 | command | 重写可行性 | 决策 |
|------|---------|-----------|------|
| fillet | FILLET | 需手写圆弧过渡几何算法，圆角类型多、边缘情况多 | 维持 |
| chamfer | CHAMFER | 同上（斜面裁剪） | 维持 |
| trim | TRIM | 需自建求交/裁剪逻辑，2D 交互语义复杂 | 维持 |
| extend | EXTEND | 同上 | 维持 |
| stretch | STRETCH | 需自建顶点位移逻辑，跨图元联动复杂 | 维持 |
| overkill | OVERKILL | 重复图元清理算法（去重/合并），工程量大 | 维持 |
| loft | LOFT | ⚠️ `Solid3d.CreateLoftedSolid` 托管包装 NRE（实测 Circle/Region/LoftProfile 全崩） | **LoftedSurface+Thicken 链路**（2026-08-11 实测 13/13 通过） |
| dimAngular | DIMANGULAR | AngularDimension 类在 25.0.58 缺失 | 维持 |
| sectionPlane | SECTIONPLANE | 无公开 API | 维持 |
| convToSurface | CONVTOSURFACE | 无公开 API | 维持 |
| convToSolid | CONVTOSOLID | 无公开 API | 维持 |
| render | RENDER | UI 渲染操作，无 API | 维持 |

## loft 状态更新（2026-08-11 实测）

反射确认 25.0.58 AcDbMgd.dll 存在：

```csharp
void CreateLoftedSolid(Entity[] crossSectionCurves, Entity[] guideCurves, Entity pathCurve, LoftOptions loftOptions)
void CreateLoftedSolid(LoftProfile[] crossSections, LoftProfile[] guides, LoftProfile path, LoftOptions loftOptions)
```

**但实测（2026-08-11，C3D 在线）：两个重载内部都 NullReferenceException**（Circle/Region/LoftProfile 三种传法全崩），
托管包装实现有 bug，不可用。

**替代链路（已实测 13/13 通过）**：

```csharp
using var lofted = new LoftedSurface();            // Autodesk.AutoCAD.DatabaseServices
var builder = new LoftOptionsBuilder();             // 属性可写（Ruled/Closed/DraftStart/DraftEnd...）
builder.Ruled = ...; builder.Closed = ...;
using var opts = builder.ToLoftOptions();
lofted.CreateLoftedSurface(crossSections, guides, path, opts);  // 放样曲面（真 API）
using var solid = lofted.Thicken(thickness, bothSides);         // 加厚成实体（壳）
```

- LoftOptions 属性只读 → 用 LoftOptionsBuilder 设置选项（唯一途径）
- LoftedSurface 还带 `ProjectOnToSurface`（曲线投影到曲面）/`SliceByPlane`/曲面布尔，可后续暴露
- 局限：生成的是**薄壳实体**（厚度=thickness），非实心放样；实心需 LOFT 命令兜底或大厚度近似

## loft 布尔交集方案（原替代方案，降级为备选）

三视图 → 锥体重建用**布尔交集**模拟（2026-07 验证，体积误差 <0.1%）：

```
圆锥 ∩ 月牙柱 = prism − (prism − cone)   // 双重 booleanSubtract 模拟 intersect
```

原理：`intersect(A,B) = A − (A − B)`。Solid3d 有 `BooleanOperation(BoolSubtract)`，
无直接 intersect 时用两次差集实现。

**局限**：只覆盖"由三视图/轮廓重建"场景；自由曲面放样（loft 的典型用途）无法模拟。
→ 2026-08-11 起 CreateLoftedSolid 真 API 已确认存在，本方案仅作"无闭合截面"场景备选。

## 风险与控制

- command 方法在 AutoCAD 命令上下文被占用（模态框/对话框）时会 25s 超时，属预期行为
- 25s 超时只释放队列，不释放文档锁 → **超时后插件可能持续不可用，需重启 C3D**（已知，2026-07 记录）
- 缓解：AI 侧 SOP 已注明避免交互命令；executeCommand 白名单拦截交互命令

## 后续（P4 功能验收时按需排期）

1. 用户高频反馈某方法不可用时再专项重写（优先级：fillet > trim > loft）
2. loft 的布尔交集方案可作为"由三视图重建"专用方法单独实现（新增而非替换）
