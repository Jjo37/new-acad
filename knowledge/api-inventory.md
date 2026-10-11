# new-acad API 清单

> 最后更新: 2026-08-07
> 调用方式: TCP :8080 (C# 插件) + HTTP :3000 (Sacred MCP) + LISP (runLisp)
> **方法数口径**：实际方法数以 listMethods 实时清单为准（2026-08-07 约 344 反射 / 337 注册）；MCP 通道：HTTP :3000/execute 可调 271 个，标准 MCP 客户端（stdio）默认仅 35 个 canonical 工具（alias 需 CIVIL3D_ENABLE_TOOL_ALIASES=true）

---

## 一、总览

| 通道 | 地址 | 方法数 | 来源 |
|------|------|--------|------|
| **TCP** | `127.0.0.1:8080` JSON-RPC | 以 listMethods 实时清单为准（约 344 反射/347 注册） | C3D 插件 (AcBridge-v24) |
| **MCP** | `127.0.0.1:3000 POST /execute` | 208 | Sacred MCP 桥 |
| **LISP** | `runLisp`（2026-08-07 已修复） | ∞ | SendStringToExecute 投递执行（无返回值通道，适合操作类脚本；查询类用专用方法） |

调用优先级: **TCP > MCP > LISP**

> 2026-07-31: TCP 方法增至 335（新增 createBox/createCylinder/createSphere/createCone/createWedge/createTorus 3D 实体 API）
> 2026-08-05: 新增 listMethods（反射自动扫描），方法数 337（2026-08-06 静态扫描确认；运行时以 listMethods 实时清单为准）
> 2026-08-07: 新增文件通道方法 addSurfacePointsFromFile（参数化）/ importCogoPointsFromFile / exportSelectionToFile + 批量方法；方法数口径统一改为 **以 listMethods 实时清单为准**（不再写死）

---

## 二、TCP 方法（:8080）

### 2.1 自研方法（手动添加）

这些是我加在插件里的通用 CAD 操作，不走 Sacred MCP。

| 方法 | 参数 | 返回 | 说明 |
|------|------|------|------|
| `listMethods` | — | `{count, methods[]}` | **先调这个拿真实方法清单**（反射自动扫描，2026-08-05 新增） |
| `getSelection` | — | `{count, handles, source, docName}` | 读自动保存的选中图元；`docName` 用于多图纸比对（先 getDrawingInfo 确认当前文档） |
| `saveSelection` | — | `{count, handles}` | 重捕当前选择集（面板发送时自动调用） |
| `getEntityInfo` | `{handle}` | `{type, layer, color, text?}` | 按 handle 查图元属性（Polyline 含 bulges 弧线） |
| `setEntityColor` | `{handle, color}` | — | 改颜色 (1=红, 2=黄, 3=绿...) |
| `mirrorEntity` | `{handle}` | `{mirroredHandle}` | 沿垂直中心轴镜像 |
| `getCivil3DHealth` | — | health 状态 | 检测插件在线 |
| `getDrawingInfo` | — | drawing 信息 | 文件名/单位/坐标系 |

### 2.2 创建图元

| 方法 | 参数 | 说明 |
|------|------|------|
| `createPolyline` | `{points:[{x,y}], closed?, layer?}` | 画多段线 |
| `createCircle` | `{center:[x,y], diameter}` | 画圆 |
| `createText` | `{text,x,y,height?}` | 写单行文字 |
| `createMText` | `{text,x,y,textHeight?,width?}` | 写多行文字 |
| `create3dPolyline` | `{points:[{x,y,z}], closed?}` | 画 3D 多段线 |
| `createLineSegment` | `{startX,startY,endX,endY}` | 画直线 |
| `createSurface` | `{name, style?, layer?}` | 创建三角网曲面 |
| `addSurfacePoints` | `{name, points:[{x,y,z}]}` | 曲面加点（少量用；**大量数据用 addSurfacePointsFromFile 走文件通道**） |
| `addSurfaceBreakline` | `{name, points:[{x,y,z}]}` | 曲面加断裂线 |
| `addSurfaceBoundary` | `{name, ...}` | 曲面加边界 |
| `addSurfacePointsFromFile` | `{name, filePath, delimiter?, hasHeader?, xCol?, yCol?, zCol?, skipRows?}` | **文件通道**（2026-08-07 新增）：任意文本格式批量加曲面点；delimiter: auto/comma/space/tab/semicolon，AI 判断格式传参，插件解析；>500 数据强制走文件通道 |

### 2.3 查询（C3D 对象）

#### 曲面
`listSurfaces`, `getSurface`, `getSurfaceElevation`, `getSurfaceElevationsAlong`, `getSurfaceStatistics`, `getSurfaceStatisticsDetailed`, `analyzeSurfaceSlope`, `analyzeSurfaceElevation`, `analyzeSurfaceDirections`, `sampleSurfaceElevations`, `createSurfaceFromDem`, `setSurfaceContourInterval`, `extractSurfaceContours`, `deleteSurface`

#### 路线 (Alignment)
`listAlignments`, `getAlignment`, `createAlignment`, `deleteAlignment`, `alignmentStationToPoint`, `alignmentPointToStation`, `alignmentSampleStations`, `alignmentAddTangent`, `alignmentAddSpiral`, `alignmentGetStationOffset`, `alignmentOffsetCreate`, `alignmentWidenTransition`, `alignmentSetStationEquation`, `alignmentDeleteEntity`

#### 纵断面 (Profile)
`listProfiles`, `getProfile`, `getProfileElevation`, `sampleProfileElevations`, `createProfileFromSurface`, `createLayoutProfile`, `deleteProfile`, `profileAddPvi`, `profileDeletePvi`, `profileAddCurve`, `profileSetGrade`, `profileCheckKValues`, `profileViewCreate`, `profileViewBandSet`

#### 廊道 (Corridor)
`listCorridors`, `getCorridor`, `rebuildCorridor`, `getCorridorSurfaces`, `getCorridorFeatureLines`, `computeCorridorVolumes`, `getCorridorTargetMappings`, `setCorridorTargetMappings`, `addCorridorRegion`, `deleteCorridorRegion`

#### 管网 (Pipe)
`listPipeNetworks`, `getPipeNetwork`, `getPipe`, `getStructure`, `createPipeNetwork`, `addPipeToNetwork`, `resizePipeInNetwork`, `addStructureToNetwork`, `listPipePartsCatalog`, `checkPipeNetworkInterference`, `calculatePipeNetworkHgl`, `analyzePipeNetworkHydraulics`, `getPipeStructureProperties`

#### 压力管网 (Pressure)
`listPressureNetworks`, `getPressureNetworkInfo`, `createPressureNetwork`, `deletePressureNetwork`, `addPressurePipe`, `addPressureFitting`, `addPressureAppurtenance`, `assignPressurePartsList`, `setPressureNetworkCover`, `validatePressureNetwork`, `exportPressureNetwork`, `connectPressureNetworks`, `getPressurePipeProperties`, `resizePressurePipe`, `getPressureFittingProperties`

#### 放坡 (Grading)
`listGradingGroups`, `getGradingGroup`, `createGradingGroup`, `deleteGradingGroup`, `getGradingGroupVolume`, `createSurfaceFromGradingGroup`, `listGradings`, `getGrading`, `createGrading`, `deleteGrading`, `listFeatureLines`, `getFeatureLine`, `exportFeatureLineAsPolyline`, `createFeatureLine`

#### 地块 (Parcel)
`listParcelSites`, `listParcels`, `getParcel`, `createParcel`, `editParcel`, `adjustParcelLotLine`, `reportParcels`

#### 测量 (Survey)
`listSurveyDatabases`, `createSurveyDatabase`, `listSurveyFigures`, `getSurveyFigure`, `listSurveyObservations`, `adjustSurveyNetwork`, `createSurveyFigure`, `importSurveyLandXml`

#### 点 (CogoPoint)
`listCogoPoints`, `getCogoPoint`, `createCogoPoints`, `deleteCogoPoints`, `listPointGroups`, `createPointGroup`, `updatePointGroup`, `deletePointGroup`, `importCogoPoints`, `importCogoPointsFromFile`（**文件通道**，2026-08-07 新增：`{format, filePath}` 从文件导入）, `exportCogoPoints`, `transformCogoPoints`

#### 横断面 (Section)
`listSampleLineGroups`, `getSectionData`, `createSampleLines`, `createSectionViews`, `listSectionViews`, `updateSectionViewStyles`, `createSectionViewGroup`, `exportSectionData`

#### 汇水区 (Catchment)
`listCatchmentGroups`, `getCatchmentGroup`, `listCatchments`, `getCatchmentProperties`, `setCatchmentProperties`, `copyCatchmentToGroup`, `getCatchmentFlowPath`, `getCatchmentBoundary`, `calculateTimeOfConcentration`, `generateHydrograph`, `listTcMethods`

#### 标签 (Label)
`listLabelStyles`, `listLabels`, `addLabel`

#### 样式 (Style)
`listStyles`, `getStyle`

### 2.4 图纸操作

| 方法 | 说明 |
|------|------|
| `newDrawing` | 新建图纸 |
| `saveDrawing` | 保存图纸 |
| `undoDrawing` | 撤销 |
| `redoDrawing` | 重做 |
| `getDrawingSettings` | 图纸设置 |
| `getProjectContext` | 项目上下文 |
| `getCoordinateSystemInfo` | 坐标系信息 |
| `transformCoordinates` | 坐标转换 |
| `listCivilObjectTypes` | 列出 C3D 对象类型 |
| `getSelectedCivilObjectsInfo` | 当前选中 C3D 对象 |
| `exportSelectionToFile` | **文件通道**（2026-08-07 新增）：`{path}` 选中集完整几何写 JSON（已存在拒绝） |

### 2.5 工程量 / 质检

#### 工程量 (Quantity)
`qtyCorridorVolumes`, `qtySurfaceVolume`, `qtyPipeNetworkLengths`, `qtyPressureNetworkLengths`, `qtyParcelAreas`, `qtyAlignmentLengths`, `qtyPointCountByGroup`, `qtyExportToCsv`, `qtyMaterialListGet`, `qtyEarthworkSummary`, `exportPayItems`, `calculateMaterialCostEstimate`

#### 质检 (QC)
`qcCheckAlignment`, `qcCheckProfile`, `qcCheckCorridor`, `qcCheckPipeNetwork`, `qcCheckSurface`, `qcCheckLabels`, `qcCheckDrawingStandards`, `qcFixDrawingStandards`, `qcReportGenerate`

### 2.6 其他

| 方法 | 说明 |
|------|------|
| `cogoInverse` | 坐标反算 |
| `cogoDirectionDistance` | 方向距离 |
| `cogoTraverse` | 导线测量 |
| `cogoCurveSolve` | 曲线计算 |
| `calculateSightDistance` | 视距计算 |
| `checkStoppingDistance` | 停车视距 |
| `calculateDetentionBasinSize` | 调蓄池计算 |
| `calculateDetentionStageStorage` | 水位-库容计算 |
| `calculateSlopeGeometry` | 边坡几何 |
| `checkSlopeStability` | 边坡稳定 |
| `getJobStatus`, `cancelJob`, `startJob` | 任务管理 |
| `exportStm`, `importStm`, `openStormSanitaryAnalysis`, `listSsaCapabilities` | SWMM 分析 |
| `listSheetSets`, `getSheetSetInfo`, `createSheetSet`, `addSheet`, `getSheetProperties`, `setSheetTitleBlock`, `createSheetView`, `setSheetViewScale`, `publishSheetPdf`, `exportSheetSet` | 图纸集 |

---

## 三、Sacred MCP 工具（:3000 POST /execute）

格式: `{tool: "工具名", parameters: {...}}`

### 3.1 通用 CAD（4 个）

| 工具 | 说明 |
|------|------|
| `acad_create_polyline` | 画多段线 |
| `acad_create_text` | 写文字 |
| `acad_create_3dpolyline` | 画 3D 多段线 |
| `acad_create_mtext` | 写多行文字 |

### 3.2 C3D 域（204 个）

按域分组，每个域通常有 `list`、`get`、`create` 等操作：

| 域 | 工具数 | 包含操作 |
|-----|--------|----------|
| `civil3d_surface` | 14 | 创建/查询/编辑/体积分析/坡向分析 |
| `civil3d_alignment` | 11 | 创建/查询/切线/缓和曲线/加宽 |
| `civil3d_profile` | 12 | 创建/查询/PVI/竖曲线/视图 |
| `civil3d_corridor` | 10 | 创建/重建/目标映射/体积 |
| `civil3d_pipe` | 15 | 创建/管道/检查井/水力分析 |
| `civil3d_pressure_network` | 14 | 创建/删除/验证/导出 |
| `civil3d_grading` | 15 | 放坡组/放坡/特征线 |
| `civil3d_point` | 11 | 创建/导入/导出/点编组 |
| `civil3d_parcel` | 6 | 创建/编辑/调整界线 |
| `civil3d_section` | 8 | 采样线/横断面视图 |
| `civil3d_qty` | 8 | 土方/长度/面积统计 |
| `civil3d_qc` | 10 | 质检/报告 |
| `civil3d_detention` | 2 | 调蓄池计算 |
| `civil3d_survey` | 5 | 测量库/图形/观测 |
| `civil3d_sheet_set` | 11 | 图纸集管理/PDF 发布 |
| ...其他 | ... | 水文、装配、交叉口、成本估算等 |

完整 208 个工具: `GET http://127.0.0.1:3000/tools`

---

## 四、覆盖分析

### 你常用的场景 × 有没有方法

| 你要做 | TCP | MCP | 推荐路径 |
|--------|-----|-----|----------|
| 选中图元 → 查属性 | ✅ `getEntityInfo` | ❌ | TCP |
| 选中图元 → 变色 | ✅ `setEntityColor` | ❌ | TCP |
| 选中图元 → 镜像 | ✅ `mirrorEntity` | ❌ | TCP |
| 选中图元 → 删除/移动/复制/旋转/缩放 | ✅ `deleteEntity`/`moveEntity`/`copyEntity`/`rotateEntity`/`scaleEntity` | ❌ | TCP |
| 画线/画圆/写字/3D实体 | ✅ | ✅ `acad_create_*` | TCP (无需审批) |
| 读曲面/路线数据 | ✅ | ✅ `civil3d_surface/alignment` | MCP (懒得记TCP名时) |
| 图层开关/冻结/锁定 | ✅ `layerOff`/`layerFreeze`/`layerLock` 等 | ❌ | TCP |
| 统计图元数量 | ✅ `selectByCriteria` + `getEntityInfo` | ❌ | TCP |
| 批量改文字高度 | ✅ `scaleText` | ❌ | TCP |
| 块操作/外部参照 | ✅ `createBlock`/`insertBlock`/`writeBlock`/`attachXref` | ❌ | TCP |
| 3D 布尔/拉伸/扫掠/切片 | ✅ `booleanUnion`/`booleanSubtract`/`extrudeSolid`/`sweepSolid`/`sliceSolid` | ❌ | TCP |
| 放样/NURBS自由曲面/加厚 | ✅ `loftSolid`(真API 2026-08-11)/`createNurbsSurface`/`editNurbsControlPoints`/`thickenSurface` | ❌ | TCP |

### 剩余缺口（无公开 API，保留 command 实现）

| 缺口 | 影响 | 现状 |
|------|------|------|
| fillet/chamfer/trim/extend/stretch/overkill | 编辑类几何操作 | command + 25s 超时兜底 |
| loft | 放样成体 | ⚠️ `Solid3d.CreateLoftedSolid` 在 25.0.58 托管包装内部 NRE（实测三种传法全崩，2026-08-11）→ **loftSolid 已改用 `LoftedSurface.CreateLoftedSurface` + `Surface.Thicken` 链路**（放样曲面+加厚成壳，同步返回 handle；支持 ruled/closed/拔模 via LoftOptionsBuilder） |
| dimAngular | 角度标注 | AngularDimension 类缺失 |
| sectionPlane/convToSurface/convToSolid | 剖切面/转换 | 无公开 API |
| render | 渲染 | UI 操作无 API |
