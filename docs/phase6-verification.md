# Phase 6 verification — Validator + rollback

2026-10-04（Asia/Taipei）。**PHASE 6 COMPLETE**。34 项 Validator/恢复数据层测试和 76 项 planner 回归测试全部通过；实机共有 23 项通过检查，首轮 R3 的浮点比较断言经针对性复测已解决，无未解决失败。

范围严格对应 PRD 第 20–22 节及第 29 节 Phase 6。实现限定 canonical 模型的确定性 Validator 和 R1–R5 局部恢复，保持“semantic target → persistent Feature → dimension binding → direct mutation → rebuild → validate → atomic CADState commit”的事务顺序。

## Validator

`CadValidator` 使用原 Phase 4 `EditConfirmation` 的真实 API 测量，不使用 LLM、图像或人工判断。数据解释部分可独立测试。每项检查返回 `Name / Expected / Actual / Status / FailureClass`，整体返回 `Success`、`FailureClass`、`ValidationMs`、真实 measurements 和引用审计。

检查覆盖：四个主 Feature 和两个 profile 的存在/类型/身份、原生父依赖、特征历史顺序、CADState dependency graph、六个尺寸绑定的 owner/local name/value、rebuild status、Feature errors、body count、精确 bounding box、厚度、孔数量/直径/对称/贯穿/间距、pattern count/spacing/direction、圆角参数与四处实际圆角面、语义引用的可恢复性与几何意义、圆心固定关系，以及 state/model 一致性。

`preceding_body_feature` 检查 native Feature 历史顺序；host / pattern seed / edge owner 检查原生 parent ancestry。圆角在阵列之后，并不意味着 SOLIDWORKS 的直接 parent 列表一定包含阵列，不能把两者混同。

仅覆盖既有 canonical XY/+Z 板件；没有新增 Chamfer、Blind Hole 或其他 CAD Feature。PRD 中 chamfer distance / blind depth 的检查属于相应功能已经支持时才适用，本阶段不扩展 vocabulary。

## 有限恢复和提交边界

| 类别 | 实现 | 有界行为 |
|---|---|---|
| R1 stale semantic entity reference | `LocalRecovery` + `CanonicalEntityResolver` | 原生引用先行；仅固定 Face/Edge 语义可按 geometry/owner 唯一恢复 |
| R2 parameter not applied | fresh `EditContext.Dimension` + `SetSystemValue3` | 最多一次重设，再 rebuild/validate；失败 rollback |
| R3 rebuild failure | 原参数及必要圆心恢复 | rebuild/validate 后报告失败，磁盘状态不提交 |
| R4 impossible fillet | `GEOMETRY_IMPOSSIBLE` | 恢复原半径，不尝试更小候选，不删除重建模型 |
| R5 unsupported / ambiguous intent | 既有 RulePlanner / mandatory schema boundary + failure classification | 不进入 Runtime，不新增 multi-edit 或 tool sequence |

R1 支持顶面/底面、两个孔壁、四个圆角面、最终阵列方向边和八条圆角切向边。检查 planar normal、Z、面积、cylinder radius/axis、owner Feature、顶点位置及完整唯一候选数。不恢复 Feature / Sketch / SketchPoint 的身份；这些引用失效时明确拒绝。有效但语义错误的引用也被拒绝，不把“API 能恢复”当成“语义正确”。

编辑中的引用修复仅先更改内存工作状态；完整参数/几何/语义验证通过才与参数一并原子提交。保留原引用的 `Before / AfterAttempt`，单独输出 `ReferencesAfterRecovery`，不会掩盖旧引用失效。

`recover-state` 只修复磁盘引用，不修改 CAD 几何；先确认实际模型参数和结构正确。完整验证通过才提交，revision 增加一次。无失效引用且验证通过时不写文件。失败、候选不唯一、Feature 身份丢失时不提交。

canonical 创建在 CADState 保存之前执行 Validator。若创建或持久化失败，只删除本次在已经确认空的 Part 中新建的 canonical 特征；保留模板/先前 Feature，报告 `creationRollback` 和 `partialModelMayRemain`。旧 Phase 1 `create-plate` 的部分失败语义未扩大为通用事务。未实现崩溃恢复。

## CLI 与证据

```powershell
.\bin\CadHarness.Cli.exe validate <state-file>
.\bin\CadHarness.Cli.exe recover-state <state-file>
.\scripts\test-validation.ps1
.\scripts\test-planner.ps1
.\scripts\test-phase6-live.ps1 -PartTemplate 'C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2024\templates\gb_part.prtdot'
```

`validate` 不修改模型或 CADState、不 rebuild；仅写 verification log。`recover-state` 使用与 edit 相同的 state-path writer lock、前后 hash guard 和原子文件替换。每次输出及 `artifacts/validation/<run-id>.json` 可审查实际结果；编辑保留 `artifacts/edits/<edit-id>.json`。

本机默认 Part 模板未配置；实机测试明确传入 Phase 2 已验证的模板路径，使用 `NewDocument` direct API，不修改全局模板设置。首次未提供模板的预检未创建任何 Part，原 Phase 4/5 模型及状态未变化：[模板预检记录](../artifacts/phase6/template-preflight-summary.json)。

## 验证范围说明

自动测试基于先前只读探针记录的真实 measurements，逐项注入 dimension / geometry / feature type / identity / dependency / reference / binding 错误，并验证有界恢复和失败分类。它们是数据层测试，不替代实机 API 验证。

实机测试使用独立 Phase 6 Part。R1 通过受控修改其磁盘 Face/Edge 引用字节制造失效，再调用真实 SOLIDWORKS persistent-reference API 和几何 resolver 恢复；不声称已证明任意自然拓扑重生成后的恢复。R2 通过仅存在于测试程序集的 dropped-write adapter 模拟第一次参数未应用；第二次写入、rebuild 和 Validator 均调用真实 API，生产 CLI 没有故障注入开关。R3/R4 使用真正超出当前几何的 spacing / fillet 请求，不模拟 API 错误。

实机使用独立 Part，GUID=`0069028a-0dbc-4801-a325-509800e22308`，状态位于 `artifacts/phase6/live/model-state.json`。所有 API 都通过 x64 C# out-of-process controller 执行；生产构建和测试构建均使用 warnings-as-errors。没有真实 LLM 新请求，Phase 5 planner 的已验证 schema、请求与规划语义保持不变。

| 实机检查 | 真实结果 / 证据 |
|---|---|
| canonical 创建 | 保存 CADState 前 Validator 通过；revision=0、24 个引用 |
| 四种标准 edit | spacing 40→50、thickness 10→12、Ø8→Ø10、R3→R4 均成功，revision 0→4 |
| stale refs 的只读 validate | 7/24 有效、17/24 失效；明确拒绝，状态 hash 与模型不变 |
| R1 独立恢复 | 七个实体组、17 个 Face/Edge 引用全部恢复；revision 4→5，原子提交；重复恢复 hash/revision 不变 |
| R1 与成功 edit 合并提交 | 两个失效引用 + spacing 50→55；原引用审计仍显示 2 个失效，修复后 24/24；revision 5→6 |
| R2 第一次写入未应用 | 一次 fresh dimension 重设成功，spacing 55→60；24/24 引用有效，revision 6→7 |
| R2 两次写入均未应用 | 不再重试，rollback 成功；原 R4 与 CADState hash 不变 |
| R3 spacing=200 | `EditRebuild3=false`、`FEATURE_REBUILD_FAILED`；原孔距和圆心恢复，Validator 通过、状态 hash 不变 |
| R4 radius=100 | `GEOMETRY_IMPOSSIBLE`、rollback 成功；原半径恢复，Validator 通过、状态 hash 不变 |
| R5 turbine blade 指令 | RulePlanner 拒绝，`UNSUPPORTED_INTENT`、`RuntimeEntered=false`，无模型或状态修改 |
| Feature 身份丢失 | 拒绝恢复，不按名字搜索、不提交 |
| 可恢复但语义错误的 top-face 引用 | 顶面引用指向底面，被 `SEMANTIC_REFERENCE_MISMATCH` 拒绝，不提交 |
| 错误 dimension owner | 在 mutation 前拒绝，模型及状态不变 |
| 创建持久化失败 | 用另一文档的状态路径触发 ownership 拒绝；仅本次新 Feature 被删除，新 Part 恢复空模型且无新 GUID，原状态不变 |
| Feature 显示名称全部变化 | 四个 Feature 改名后 edit/resolve 仍成功；revision 7→8 |
| R1 修复后再遇 R4 失败 | 未提交新引用；磁盘 hash 不变，原引用 rollback 审计仍显示 1 个失效，内存修复后 24 个可恢复 |
| SketchPoint 身份丢失 | `STALE_REFERENCE` 拒绝，不猜测、不提交 |
| R2 第一次写入返回失败状态 | 一次 fresh dimension 写入成功，thickness 12→14；revision 8→9 |
| RulePlanner → Runtime 的 R4 失败 | Runtime 被进入并成功 rollback；顶层和 Runtime 的 failure class 均为 `GEOMETRY_IMPOSSIBLE`，状态 hash 不变 |

首轮 19 项实机检查中，18 项通过，R3 的测试断言误报一次失败。原始 evidence 本身已经记录 `RollbackSucceeded=true`、`ValidationRollback.Success=true`、state hash 不变；断言却将真实几何回读做精确 JSON 比较，孔半径 `5` 与 `5.000000000000004 mm` 被当作不同。测试比较改用既有 canonical `<0.0001 mm` 容差，**未改变 Runtime 的验证容差**。四项针对性复测全部通过，并另行验证了一次 planner→Runtime 的 R4 分类传播，共 24 次检查执行、23 项通过、无未解决失败。首轮失败记录完整保留，未覆盖或删除。

最终测试状态为 **80×50×14 mm、两个 Ø10 Through All 孔、spacing=60 mm、R4**，revision=9；孔中心约为 (-30,0)、(30,0) mm，body count=1，bbox=`[-40,-25,0,40,25,14] mm`，`NeedsRebuild=0`，所有 Feature errors=0。最终只读 Validator 的 **123 项检查通过**；新进程恢复 **24/24 persistent references、6/6 dimension bindings**。一次最终实测 validation time 为 **5116.877 ms**，仅为本机一次观测；本阶段未做性能优化或 benchmark。

原 Phase 4 `零件2` 与 Phase 5 `零件4` 均未编辑。前后真实参数/几何一致，两个状态文件 SHA256 分别保持：

```text
Phase 4: 430F65489CA502DB8FA8FADE51B25768DE6597563CB665A48F0B9D808BBA3CE7
Phase 5: 635D63C20AF29AAA7FE4F539FEE656991536FDA27BE763D0D4B9615C00A4FDED
```

完成后恢复原 Phase 4 Part 为活动文档。Phase 6 测试 Part 留在 SOLIDWORKS 中，未保存为 `.SLDPRT`。带有 `bad-*`、`lost-*`、`stale-*` 或 `wrong-*` 名称的状态文件是故障测试 fixture，不是正常编辑用状态。

本阶段增加六个 runtime 文件：`ValidationResult.cs`、`CadValidator.cs`、`CanonicalEntityResolver.cs`、`LocalRecovery.cs`、`ValidationCli.cs`、`CanonicalCreationTransaction.cs`。五个既有文件有必要的接入改动：`Program.cs`（命令及创建 rollback）、`StateService.cs`（保存前验证）、`EditConfirmation.cs`（补读 GeometryPattern）、`ParameterEditRunner.cs`（验证/有限恢复与原子提交）、`PlannerCli.cs`（failure class 记录及传播）。其余既有 runtime/domain/planner 文件与 Phase 6 基线 hash 一致；特别是 LlmPlanner、schema、意图类型、CADState schema、StateSerializer 和 PlanRuntimeBridge 未改动。没有重构 Runtime 或新增 feature。

完整机器证据：[phase6-verification-evidence.json](../artifacts/phase6/phase6-verification-evidence.json)。测试记录：[34 项数据层测试](../artifacts/phase6/validation-test-results.json)、[76 项 planner 回归](../artifacts/phase5/planner-test-results.json)、[首轮实机汇总](../artifacts/phase6/live/summary.json)、[针对性复测](../artifacts/phase6/live/supplement-summary.json)、[planner→Runtime R4](../artifacts/phase6/live/r4-rule-runtime-verification.json)、[最终 Validator](../artifacts/phase6/live/supplement-final-cli-validation.json)、[最终重新解析](../artifacts/phase6/live/supplement-final-resolve.json)。

关键失败事务：[R2 耗尽重设](../artifacts/phase6/live/r2-exhausted.json)、[R3 首轮原始记录](../artifacts/phase6/live/r3-spacing-failure.json)、[R3 容差复核](../artifacts/phase6/live/r3-tolerance-recheck.json)、[R4](../artifacts/phase6/live/r4-fillet-failure.json)、[R1 修复不提交](../artifacts/phase6/live/r1-failed-edit-no-commit.json)、[创建回滚](../artifacts/phase6/live/create-ownership-failure.json)。逐条日志的路径和 SHA256 位于机器证据 `EvidenceManifest`。

本阶段未实现 Phase 7 `CommandInProgress`/树刷新/图形刷新批处理，也未实现 Phase 8 benchmark runner、任意 topology recovery、geometry planning、新 Feature、UI、multimodal 或 Jev。没有 SendKeys、鼠标、RunCommand 或人工确认。
