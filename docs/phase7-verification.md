# Phase 7 verification — batch execution optimization

范围对应 `CAD_Harness_MVP_PRD.md` 第 4、29、31 节。基于已验证的 Phase 6，仅增加 direct SOLIDWORKS API batch scope 和固定验收测量；不实现 Phase 8 benchmark runner。

## 实现边界

- `SolidWorksBatchScope` 保存真实原值，暂设 `ISldWorks.CommandInProgress=true`，关闭 FeatureManager tree/window 更新和当前 model-view graphics 更新。
- 部分初始化失败、正常结束、body 抛错和 rollback 都执行清理；逐项尝试恢复，单项失败不阻止其余项。恢复错误显式报告，成功状态提交前完成恢复。嵌套 scope 保留外层 batch 状态。
- 按调用方原始设置，结束后最多一次显式 `UpdateFeatureTree` 和一次 `GraphicsRedraw2`。原先关闭的更新保持关闭。
- 孔圆 primitive 暂用 `AddToDB=true` / `DisplayWhenAdded=false`，在固定圆心和添加直径尺寸前恢复。矩形仍使用原生关系推断，编辑仍执行原有 fixed-center relation 操作，避免改变约束语义。
- canonical 创建不再单独对 base plate 显式 rebuild；`FeatureExtrusion3` 返回的真实 body 支持下一步的 Face/Edge 查询。完整 canonical model 后保留一次显式 rebuild。`InsertSketch(true)` 与 feature API 的内部求解不变。
- 正常 edit 保留一次 rebuild；R2 retry、R3/R4 rollback 的 rebuild 与原值恢复、真实几何校验、引用审计、原子提交均保留。
- scope 不执行自动 rebuild。必要重建位于既有事务边界，并在提交前读取真实 SOLIDWORKS 参数和几何。

生产改动：新增 `SolidWorksBatchScope.cs`；修改 `Program.cs`、`CanonicalExecutor.cs`、`ParameterEditRunner.cs`、`CanonicalCreationTransaction.cs`。不修改 planner、intent schema、CADState schema、Validator、persistent-reference resolver 或 StateSerializer，不新增 Feature 或交互执行路径。

## 测量方法

保留改动前的 Phase 6 CLI 二进制和源码 SHA256 基线：`artifacts/phase7/baseline/`。固定验收程序分别调用基线和优化版 CLI entry，均从独立 native Part 执行 canonical create，再执行 spacing=50、thickness=12、diameter=10、radius=4 的四种既有编辑。所有意图、模板、初始几何和校验契约相同。

一对 warm-up 不计入统计；三对正式样本交替执行顺序。wall-clock 覆盖 direct API、Validator、状态及 edit evidence 的磁盘写入、CLI JSON 序列化；不包括新建空 Part、额外只读验收与测试证据写盘。单机小样本仅说明本次测试结果。

edit `RebuildMs` 是原有 Runtime 计时；优化后的 `Batch` 另记录 scope 内显式 rebuild 次数/时间和最终显式 refresh/redraw。基线显式创建 rebuild 次数=2 来自已核对的源码调用点，优化版次数=1 来自 scope instrumentation。两者均不表示 SOLIDWORKS 全部 native rebuild 事件；没有统计 feature API、sketch exit、自动 tree/graphics 更新的内部事件。基线创建的 rebuild 耗时未单独插桩，不虚构该数据。

若 persistence 在 scope 已恢复后失败，后续 rollback 仍执行真实 rebuild，但不计入该已结束 scope 的计数。edit 的原有 `RebuildMs` 包括本次尝试、retry 和 rollback 的显式重建计时。创建时的 Capture/Validator 在 scope 恢复后执行，完整命令 wall-clock 仍包含这些成本。

结论：**PHASE 7 COMPLETE**。17 项 scope 单元测试、34 项 Validator/recovery 回归、76 项 planner 回归通过；实机 fixed acceptance、失败 rollback、设置恢复和最终新进程生产 CLI 验证通过。

| 操作 | 基线 wall 中位数 (s) | 优化 wall 中位数 (s) | wall 耗时下降 | 显式 rebuild |
|---|---:|---:|---:|---:|
| create | 28.50 | 23.62 | 17.1% | 2 → 1 |
| mounting_holes.spacing | 34.84 | 17.38 | 50.1% | 1 → 1 |
| base_plate.thickness | 40.20 | 16.97 | 57.8% | 1 → 1 |
| mounting_hole_seed.diameter | 25.41 | 15.13 | 40.5% | 1 → 1 |
| outer_edge_fillet.radius | 35.32 | 16.31 | 53.8% | 1 → 1 |

负的下降百分比表示该操作实测变慢。结果保留真实差异，不以 scope/rebuild 时间替代完整命令 wall-clock。各次原始 wall 时间、edit rebuild 中位数和完整 workflow 时间见汇总 JSON。

第三对创建的 wall 时间从基线 19.73 s 增至优化版 32.02 s；该变慢样本计入统计并保留，不能把中位数改善解释为每次调用都更快。

所有 8 次创建与 32 次正常 edit 均通过真实几何验证、revision 和 24/24 引用检查；12 项实机专项检查全部通过。最终生产 CLI 使用独立进程在已验证的 Phase 7 Part 上执行 radius=4 的既有单参数 edit，成功提交并再次验证 24/24 引用。

最终生产版本还通过新进程执行旧 Phase 1 `create-plate`：一次显式 rebuild、单 body、Native GetExtremePoint 确认 80×50×10 mm、NeedsRebuild=0；更新设置和尺寸输入 prompt 原值均恢复。实测 bounding box 与该次 wall-clock 记录在 final-production-smoke.json。

测量可执行文件之后仅补充 `Program` 外层无条件 `finally` 清理兜底；没有改变 batch 设置、重建边界或验证行为。最终源码重新编译并通过新进程生产 CLI 验证；两份二进制 hash 均保留。

保护 CADState SHA256（before = after）：

- `artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json`: `430F65489CA502DB8FA8FADE51B25768DE6597563CB665A48F0B9D808BBA3CE7`

- `artifacts/phase5/live-model-state.json`: `635D63C20AF29AAA7FE4F539FEE656991536FDA27BE763D0D4B9615C00A4FDED`

- `artifacts/phase6/live/model-state.json`: `8982412D055B8FFD01455FAEBF2FBDEE0C2A5EFA066AE7501C7718697CBAE5EF`

## 验收与保护

专项单元测试检查正常/异常/嵌套/已关闭更新状态，以及 getter 失败、setter 写入后抛错、恢复报错和最终 refresh 报错。既有 34 项 Validator/recovery 测试和 76 项 planner 回归保留。

实机验收检查每次创建和编辑后的 Feature errors、真实尺寸、两孔数量/直径/间距/中心对称、fillet 半径、body count、bounding box、NeedsRebuild 和 24 个 persistent references；逐次检查状态 revision 与所有 batch/sketch 设置的原值恢复。跨 Part 对照不比较 native ID 的数值，比较对应 semantic target 的几何与参数。

额外测试覆盖 native 嵌套 scope、异常清理、原先已关闭的更新设置、sketch setting 异常恢复、spacing=200 失败 rollback、radius=100 失败 rollback、一次 R2 dropped-write recovery、R2 耗尽后的 rollback，以及创建 persistence 失败后仅删除本次新特征。故障 adapter 仅在测试代码中使用，生产 CLI 没有故障注入开关。

Phase 4、5、6 原 Part 和 CADState 均作为只读保护对象，测试使用独立 Phase 7 Parts。结束时回到 Phase 4 原 Part；不自动保存或关闭用户文档。验证覆盖控制器跨进程连接当前仍打开模型，不声称 SOLIDWORKS 关闭重启后的恢复或其他版本的兼容性。

证据：`artifacts/phase7/unit-tests.json`、`artifacts/phase7/live/verification.json`、`artifacts/phase7/phase7-verification-evidence.json`。每次 command、rollback、验证、保护模型 before/after 与 raw timing 都保留。

复核命令：

```powershell
.\scripts\build.ps1 -SolidWorksApiDirectory 'D:\Solidworks Crops\SOLIDWORKS\api\redist'
.\scripts\test-batch.ps1
.\scripts\test-validation.ps1
.\scripts\test-planner.ps1
.\scripts\test-batch.ps1 -Live -BuildOnly
```

`-Live` 的完整执行需能连接当前 SOLIDWORKS COM session，原保护模型仍打开，并且 `artifacts/phase7/live` 尚未存在。程序拒绝覆盖已完成验证的测试状态与证据。默认明确使用已验证的 `gb_part.prtdot`，不更改 SOLIDWORKS 默认模板或打开模板 UI。
