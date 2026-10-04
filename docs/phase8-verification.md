# Phase 8 verification

对应 `CAD_Harness_MVP_PRD.md` 第 23–26 节和 Phase 8 实施顺序。新增 CLI benchmark runner、严格 manifest、固定 A–E 案例、独立模型/状态、观测指标、逐案例及 suite JSON；完整接口和统计边界见 [benchmark.md](benchmark.md)。

**完整 Phase 8 的唯一能力阻塞是 PRD Case B。** 现有 typed create intent 仅支持 canonical；CADState/Validator 仅支持两个孔和 canonical features。无法真实创建并编辑 100×60×8、四个 Ø6 矩形对称孔的模型。PRD 同时要求 “Do not over-expand feature support solely for this case.” 本次没有为 benchmark 新增几何支持，不把 B 的安全拒绝算作案例通过。

## 验证范围

- 新增 23 项 benchmark 测试：strict manifest、未知字段/枚举/重复键、次数边界、语义不匹配时不执行 Runtime、unsupported gate、runtime exception、API telemetry 异常/嵌套、warm-up 分离、blocked/negative case 分母、原子结果替换。
- 既有 34 项 Validator/recovery、76 项 planner、17 项 batch scope 回归通过，共 150 项单元/回归测试。
- 实机通过独立 child process 调用生产 `CadHarness.Cli.exe benchmark`，将默认 manifest 的 iterations 改为 2。十个 case 均创建独立空 Part；A/C/D 分别保存不同 GUID 和 CADState，不复用用户模型。记录同一 manifest、exe SHA256，固定 run 下的原始结果和每次 command 证据均保留。
- default planner 是真实 RulePlanner + 真实 SOLIDWORKS API；该次不执行 LLM HTTP。LlmPlanner 选项复用现有实现；单元测试的 FixedPlanner 仅用于安全 gate 测试，不作为实机模型或 LLM 推理证据。
- Case D 检查编辑前原 refs 的实际状态、top face Z=10→12；另一个显式标注的受控磁盘 Face 引用失效 probe 验证真实 R1 fallback，不能宣称自然编辑必然导致引用失效。
- 最后只读复核 Phase 4、5、6、7 保护模型的 native geometry/parameters/ref 状态和 CADState SHA256，恢复原活动 Part。

结论：**PHASE 8 BLOCKED**。Benchmark runner 实现与验收通过；PRD 的全部 required cases 尚未通过，唯一能力缺口是 Case B。

生产 CLI 完整运行两轮：10 个 required cases，8 passed、2 blocked、0 failed；child exit=3，RunnerCompleted=true，AllRequiredCasesPassed=false。

| Case | 第 1 轮 | 第 2 轮 |
|---|---|---|
| A | PASSED | PASSED |
| B | BLOCKED_CAPABILITY | BLOCKED_CAPABILITY |
| C | PASSED | PASSED |
| D | PASSED | PASSED |
| E | PASSED | PASSED |

TotalSuccess=8/10=80%；EditSuccess=10/12=83.33%（含两个预期失败 R100 edit）；RollbackSuccess=2/2=100%。ParseFailures=0、SemanticSafetyFailures=0；RulePlanner 的真实 LLM HTTP calls=0、LLM wall=0、tokens=null，未宣称本轮执行了真实 LLM 推理。

总 suite wall=411.64 s；显式 rebuild=20；已插桩 direct API operations=182；recovery attempts=4。完整逐命令 wall/planner/runtime/API/rebuild/validation、原始 Runtime 结果和状态 SHA/revision 见汇总及 raw results JSON。

两次 Case D：自然编辑后原 persistent refs 失效数量分别为 0, 0；受控 probe 均使单个 Face ref 失效，并由真实 R1 恢复为正确 top face，revision 1→2、24/24 refs 有效。

Phase 4–7 原模型的 before/after native measurement 一致，CADState SHA256 均不变；最后恢复原活动 Part。

- `artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json`: `430F65489CA502DB8FA8FADE51B25768DE6597563CB665A48F0B9D808BBA3CE7`

- `artifacts/phase5/live-model-state.json`: `635D63C20AF29AAA7FE4F539FEE656991536FDA27BE763D0D4B9615C00A4FDED`

- `artifacts/phase6/live/model-state.json`: `8982412D055B8FFD01455FAEBF2FBDEE0C2A5EFA066AE7501C7718697CBAE5EF`

- `artifacts/phase7/live/3-optimized-state.json`: `6832E80FB5AF6C900207970ACC89EB560ED89A83B98306E48518454906757A09`

汇总：[phase8-verification-evidence.json](../artifacts/phase8/phase8-verification-evidence.json)；生产 raw suite：`D:\CAD-Harness\artifacts\phase8\live\runs\18341e4b-0c7a-4309-b8dc-fe5ac609b106\results.json`；保护与 runner 验收：[verification.json](../artifacts/phase8/live/verification.json)。

## 源码与运行边界

新增 `BenchmarkDefinition`、`BenchmarkResult`、`BenchmarkStep`、`BenchmarkRunner`、`BenchmarkCli`、`BenchmarkTelemetry`。既有 `Program` 仅新增 benchmark 路由以及观测 wrapper；`CanonicalExecutor`、`EditContext`、`SolidWorksBatchScope`、`CanonicalCreationTransaction` 仅给原有 API invocation 加计数/计时 wrapper；`CadValidator.Read` 增加返回前的观测 hook。未修改 intent/planner 协议、CADState schema、parameter edit transaction、rollback、StateSerializer 或 semantic resolver。

wrapper 原样返回结果并传播原异常，不做 retry/fallback、不改变值、选择、建模顺序、重建边界或提交条件。API counters 的覆盖边界出现在每条 Runtime metrics 和 benchmark.md；不宣称测量了所有 COM property/selection/query 调用。

CLI 0 表示所有 required cases 通过，3 表示执行完成但存在 capability blocked。此次验收预期 child exit=3；测试程序的 success=true 仅表示 runner/保护/指标行为通过，不表示 A–E 全部通过。未实现 Case B 的后续 thickness=10、diameter=8，因为其初始模型无法建立；明确保留为未满足项。

未实现 naive iterative agent baseline、multimodal、Jev、tool calling、新 CAD Feature、通用几何规划、UI automation、crash resume 或任意 topology recovery。没有 SendKeys、鼠标、RunCommand 或人工确认路径。

复核命令：

```powershell
.\scripts\build.ps1 -SolidWorksApiDirectory 'D:\Solidworks Crops\SOLIDWORKS\api\redist'
.\scripts\test-benchmark.ps1
.\scripts\test-validation.ps1
.\scripts\test-planner.ps1
.\scripts\test-batch.ps1
.\scripts\test-benchmark.ps1 -Live -BuildOnly
.\bin\CadHarness.Cli.exe benchmark .\tests\benchmark-cases\mvp.json
```

一般 benchmark 可重复执行，自动使用新的 GUID run 目录；当前 Phase 8 live 验收 helper 拒绝覆盖已有 `artifacts/phase8/live`，以保留此轮证据。
