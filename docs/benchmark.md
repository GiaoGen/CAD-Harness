# Phase 8 benchmark runner

对应 PRD 第 23–26、29 节。执行固定 A–E 案例，写入 JSON；不增加几何生成能力、LLM 协议、tool calling、UI 或 baseline agent。

```powershell
.\bin\CadHarness.Cli.exe benchmark .\tests\benchmark-cases\mvp.json
```

可提供独立输出根目录：

```powershell
.\bin\CadHarness.Cli.exe benchmark .\tests\benchmark-cases\mvp.json .\artifacts\my-benchmarks
```

每次生成新的 GUID run 目录，包含 `results.json`、每个 iteration/case JSON 和独立 CADState。命令 stdout 为最终 suite JSON。程序用 direct `NewDocument` 新建测试 Parts，保留模型以供检查，结束后恢复原活动文档与更新设置；不修改原 Part、不自动保存 `.SLDPRT`。不同 case、iteration、warm-up 都有独立 Part 和 state path。当前安装的 benchmark controllers 使用 session lock 串行执行。

## Manifest

`tests/benchmark-cases/mvp.json` 声明严格 `benchmarkVersion=0.1`、suiteId、planner、iterations、warmupIterations、显式 native Part template 和五个固定 case ID/scenario。未知字段、重复 key/case、缺失案例、未知 enum、非整数或超限次数在 COM/HTTP 前拒绝。支持 iterations=1..20、warmupIterations=0..3；warm-up 完整保留且不混入正式统计。

默认 `planner=rule`，便于复现。`planner=llm` 复用 Phase 5 `LlmPlanner` 和既有环境配置，不重试、不回退至 RulePlanner，不记录 API key。LLM 输出仍经过现有 schema/enum/numeric validation；benchmark 另核对返回 intent 是否匹配固定案例的已知语义。Case B/E 始终只规划，合法但语义错误的 intent 也不执行 Runtime，并记录 `SemanticSafetyFailure`。该 gate 是测试预期核对，不是新增 planner 能力。

## 固定案例与真实能力差距

| Case | 行为 | 通过条件 |
|---|---|---|
| A | canonical create；spacing=50、thickness=12、diameter=10、radius=4 | 原有 Feature IDs、真实几何、revision=4、24/24 refs |
| B | 请求 100×60×8、四个 Ø6 对称矩形排列孔 | 当前为 BLOCKED_CAPABILITY；安全拒绝不算该案例建模通过 |
| C | canonical create；请求 R100 | GEOMETRY_IMPOSSIBLE、真实 rollback、原 SHA256/revision/几何保留、24/24 refs |
| D | canonical create；thickness=12；恢复重要 top face | 实测旧引用恢复状态和 Face Z=10→12；单独受控失效一个磁盘 Face 引用，复用 R1 恢复、revision=2、24/24 refs |
| E | turbine blade / cooling passages | UNSUPPORTED_INTENT、RuntimeEntered=false、零 body、原空 Part feature count、无 CADState |

Case B 原规格还要求 thickness=10、diameter=8 后续编辑。当前创建 intent 只有 canonical preset，状态/验证只支持两个孔和固定 canonical feature 结构，无法真实创建或验证该四孔板，因此不能执行其后续编辑。PRD 明确要求 “Do not over-expand feature support solely for this case.” 本阶段只实现 runner，不把四孔模型伪装成两孔 canonical、不删除或重新生成现有模型、不把 Case B 当作 skipped/pass。

Case D 的厚度变化实际重建并移动 top face；persistent references 可能全部保持有效。记录原引用的实际失效数量，不声称一定自然失效。后续 fallback probe 明确 `ControlledReferenceInvalidation=true`，只改本次测试 state 的一个 Face ref；真实的 `recover-state` / persistent reference API / geometry resolver 执行恢复。probe 独立计时，不能代替自然引用恢复证据。

## 指标的统计边界

每条命令记录 runId、command、typed intent、provider/model、真实 LLM HTTP attempts、planner latency、tokens、parse failure、unsupported intent、RuntimeEntered、exit/result、state SHA256、revision、rollback 和 total wall-clock。无 token usage 用 null；RulePlanner 的 LLM calls/wall 是零，tokens 是 null。

`WallMs` 覆盖读取 state → planning → verified Runtime → state audit；`RuntimeWallMs` 覆盖既有 direct API Runtime、验证、日志/原子提交和结果处理。Case wall 另包含 NewDocument、设置检查、额外验收；suite wall 包含全部 warm-up/iteration 和输出处理。最终 summary 写盘/CLI 输出传输在最后的 stopwatch stop 后执行，不冒充用户端 transport latency。

`CompileMs=0` 表示预编译 Runtime，无按命令生成/编译 C#。`ValidationMs/ValidationCalls` 是实际 `CadValidator.Read` 计时总和，包括 baseline、post-edit、retry、rollback 的调用；case 的额外验收单独记录 `AuditValidationMs`。

`DirectSolidWorksApiOperations` 统计已插桩的建模、尺寸设置、sketch relation、显式 rebuild 和最终 refresh/redraw 调用，逐 API 记录 calls、exceptions、wall time。**它不包含 selection、属性读写、元数据、验证查询或 native 内部隐式操作，不是全部 COM round trips。** 每条 Runtime 结果都给出 `ApiCountCoverage`。插桩只观察原调用的结果/异常，不重试、不调整参数、不改变依赖顺序。`SolidWorksExecutionMs` 是这些同步 direct API 调用区段的 wall 总和，包含 COM 往返；外部进程不能读取 SOLIDWORKS 内部纯执行时间。

`Rebuilds/RebuildMs` 由统一的显式 rebuild 边界实际观测，也计入 retry/rollback；不统计 API 内部的隐式重建。`RecoveryAttempts` 保留既有 evidence 的真实 attempts，Case D 的 recovery probe 单独给出并计入 summary。

summary 的 TotalSuccess=正式通过案例/全部正式必需案例，B 阻塞仍保留在分母。EditSuccess=真实成功 edits/实际执行 edits，包括预期失败的 C；case 通过不能把失败 edit 改成 success。RollbackSuccess=成功 rollback/实际 rollback。无对应尝试使用 null。

Exit codes：0=全部必需案例通过；3=runner 完成但存在 capability blocked；1=失败/恢复/写盘错误；2=usage 或 manifest 错误。已完成的每个 case 原子写盘，并刷新 progress `results.json`；中断后可检查已有证据，不提供 crash resume。

验证记录见 [Phase 8 verification](phase8-verification.md)。
