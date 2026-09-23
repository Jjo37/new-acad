# new-acad 内置知识库（前提核查 + 注意事项）

> 用途：复杂任务先读本库核对前提，再根据用户实际话语现场组织步骤。
> ⚠️ 设计原则（2026-08-07 Bro 定）：**不提供固定步骤模板**——用户自然语言经常不准确（说"选中"可能没选中、"范围线"可能是别的图层），固定流程会诱导 AI 照搬而翻车。流程由 AI 按实际对话内容现组织，知识库只给：前提核查点 + 注意事项。
> 方法查找：`listMethods` 带 filter（如 filter="surface"），高频方法自带用途描述，禁止猜方法名。

## 一、关键前提核查（精简，2026-08-08）

> 先核实再动手，对不上如实指出。**不逐场景教判断——复杂任务按注入的规划执行。**

0. **"图纸里有什么/模糊指令/全图概览"** → 先调 `getDrawingOverview`（输出单位/图层+数量/块+数量/类型分布/选择集数）
1. **"选中/这些"类** → 先 `getSelection` 确认真的有选中；没选中 → 告诉用户请框选
2. **"导出/生成文件"** → 先确认路径、格式（用户要坐标文件→csv/txt，AI 自用→json）
3. **数字参数拿不准**（半径 vs 直径等）→ 按常识判断，拿不准就问

## 二、注意事项（精简）
- 方法名不确定 → `listMethods` 带 filter 查（禁止猜方法名穷举）
- **支持/不支持由运行时判断**：filter 查→换词再查→仍无→报 [MISSING] 给替代方案
- 大结果截断（≤2KB）→ 需要完整数据换文件通道（导出/读文件）
- `executeCommand`/`runLisp` 及 fillet/chamfer/trim/extend/stretch 等命令类方法返回 `{status:"queued"}`（异步投递、无同步结果），**不代表失败**
- **loftSolid 已是真 API**（2026-08-11：LoftedSurface+Thicken 链路，同步返回 solidHandle；参数 crossSections≥2 闭合曲线 + 可选 guides/path/ruled/closed/draftStart/draftEnd/thickness=1/bothSides；生成的是**薄壳实体**，实心需 LOFT 命令兜底）
- **NURBS 自由曲面三件套**（2026-08-11 新增）：`createNurbsSurface{uCount,vCount,points:[[x,y,z]...]}` → `editNurbsControlPoints{handle, points|moves}` → `thickenSurface{handle,thickness,bothSides?}`；圆润曲面→加厚成薄壁壳
- **创建廊道**（2026-08-16 新增）：`createCorridor{corridorName, alignmentName, assemblyName, baselineName?, regionName?, startStation?, endStation?, profileName?, surfaceName?, style?}`——路线+装配→廊道；profileName 可选（作竖向基准），surfaceName 可选（自动关联曲面）；style 用 listStyles filter='corridor' 查
- **一键给廊道加曲面（算量用）**（2026-09-11 新增，2026-09-21 修）：`addCorridorSurface{name, surfaceName?, codes?[], asBreakLine?, waitForBuild?=true}`——把廊道**链接码**挂成曲面数据并生成曲面；**走 job 通道**（返回 `{jobId, state:"running"}` 属正常，用 `getJobStatus{jobId}` 轮询）。**默认 `waitForBuild=true`：job 等曲面真正生成完才返回（安静窗口策略，实测 ~90s），并回 `buildReady`/`waitedMs`**；等待期间其它请求拿 `JOB_RUNNING`（relay 自动重试），job 结束 CAD 立即可用。`waitForBuild=false` = 秒回（结束瞬间 CAD 仍忙 ~1 分钟、结果不确定）。**实现要点**：写事务走 `App.Idle`（非命令上下文），否则被 C3D 节流到 215~360s 且约一半假失败；**不要每 2s 轮询文档**——会饿死 C3D 的异步再生。不传 `codes` 会**自动收集**廊道链接码；result 里 `surfaceId` = 曲面对象 handle，另有 `addedLinkCodes / isBuild / surfaceCount`。**要出曲面/算量必须调用它**（createCorridor 建的廊道默认无曲面数据，surfaceCount=0）。前提：自定义部件的**链接（links）**要有码——形状码只进材料表、不进曲面。
- **样式/标签集修改**（2026-08-16 新增）：
  - `setAlignmentStyle{alignmentName, style?, labelSet?}`——改已有路线样式/标签集；style 用 `listStyles{objectType:'alignment'}` 查，labelSet 用 `listStyles{objectType:'alignmentlabelset'}` 查
  - `setProfileStyle{alignmentName, profileName, style}`——改已有纵断面样式；style 用 `listStyles{objectType:'profile'}` 查；**纵断面标签集只能在创建时指定**，改带集用 setProfileViewStyle
  - `setProfileViewStyle{profileViewName, style?, bandSet?}`——改纵断面图框样式/带集；**中文纵断面 = 换带集（bandSet）+ 图框样式**；style 用 `listStyles{objectType:'profileview'}`，bandSet 用 `listStyles{objectType:'profileviewbandset'}` 查
- **listStyles 支持类型**：surface/alignment/profile/corridor/pipe/structure/point/section/assembly/profileview/profileviewbandset/**alignmentlabelset**/**profilelabelset**
- **createAssembly 的 assemblyType 枚举值**：`UndividedCrownedRoad`（单幅路拱）/`UndividedPlanarRoad`（单幅平）/`DividedCrownedRoad`（双幅路拱）/`DividedPlanarRoad`（双幅平）/`Other`/`Railway`——创建道路装配必填，拿不准用 UndividedCrownedRoad（市政路常用）
- **子装配（路面结构层）**：`listStockSubassemblies{category?}` 查全部可用类名（Basic/Lanes/Shoulders/Daylight/CurbAndGutters 等 12 类 ~109 个）；`createSubassembly{assemblyName, subassemblyType, side: Left|Right|Both, parameters?}` 添加。
  - **类名必须带 `Subassembly.` 前缀**（如 `Subassembly.BasicLane`；不带前缀插件会自动补，但建议按目录原样传）
  - **路面层**：`Subassembly.BasicLane`（单层车道，parameters: Width/Slope/Thickness）或 `Subassembly.LaneSuperelevationAOR`（超高车道）；多结构层用 `Subassembly.GenericPavementStructure`（多层路面结构）或多次 BasicLane 叠加
  - **路肩**：`Subassembly.ShoulderExtendAll`/`Subassembly.ShoulderMultiLayer`；**放坡**：`Subassembly.DaylightStandard`/`Subassembly.DaylightGeneral`（到地面线）；**路缘石**：`Subassembly.UrbanCurbGutterGeneral`
  - **典型道路结构层（市政路）**：路面=BasicLane(厚度=面层+基层合计)，或 GenericPavementStructure 分列 4cm细沥青/6cm粗沥青/32cm水稳/30cm灰土各层；放坡用 DaylightStandard(1:1)；拓宽用 LaneOutsideSuperWithWidening
  - 注：subassemblyType 必须来自 listStockSubassemblies 的类名，禁止猜；parameters 键名=子装配参数名（如 Width/Slope/Thickness）
- **SAC 自定义部件导入**（2026-08-16 新增）：`importSACSubassembly{subassemblyName, pktFilePath, insertX?, insertY?}`——导入 Subassembly Composer 做的 .pkt 部件文件（如花岗岩平石/侧石/边石/树池缘石等非标准件），导入后像标准部件一样用 editAssembly/装配加区域；pktFilePath 是用户提供的 .pkt 文件绝对路径
- **AI 生成 SAC 部件（核心！2026-08-16）**：`buildSACSubassembly{subassemblyName, xamlContent, categoryName?, description?, atcContent?, paramsXml?}` → 返回 pktFilePath → `importSACSubassembly` 导入。**xamlContent = SAC 工作流 XAML**（标准 .NET 工作流活动：InternalVariableDefine/FlowStep/Sequence/Point/Link/Shape 等，x:Members 定义参数）。插件自动生成 cfg/emd/pvd 最小模板 + CreatePktFile 打包；**C3D 导入时实时编译 xaml，不需要预编译 dll**。实测：APWACurbs 的 xaml 打包导入全通。环境：SACRuntime DLL 在 C3D 安装目录，System.IO.Packaging 需 WindowsDesktop 8.0
  - **SAC XAML 写法**：先读 `knowledge/sac-xaml-guide.md`（完整语法：骨架/活动清单/表达式/流程控制）+ `knowledge/sac-templates/*.xaml`（4 个精简模板：EvenSlopeDitch 最简入门/CurbWallRadius 含曲线/ShoulderRoundedTarget 含目标曲面/PermeablePavement 复杂）——用 readFile 读模板再改；**ViewState 布局信息可删**（实测精简版导入成功），AI 只需写核心活动逻辑；生成后 buildSACSubassembly→importSACSubassembly 验证，失败按报错迭代
- **编程创建自定义部件**（2026-08-16 新增）：`createCustomSubassembly{subassemblyName, points:[{offset,elevation,code?}], links:[{pointIndices:[],code?}], shapes:[{linkIndices:[],code?}], assemblyName?, side?, insertX?, insertY?}`——无 .pkt 文件时直接定义断面几何（offset=横向距离, elevation=高程）；links 的 pointIndices 引用 points 索引，shapes 的 linkIndices 引用 links 索引（封闭形状至少 3 条边）；实测 4点4线1形状挂装配成功。**注意：自定义部件默认无放坡/超高行为，仅静态几何**（要复杂行为还是得用 SAC）；**autoLinkCodes 默认 true**（2026-09-11）：链接没写 `code` 会自动补——优先用所属 shape 的码，仍无则 `Link{n}`；**廊道曲面/算量靠链接码**，所以别随手关（`autoLinkCodes:false` 可关）；返回里 `autoCodedLinkCodes` 告知补了哪些
