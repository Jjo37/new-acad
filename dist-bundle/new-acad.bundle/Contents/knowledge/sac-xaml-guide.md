# SAC XAML 部件语法指南（AI 生成用）

> 2026-08-16 从 C3D 自带 73 个 .pkt 模板反解整理。
> 用途：`buildSACSubassembly {xamlContent}` 的 XAML 写法参考。AI 按此语法生成 SAC 工作流 XAML。

## 一、XAML 骨架（固定结构）

```xml
<Activity mc:Ignorable="sads sap" x:Class="Subassembly"
 this:Subassembly.Side="[new EnumType(0, &quot;Right&quot;, &quot;Side&quot;)]"
 this:Subassembly.参数名="默认值"
 xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
 xmlns:asa="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.API"
 xmlns:asa1="clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"
 xmlns:asw="clr-namespace:Autodesk.SubassemblyComposer.WorkflowEngine;assembly=Subassembly.WorkflowEngine"
 xmlns:av="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
 xmlns:mva="clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities"
 xmlns:s="clr-namespace:System;assembly=mscorlib"
 xmlns:sa="clr-namespace:System.Activities;assembly=System.Activities"
 xmlns:sads="http://schemas.microsoft.com/netfx/2010/xaml/activities/debugger"
 xmlns:sap="http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation"
 xmlns:scg="clr-namespace:System.Collections.Generic;assembly=mscorlib"
 xmlns:this="clr-namespace:"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:Members>
    <!-- 3 个固定成员 -->
    <x:Property Name="Geometry" Type="InOutArgument(asw:Geometry)" />
    <x:Property Name="SubassemblyErrorCenter" Type="InOutArgument(asw:SubassemblyErrorCenter)" />
    <x:Property Name="SubassemblyRunMode" Type="InOutArgument(asw:SubassemblyRunMode)" />
    <!-- 用户参数：InArgument(x:Double) 数字 / InArgument(asw:EnumType) 枚举 / InArgument(asw:Slope) 坡度 -->
    <x:Property Name="DitchDepth" Type="InArgument(x:Double)">
      <x:Property.Attributes>
        <asw:EnabledFlag2Attribute EnabledFlag="True" />
        <asw:DisplayName2Attribute DisplayName="Ditch Depth" />
        <asw:Description2Attribute Description="描述" />
      </x:Property.Attributes>
    </x:Property>
  </x:Members>
  <Flowchart sap:VirtualizedContainerService.HintSize="614,636">
    <Flowchart.Variables>
      <!-- 内部变量（枚举常量定义） -->
      <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(1, &quot;Left&quot;)]" Modifiers="ReadOnly" Name="Left" />
      <Variable x:TypeArguments="x:Double" Default="1" Name="内部数字变量" />
    </Flowchart.Variables>
    <Flowchart.StartNode>
      <FlowStep x:Name="__ReferenceID0">
        <!-- 活动写在这里 -->
      </FlowStep>
    </Flowchart.StartNode>
    <!-- 后续 FlowStep 用 .Next 串接 -->
  </Flowchart>
</Activity>
```

## 二、命名空间速记
- `asa1:` = SAC 活动库（CreatePoint/CreateLink/CreateShape 等）
- `asw:` = 引擎类型（Geometry/EnumType/Slope/DisplayName2Attribute）
- 标准活动（FlowStep/FlowDecision/Sequence/Variable）无前缀

## 三、核心活动清单（73 模板统计，按使用频率）

### 1. CreatePoint（1027 次）— 创建点
```xml
<asa1:CreatePoint AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" FromPoint="{x:Null}"
 ActivityId="1" ApplyAOR="False" AutoLink="False" DisplayName="P1" Geometry="[Geometry]"
 PointNumber="P1" Positioning="DeltaXAndDeltaY" ShowErrors="True" Side="[Side]"
 SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
  <asa1:CreatePoint.Arguments>
    <InArgument x:TypeArguments="x:Double" x:Key="DeltaX1">0</InArgument>
    <InArgument x:TypeArguments="x:Double" x:Key="DeltaY1">0</InArgument>
  </asa1:CreatePoint.Arguments>
  <asa1:CreatePoint.PointCodes>
    <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
      <InArgument x:TypeArguments="x:String">Hinge</InArgument>
    </scg:List>
  </asa1:CreatePoint.PointCodes>
</asa1:CreatePoint>
```
**Positioning 模式**：`DeltaXAndDeltaY`（相对偏移，Arguments=DeltaX1/DeltaY1）、`DeltaXAndSlope`（DeltaX1+Slope1）、`AngleAndDistance`、`FromSurface`（贴地面）、`FromPoint`（参照点+偏移）

### 2. CreateLink（761 次）— 连线
```xml
<asa1:CreateLink ActivityId="2" ApplyAOR="False" DisplayName="L1" EndPoint="P2" Geometry="[Geometry]" IsEnabled="True" LinkNumber="L1" ShowErrors="True" StartPoint="P1" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
  <asa1:CreateLink.LinkCodes>
    <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
      <InArgument x:TypeArguments="x:String">Top</InArgument>
    </scg:List>
  </asa1:CreateLink.LinkCodes>
</asa1:CreateLink>
```
**真实属性**（2026-09-09 实测校正，PermeablePavement.xaml 验证）：`StartPoint` / `EndPoint`（引用已建点 PointNumber）+ **`LinkNumber`（=线名，CreateShape 引用它）**；LinkCodes 放子元素。⚠️ 旧写法 `LinkType="NextPoint" Point1= Point2=` **C3D 不识别，几何不生成**（createCustomSubassembly 重写时踩坑）。需要非两点连线时用 CreatePoint 的 `AutoLink="True" AutoLinkGeometryName="Lx"`（创建点时自动连线，EvenSlopeDitch 全用此法）；复杂 LinkType（FromSurface/SlopeToSurface）另查模板。

### 3. CreateAuxPoint（403 次）— 辅助点
类似 CreatePoint 但 `PointNumber` 前加 "Aux"，不参与最终几何

### 4. InternalVariableDefine（333 次）— 内部变量定义
```xml
<asa1:InternalVariableDefine ActivityId="13" DisplayName="IsMetric &lt;Yes\No&gt;"
 ShowErrors="True" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"
 VariableName="IsMetric" VariableType="Yes\No">
  <asa1:InternalVariableDefine.DefaultValue>
    <InArgument x:TypeArguments="asw:EnumType">[No]</InArgument>
  </asa1:InternalVariableDefine.DefaultValue>
</asa1:InternalVariableDefine>
```
**VariableType**：`String` / `Double` / `Yes\No`（枚举）/ `EnumType`

### 5. CreateShape（141 次）— 封闭形状
```xml
<asa1:CreateShape ActivityId="3" DisplayName="S1" Geometry="[Geometry]" Links="L1,L2,L3,L4" ShapeNumber="S1" ShowErrors="True" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
  <asa1:CreateShape.ComponentNames>
    <x:String>L1</x:String>
    <x:String>L2</x:String>
    <x:String>L3</x:String>
    <x:String>L4</x:String>
  </asa1:CreateShape.ComponentNames>
  <asa1:CreateShape.ShapeCodes>
    <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
      <InArgument x:TypeArguments="x:String">Curb</InArgument>
    </scg:List>
  </asa1:CreateShape.ShapeCodes>
</asa1:CreateShape>
```
**真实属性**（2026-09-09 实测校正）：`Links="L1,L2,..."`（逗号分隔线名，对应 CreateLink 的 LinkNumber）+ `ShapeNumber` + 子元素 `ComponentNames`（逐行列出线名）/`ShapeCodes`。⚠️ 旧写法 `ShapeType="Closed"` + `<asa1:CreateShape.Links><InArgument>...</InArgument>` 子元素列表 **C3D 不识别**（生成器解析不到线，造型不出现）。

**⚠️ Flowchart 尾部必须注册所有 FlowStep**（2026-09-09 实测）：`</Flowchart.StartNode>` 后每个 `__ReferenceIDn` 都要一行 `<x:Reference>__ReferenceIDn</x:Reference>`，缺失时几何不生成（buildSACSubassembly 链路 createCustomSubassembly 重写时踩坑，AIShoulder.xaml 无 x:Reference 也能跑是特例——AI 生成新部件一律加上保险）。

### 6. AuxIntersection（127 次）/ LinkIntersection（47 次）— 交点
两条线/面的交点生成辅助点

### 7. SetVariableValue（75 次）— 变量赋值
```xml
<asa1:SetVariableValue ActivityId="10" DisplayName="设置变量" ShowErrors="True"
 SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"
 VariableName="MyVar" VariableValue="[表达式]" />
```

### 8. CreateCurve（11 次）— 曲线 / FilletArcActivity（1 次）— 倒圆角

## 四、流程控制

### FlowStep（1500 次）— 串行步骤
```xml
<FlowStep x:Name="__ReferenceID0">
  <asa1:CreatePoint ... />
  <FlowStep.Next>
    <FlowStep x:Name="__ReferenceID1">
      <asa1:CreateLink ... />
    </FlowStep>
  </FlowStep.Next>
</FlowStep>
```

### FlowDecision（310 次）— 条件分支
```xml
<FlowDecision x:Name="__ReferenceID2" DisplayName="If Fill or Cut" Condition="[FillOrCut = &quot;Fill&quot;]">
  <FlowDecision.True>
    <FlowStep x:Name="__ReferenceID3"><asa1:CreatePoint ... /></FlowStep>
  </FlowDecision.True>
  <FlowDecision.False>
    <FlowStep x:Name="__ReferenceID4"><asa1:CreatePoint ... /></FlowStep>
  </FlowDecision.False>
</FlowDecision>
```
常见条件：`[FillOrCut = "Fill"]` / `[Side = "Right"]` / `[IsMetric = "Yes"]` / `[数值比较]`

### Sequence（388 次）— 分组
```xml
<Sequence DisplayName="Codes">
  <asa1:InternalVariableDefine ... />
</Sequence>
```

## 五、常见表达式语法（VB 风格，`[ ]` 包裹）
- 引用参数：`[DitchDepth]` / `[Side]`
- 数字运算：`[DitchWidth / 2]`、`[0.125 + 0.06]`
- 坡度：`[new Slope(0.5)]`（0.5 = 1:0.5？实测为比值）、`[Slopes]`
- 枚举：`[new EnumType(1, "Left")]`（1=Left, 0=Right）
- 字符串比较：`[FillOrCut = "Fill"]`
- 条件：`[If(条件, 真值, 假值)]`、`[条件 AndAlso 条件2]`

## 六、生成步骤（AI 工作流）
1. 理解需求 → 定参数（x:Members）和几何逻辑
2. 写 XAML 骨架（复制上面模板，改参数名/默认值）
3. 按逻辑顺序加 FlowStep：InternalVariableDefine（常量）→ CreatePoint（关键点）→ CreateLink（连线）→ CreateShape（封闭）
4. 需要分支 → FlowDecision；需要贴地面 → CreatePoint Positioning="FromSurface"
5. 调 buildSACSubassembly 打包 → importSACSubassembly 导入 → 若失败根据报错迭代

## 七、模板文件（knowledge/sac-templates/，供参考）
- **EvenSlopeDitch.xaml**（16KB）— 最简，含 FromSurface/条件分支，最佳入门
- **CurbWallRadius.xaml**（23KB）— 含 CreateCurve
- **ShoulderRoundedTarget.xaml**（19KB）— 含目标曲面
- **AIShoulder.xaml**（6.7KB，2026-08-17）— **AI 从需求现场生成的样例**（水平段+放坡，无 ViewState 噪音，最干净的精简写法），生成新部件时以此风格为基准
- 复杂参考：PermeablePavement / CaltransB3RetainingWall / RailDoubleTrackCANT
- **端到端回归**（开发仓脚本，不随分发包）：`server/test-sac-e2e.js`（模板链路）+ `server/test-ai-subassembly.js`（AI 自主生成链路），需 C3D+relay 在线
