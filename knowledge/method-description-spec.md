# 方法描述规范（Method Description Spec）— 2026-08-25 定

> 适用：CommandDispatcher.cs descriptions 字典（listMethods 返回给 AI 的方法描述）。
> 来源：8-10 全覆盖教训（Bro 污染论）+ 8-25 描述参数一致性排查（183 处不符根因）。

## 硬规则（违反即不合格）

1. **全覆盖**：所有方法必须有描述，不允许缺省。
2. **参数一致性（最重要）**：描述 `{xxx}` 里的参数名**必须与实现代码** `PluginRuntime.GetRequired*/GetOptional*` **读取的参数名完全一致**。新增/修改方法时，描述与实现同步改。
3. **参数格式**：`{param1, param2?, param3?:[]}` —— 可选参数带 `?`；数组参数带 `:[]`（如 `handles:[]`、`points:[]`）；多选一写 `|`（如 `type: rect|polar`）。
4. **禁写频率/偏好词**：不用"常用/推荐/优先/建议"（8-10 污染论：描述有无/措辞不能变成重要性信号）。
5. **禁写 `...` 模糊占位**：`{handle, ...}` 不合格——参数必须列全。只有实现本身就是"任意参数"（如 runLisp）才允许说明性文字。
6. **编码**：CommandDispatcher.cs 保持 UTF-8（无 BOM）；中文描述正常。

## 风格

- 结构：`功能简述 + {参数列表} + 关键约束（如有）`
- 示例：`"旋转成实体 {handle, axisX1, axisY1, axisX2, axisY2, angle}（轴=两点坐标定义，不能穿过 profile；angle 为度）"`
- 关键约束写括号内（轴不能穿过 profile / path 需垂直 / z=中心高度 等），这是 AI 避坑信息
- 坐标对/点类参数：写全坐标分量名（startX,startY,endX,endY），不写 `start:[x,y]` 结构（实现按分量读）

## 校验

- `tools/scan-desc-check.js` 全量校验：严重数（实现必需参数描述缺）必须 = 0
- `tools/scan-desc-check.js --fail-on-severe` 接入 build-plugin.ps1（P4 落地后自动拦截）

## 变更流程

改方法实现（参数增减/改名）→ 同步改描述 → 跑 scan-desc-check → 编译 → 实测。
