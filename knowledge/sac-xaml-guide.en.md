# SAC XAML Subassembly Syntax Guide (for AI generation)

> Compiled 2026-08-16 by reverse-engineering the 73 .pkt templates shipped with C3D.
> Purpose: XAML reference for `buildSACSubassembly {xamlContent}`. The AI generates SAC workflow XAML following this syntax.

## 1. XAML skeleton (fixed structure)

```xml
<Activity mc:Ignorable="sads sap" x:Class="Subassembly"
 this:Subassembly.Side="[new EnumType(0, &quot;Right&quot;, &quot;Side&quot;)]"
 this:Subassembly.ParamName="defaultValue"
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
    <!-- 3 fixed members -->
    <x:Property Name="Geometry" Type="InOutArgument(asw:Geometry)" />
    <x:Property Name="SubassemblyErrorCenter" Type="InOutArgument(asw:SubassemblyErrorCenter)" />
    <x:Property Name="SubassemblyRunMode" Type="InOutArgument(asw:SubassemblyRunMode)" />
    <!-- user params: InArgument(x:Double) number / InArgument(asw:EnumType) enum / InArgument(asw:Slope) slope -->
    <x:Property Name="DitchDepth" Type="InArgument(x:Double)">
      <x:Property.Attributes>
        <asw:EnabledFlag2Attribute EnabledFlag="True" />
        <asw:DisplayName2Attribute DisplayName="Ditch Depth" />
        <asw:Description2Attribute Description="description" />
      </x:Property.Attributes>
    </x:Property>
  </x:Members>
  <Flowchart sap:VirtualizedContainerService.HintSize="614,636">
    <Flowchart.Variables>
      <!-- internal variables (enum constant definitions) -->
      <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(1, &quot;Left&quot;)]" Modifiers="ReadOnly" Name="Left" />
      <Variable x:TypeArguments="x:Double" Default="1" Name="InternalNum" />
    </Flowchart.Variables>
    <Flowchart.StartNode>
      <FlowStep x:Name="__ReferenceID0">
        <!-- activities go here -->
      </FlowStep>
    </Flowchart.StartNode>
    <!-- subsequent FlowSteps are chained via .Next -->
  </Flowchart>
</Activity>
```

## 2. Namespace shorthand
- `asa1:` = SAC activity library (CreatePoint/CreateLink/CreateShape, etc.)
- `asw:` = engine types (Geometry/EnumType/Slope/DisplayName2Attribute)
- Standard activities (FlowStep/FlowDecision/Sequence/Variable) have no prefix

## 3. Core activity list (stats across 73 templates, by frequency)

### 1. CreatePoint (1027×) — create a point
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
**Positioning modes**: `DeltaXAndDeltaY` (relative offset, Arguments = DeltaX1/DeltaY1), `DeltaXAndSlope` (DeltaX1+Slope1), `AngleAndDistance`, `FromSurface` (attach to ground), `FromPoint` (reference point + offset)

### 2. CreateLink (761×) — connect points
```xml
<asa1:CreateLink ActivityId="2" ApplyAOR="False" DisplayName="L1" EndPoint="P2" Geometry="[Geometry]" IsEnabled="True" LinkNumber="L1" ShowErrors="True" StartPoint="P1" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
  <asa1:CreateLink.LinkCodes>
    <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
      <InArgument x:TypeArguments="x:String">Top</InArgument>
    </scg:List>
  </asa1:CreateLink.LinkCodes>
</asa1:CreateLink>
```
**Real attributes** (corrected by measurement 2026-09-09, verified in PermeablePavement.xaml): `StartPoint` / `EndPoint` (reference an existing PointNumber) + **`LinkNumber` (= the link name, referenced by CreateShape)**; LinkCodes goes in a child element. ⚠️ The old style `LinkType="NextPoint" Point1= Point2=` is **not recognised by C3D and produces no geometry** (a pitfall hit while rewriting createCustomSubassembly). For non-two-point links, use CreatePoint's `AutoLink="True" AutoLinkGeometryName="Lx"` (auto-link while creating the point; EvenSlopeDitch uses this throughout); for complex LinkTypes (FromSurface/SlopeToSurface) consult the templates.

### 3. CreateAuxPoint (403×) — auxiliary point
Like CreatePoint but with "Aux" prefixed to `PointNumber`; does not take part in the final geometry

### 4. InternalVariableDefine (333×) — define internal variables
```xml
<asa1:InternalVariableDefine ActivityId="13" DisplayName="IsMetric &lt;Yes\No&gt;"
 ShowErrors="True" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"
 VariableName="IsMetric" VariableType="Yes\No">
  <asa1:InternalVariableDefine.DefaultValue>
    <InArgument x:TypeArguments="asw:EnumType">[No]</InArgument>
  </asa1:InternalVariableDefine.DefaultValue>
</asa1:InternalVariableDefine>
```
**VariableType**: `String` / `Double` / `Yes\No` (enum) / `EnumType`

### 5. CreateShape (141×) — closed shape
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
**Real attributes** (corrected by measurement 2026-09-09): `Links="L1,L2,..."` (comma-separated link names, matching CreateLink's LinkNumber) + `ShapeNumber` + child elements `ComponentNames` (list the link names line by line) / `ShapeCodes`. ⚠️ The old style `ShapeType="Closed"` + an `<asa1:CreateShape.Links><InArgument>...</InArgument>` child list is **not recognised by C3D** (the generator can't resolve the links, so the shape doesn't appear).

**⚠️ All FlowSteps must be registered at the tail of the Flowchart** (measured 2026-09-09): after `</Flowchart.StartNode>`, every `__ReferenceIDn` needs a `<x:Reference>__ReferenceIDn</x:Reference>` line; if missing, geometry is not produced (a pitfall hit while rewriting the buildSACSubassembly→createCustomSubassembly path — AIShoulder.xaml running without x:Reference is a special case; **always add them as insurance when AI-generating new parts**).

### 6. AuxIntersection (127×) / LinkIntersection (47×) — intersections
Produce an auxiliary point at the intersection of two lines/surfaces

### 7. SetVariableValue (75×) — assign a variable
```xml
<asa1:SetVariableValue ActivityId="10" DisplayName="Set variable" ShowErrors="True"
 SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"
 VariableName="MyVar" VariableValue="[expression]" />
```

### 8. CreateCurve (11×) — curve / FilletArcActivity (1×) — fillet

## 4. Flow control

### FlowStep (1500×) — sequential step
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

### FlowDecision (310×) — conditional branch
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
Common conditions: `[FillOrCut = "Fill"]` / `[Side = "Right"]` / `[IsMetric = "Yes"]` / `[numeric comparison]`

### Sequence (388×) — grouping
```xml
<Sequence DisplayName="Codes">
  <asa1:InternalVariableDefine ... />
</Sequence>
```

## 5. Common expression syntax (VB style, wrapped in `[ ]`)
- Reference a parameter: `[DitchDepth]` / `[Side]`
- Arithmetic: `[DitchWidth / 2]`, `[0.125 + 0.06]`
- Slope: `[new Slope(0.5)]` (0.5 means 1:0.5? measured as a ratio), `[Slopes]`
- Enum: `[new EnumType(1, "Left")]` (1 = Left, 0 = Right)
- String comparison: `[FillOrCut = "Fill"]`
- Conditional: `[If(cond, trueVal, falseVal)]`, `[cond AndAlso cond2]`

## 6. Generation steps (AI workflow)
1. Understand the requirement → decide parameters (x:Members) and geometry logic
2. Write the XAML skeleton (copy the template above, change param names/defaults)
3. Add FlowSteps in logical order: InternalVariableDefine (constants) → CreatePoint (key points) → CreateLink (connections) → CreateShape (closure)
4. Branching → FlowDecision; attach to ground → CreatePoint Positioning="FromSurface"
5. Call buildSACSubassembly to package → importSACSubassembly to import → iterate on errors if it fails

## 7. Template files (knowledge/sac-templates/, for reference)
- **EvenSlopeDitch.xaml** (16KB) — simplest, includes FromSurface/conditional branch; best starting point
- **CurbWallRadius.xaml** (23KB) — includes CreateCurve
- **ShoulderRoundedTarget.xaml** (19KB) — includes a target surface
- **AIShoulder.xaml** (6.7KB, 2026-08-17) — **an AI-generated-on-the-spot sample** (level segment + daylight, no ViewState noise, the cleanest minimal style); use its style as the baseline when generating new parts
- Complex references: PermeablePavement / CaltransB3RetainingWall / RailDoubleTrackCANT
- **End-to-end regression** (dev-repo scripts, not shipped): `server/test-sac-e2e.js` (template path) + `server/test-ai-subassembly.js` (AI self-generation path); require C3D + relay online
