# Civil 3D 对象模型笔记 — ACAD-Bridge

> 目标：整理 Civil 3D COM 对象模型的访问方法
> 核心接口: AeccXUiLand.AeccApplication
> 更新日期: 2026-05-22

---

## 1. 访问 Civil 3D 对象（LSP 侧）

```lisp
;; 获取 Civil 3D Application（版本号需根据实际调整）
(setq c3d (vlax-get-or-create-object "AeccXUiLand.AeccApplication.18.0"))

;; 先确保 AutoCAD 应用已加载
(setq acad (vlax-get-acad-object))
(setq doc (vla-get-ActiveDocument acad))

;; 获取 Civil 3D 文档
(setq c3d-doc (vla-get-ActiveDocument c3d))
```

**版本 ProgID 对照表**（需确认）：

| AutoCAD Version | Civil 3D ProgID |
|----------------|-----------------|
| 2025 | AeccXUiLand.AeccApplication.22.0 |
| 2024 | AeccXUiLand.AeccApplication.21.0 |
| 2023 | AeccXUiLand.AeccApplication.20.0 |
| 2022 | AeccXUiLand.AeccApplication.19.0 |
| 2021 | AeccXUiLand.AeccApplication.18.1 |
| 2020 | AeccXUiLand.AeccApplication.18.0 |

**版本无关的获取方式**（推荐）：
```lisp
(defun get-c3dapp (/ app)
  (vl-catch-all-apply
    (function (lambda ()
      (setq app (vlax-get-or-create-object "AeccXUiLand.AeccApplication")))))
  app)
```

## 2. 曲面（TinSurface / GridSurface）

### 读取曲面信息
```lisp
;; 获取曲面集合
(setq surfaces (vla-get-Surfaces c3d-doc))

;; 遍历曲面
(vlax-for surf surfaces
  (vlax-dump-object surf T)            ; 查看所有可用属性
  (vla-get-Name surf)                  ; 曲面名称
  (vla-get-Description surf)           ; 描述
  (vla-get-StyleName surf)             ; 曲面样式名
)

;; 曲面几何数据
;; 注意：Civil 3D COM 不直接暴露三角网顶点列表
;; 需要通过 SurfaceOutputGeometry 接口
```

### 创建曲面
```lisp
;; 创建 TIN 曲面
(setq surf (vla-Add surfaces "MySurface"))
(vla-put-Description surf "Created by ACAD-Bridge")
(vla-put-StyleName surf "Contours 1m and 5m (Background)")

;; 添加数据到曲面
;; 用 AeccSurface.AddPointsFromFile / AddContourData 等方法
```

## 3. 路线（Alignment）

### 读取路线
```lisp
;; 获取路线集合
(setq alignments (vla-get-Alignments c3d-doc))

(vlax-for align alignments
  (vla-get-Name align)                 ; 路线名称
  (vla-get-Description align)          ; 描述
  (vla-get-StartingStation align)      ; 起始桩号
  (vla-get-EndingStation align)        ; 结束桩号
  (vla-get-Length align)               ; 路线长度
  (vla-get-StyleName align)            ; 路线样式
)

;; 读取路线几何
;; 获取交点列表、曲线参数等
```

### 创建路线
```lisp
;; 创建路线（需要已有布局线/Layout）
(setq align (vla-Add alignments "MyAlignment"))
;; 添加交点、曲线等...
```

## 4. 纵断面（Profile）

### 读取
```lisp
;; 获取路线的纵断面集合
(setq profiles (vla-get-Profiles align))

(vlax-for prof profiles
  (vla-get-Name prof)
  (vla-get-Type prof)                  ; 地面线/设计线
  (vla-get-ProfileGeometry prof)       ; 几何数据
  (vla-get-StationStart prof)
  (vla-get-StationEnd prof)
  (vla-get-ElevationMin prof)
  (vla-get-ElevationMax prof)
)
```

## 5. 装配（Assembly）与 Corridor

### 读取装配
```lisp
(setq assemblies (vla-get-Assemblies c3d-doc))

(vlax-for asm assemblies
  (vla-get-Name asm)
  ;; 获取子装配/Code 等
)
```

### 读取 Corridor
```lisp
(setq corridors (vla-get-Corridors c3d-doc))

(vlax-for cor corridors
  (vla-get-Name cor)
  (vla-get-BaselineRegions cor)        ; 基线区域
  (vla-get-Frequencies cor)            ; 频率
  ;; 提取 Corridor 曲面
  ;; Corridor 会生成多个曲面（顶部、底部、各层）
)
```

## 6. 管网（Pipe Network）

```lisp
(setq pipe-nets (vla-get-PipeNetworks c3d-doc))

(vlax-for net pipe-nets
  (vla-get-Name net)
  ;; 管道
  (vlax-for pipe (vla-get-Pipes net)
    (vla-get-Diameter pipe)
    (vla-get-Material pipe)            ; 管材
  )
  ;; 检查井
  (vlax-for str (vla-get-Structures net)
    (vla-get-Size str)
  )
)
```

## 7. 地块（Parcel）

```lisp
(setq parcels (vla-get-Parcels c3d-doc))
(vlax-for parcel parcels
  (vla-get-Name parcel)
  (vla-get-Area parcel)
  (vla-get-SiteName parcel)            ; 场地名
)
```

## 8. Civil 3D 特有的注意事项

### 8.1 AECC 基础架构
Civil 3D 对象不是 AutoCAD 的普通图元（entmake 做不了），必须通过 COM 访问。
Civil 3D 对象有对应的图形化"代理图元"在模型空间，但修改代理没用，必须修改 COM 对象。

### 8.2 样式和设置
Civil 3D 对象的行为高度依赖样式。修改对象时需要考虑：
- 创建对象时需要指定样式（或使用默认）
- 修改样式会影响显示，但不影响数据
- 样式可以在不修改对象的前提下被修改

### 8.3 对象关系（关键）

```
曲面 ← 等高线数据、点云数据、DEM ...
路线 ← 曲面（作为纵断面父曲面）
路面纵断面 ← 路线 + 曲面
装配 ← 子装配
Corridor ← 路线 + 装配 + 频率设定
Corridor 曲面 ← Corridor
管网 ← 管道 + 检查井
场地 ← 地块 + 路线 + 管网（可选）
```

### 8.4 危险操作 ⚠️
1. **修改曲面数据** — 可能破坏曲面完整性，务必先备份
2. **删除被引用的路线** — 级联失效引用该路线的纵断面和 Corridor
3. **直接 delete Corridor** — 不清理引用的曲面的情况下
4. **大批量修改桩号** — 可能导致图形与数据不一致

**安全守则**：
- 每次写操作前做 `(vla-Save doc)`
- 写 Civil 3D 对象前用 `(command "_.QSAVE")`
- Phase 3 初期只读不写，摸清对象关系再动手

### 8.5 调试技巧

```lisp
;; 看 Civil 3D 对象的所有属性
(vlax-dump-object surf T)

;; 检查类型名称
(vla-get-ObjectName entity)            ; 返回 "AeccDbTinSurface" 等

;; 错误捕获
(vl-catch-all-apply
  (function (lambda ()
    (vla-put-Name surf "NewName"))))

;; 查看可用方法
(vlax-dump-object surf T)  ; 第二个参数 T 才显示方法
```

## 9. 资源参考

- Autodesk Civil 3D COM Developer's Guide
- AeccXLand.tlb / AeccXUiLand.tlb
- [Autodesk 官方 Object Model Diagram](https://help.autodesk.com/view/ACD/2024/ENU/)
