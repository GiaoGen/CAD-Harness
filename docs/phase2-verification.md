# Phase 2 verification

日期：2026-10-03（Asia/Taipei）。

本文件记录 Phase 2 验收时的实现与证据。后续 CADState 与引用持久化的实现及验证见 [Phase 3 verification](phase3-verification.md)。

## 结论与范围

Phase 2 在本机 SOLIDWORKS 内实际执行成功。硬编码、强类型 `CadIntent` 经 direct API 创建了完整 canonical part：80 × 50 × 10 mm 板件、一个 Ø8 Through All 孔种子、count=2 / spacing=40 mm 的线性阵列、四条外侧竖直边 R3 圆角。

一次性只读探针已核对实际特征、原生参数和最终几何。未实现 LLM、CADState、通用 Validator、UI 或 Phase 3；没有使用 SendKeys、鼠标、RunCommand 或需要人工确认的交互式建模命令。

## 环境与准备

- SOLIDWORKS revision：`32.0.1`。
- 当前验证文档：`零件2`，未保存；原 Phase 1 文档 `零件1` 保留。
- 使用现有 Framework x64 C# 编译器、安装目录下的 Interop 库构建。启用警告作为错误，编译通过。
- 使用已验证的提升权限执行环境连接正在运行的 SOLIDWORKS。
- 本机没有可用的默认 Part 模板设置。一次性准备程序明确将 `C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2024\templates\gb_part.prtdot` 传给 `ISldWorks.NewDocument`，新建测试零件。未打开模板选择界面，未修改默认模板设置。
- 空零件检查最初拒绝模板自带的 `InkMarkupFolder`。只读核对模板后补充允许 `InkMarkupFolder` 和 `ConfigTableFolder` 两种默认容器；该次拒绝没有修改模型。

## 实现

`src/CadHarness.Cli/CadIntent.cs` 定义一个内存中的固定强类型意图：

| 操作类型 | 硬编码参数 |
| --- | --- |
| `BasePlateIntent` | width=80、height=50、thickness=10 mm |
| `ThroughHoleSeedIntent` | diameter=8 mm，center=(-20, 0) mm |
| `LinearPatternIntent` | count=2（含种子）、spacing=40 mm，方向 +X |
| `OuterVerticalFilletIntent` | radius=3 mm |

`CanonicalExecutor` 顺序调用原生 API：

1. 复用 Phase 1 的中心矩形、原生宽高尺寸和 `FeatureExtrusion3` 创建板件。
2. 从板件当前实体中找到 Z=10 mm 的顶面，创建草图；通过 `ModelToSketchTransform` 将孔中心转换到草图坐标，创建圆，固定圆心并创建 Ø8 直径尺寸。
3. 调用 `FeatureCut4`，终止条件为 `swEndCondThroughAll`。
4. 选择板件的水平边作为方向对象，选择孔种子特征作为阵列对象，调用 `FeatureLinearPattern5`。保持 `GeometryPattern=false`。
5. 从当前实体中选取 X=±40、Y=±25、Z=0…10 mm 的四条直线边，调用 `FeatureFillet3` 创建均匀 R3 圆角。
6. 调用 `EditRebuild3` 并检查创建特征的原生错误码，完成后清空选择并刷新。

几何选择规则只用于当前 canonical 执行，不保存语义映射、不使用 persistent references，也没有引用失效恢复机制。

运行命令：

```powershell
.\scripts\build.ps1
.\bin\CadHarness.Cli.exe create-canonical
```

实际创建输出：

```json
{"success":true,"phase":2,"title":"零件2","features":["Extrude.Base","Hole.MountingSeed","Pattern.MountingHoles","Fillet.OuterVerticalEdges"],"widthMm":80,"heightMm":50,"thicknessMm":10,"holeDiameterMm":8,"holeCount":2,"holeSpacingMm":40,"filletRadiusMm":3,"saved":false,"execution":"direct-solidworks-api"}
```

此创建输出中的数值来自硬编码意图。以下验证结论来自独立只读探针读取的真实模型数据。

## Feature tree

不计模板默认容器，创建的原生特征为：

```text
Sketch.BaseProfile                ProfileFeature，完全定义
Extrude.Base                      Extrusion
└── Sketch.BaseProfile            被拉伸使用的草图
Sketch.HoleSeed                   ProfileFeature，完全定义
Hole.MountingSeed                 ICE（本机 Instant3D 下的切除特征）
└── Sketch.HoleSeed               被切除使用的草图
Pattern.MountingHoles             LPattern
Fillet.OuterVerticalEdges         Fillet
```

顶层 API 遍历也会列出被特征吸收的草图，上述缩进用于表示子特征引用，并非额外创建了同名草图。

本机 `GetTypeName2` 对孔种子返回 `ICE`。探针读取其 `IExtrudeFeatureData2`，确认前向终止条件为 `1 / swEndCondThroughAll`。实际孔壁分别属于 `Hole.MountingSeed` 和 `Pattern.MountingHoles`，孔没有以两个独立圆切除来代替线性阵列。

## 原生参数和几何核对

| 核对项 | 实际结果 | 判定 |
| --- | --- | --- |
| 基础草图宽度尺寸 | 80 mm | PASS |
| 基础草图高度尺寸 | 50 mm | PASS |
| 基础拉伸深度 | 10 mm | PASS |
| 两个草图的约束状态 | `3 / swFullyConstrained` | PASS |
| 孔种子终止条件 | `1 / swEndCondThroughAll` | PASS |
| 实际通孔数量 | 2 | PASS |
| 实际孔径 | 每孔约 7.999999999999998 mm，即 Ø8 | PASS |
| 实际孔轴中心 XY | (-20, 0)、(20, 0) mm | PASS |
| 实际孔中心间距 | 40 mm | PASS |
| 原生阵列 count | 2，包含种子 | PASS |
| 原生阵列 spacing | 40 mm | PASS |
| 原生阵列 GeometryPattern | false | PASS |
| 原生圆角类型 | `0 / Simple` | PASS |
| 原生圆角半径 | 3 mm | PASS |
| 实际 R3 圆角面 | 四个外侧角部圆柱面，半径均为 3 mm | PASS |
| Solid body count | 1 | PASS |
| All body count | 1 | PASS |
| 精确 bounding box | X=[-40,40]、Y=[-25,25]、Z=[0,10] mm | PASS |
| 精确 bounding box 尺寸 | 80 × 50 × 10 mm | PASS |
| 各创建特征错误码 | 0，warning=false | PASS |
| `EditRebuild3` | 返回成功 | PASS |
| `NeedsRebuild2` | 0 | PASS |
| 完成后的活动草图 | 无 | PASS |
| 尺寸输入提示设置 | 创建前 true，完成后 true，已恢复 | PASS |

数值比较按 0.000001 mm 的证据报告容差解释浮点舍入；生产程序没有引入此几何验证器。

通孔几何证据：两个 R4 圆柱面轴线沿 Z，每个孔壁从 Z=0 延伸至 Z=10 mm，且各有两个完整圆周边界。单孔孔壁面积约 251.32741228718336 mm²，符合 Ø8、厚度 10 mm 的贯穿圆柱面。实际孔距由两条孔轴的 XY 坐标独立计算。

圆角几何证据：四个 R3 圆柱面轴线分别位于 XY=(±37, ±22) mm，均从 Z=0 延伸至 Z=10 mm，归属 `Fillet.OuterVerticalEdges`；每个面面积约 47.1238898038469 mm²，对应四个外侧角部的四分之一圆柱面。

Bounding box 同时读取了 `GetBodyBox` 的近似值，并通过六个方向的 `IBody2.GetExtremePoint` 获取精确范围；两者尺寸一致。

## 只读探针与保护行为

一次性探针只调用查询 API，不调用 `EditRebuild3`、`AccessSelections`、`SaveAs` 或任何建模 API。为读取本机 `ICE` 切除类型，补充探针类型处理后再次只读采集最终证据。准备程序和探针源码及可执行文件已移除，未纳入 CLI。

完整最终采集结果保存在 [phase2-probe-evidence.json](phase2-probe-evidence.json)。它是本次检查输出，不是 CADState；生产程序不读取或更新它。

再次对 `零件2` 执行 `create-canonical`，返回 `PART_NOT_EMPTY`、退出码 1、`partialModelMayRemain=false`，未重复创建或修改模型。

## 验证边界

本次验证针对 SOLIDWORKS 32.0.1、标准 XY / +Z 零件和上述固定参数。未验证其他版本、任意模板朝向或其他参数组合。孔种子中心使用固定点约束；后续编辑路径尚未实现。

API 失败时仍按现有 Phase 1 策略返回结构化错误、恢复临时设置并报告可能存在的部分模型。没有自动回滚、CADState、通用 Validator 或 Phase 3 持久化。本次成功模型保留在当前未保存的 `零件2` 文档中。
