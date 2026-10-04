# CAD Harness — Phase 0–7 + Phase 8 benchmark runner

Phase 0–7 已完成：一个 x64 C# 外部控制程序，通过原生 SOLIDWORKS API 创建 canonical part、持久化 CADState 并编辑已有参数。Phase 5 增加受限自然语言 planner；Phase 6 加入确定性 Validator 和 R1–R5 有限恢复；Phase 7 加入原生 API batch scope。Phase 8 新增严格 manifest 驱动的 benchmark runner；PRD Case B 的四孔板尚不在既有 canonical Runtime 能力中，会明确返回 `BLOCKED_CAPABILITY`，完整 Phase 8 尚未通过全部验收。

## 环境和构建

Phase 8 使用：`bin/CadHarness.Cli.exe benchmark tests/benchmark-cases/mvp.json [output-directory]`。每次生成独立 run/Part/CADState，执行固定 A–E 案例并写 JSON，保留阻塞/失败案例在统计分母。完整契约见 [benchmark.md](docs/benchmark.md)，实机证据见 [Phase 8 verification](docs/phase8-verification.md)。默认 RulePlanner；选择 LlmPlanner 时复用现有配置和验证，不做 fallback。

- Windows、已运行的 SOLIDWORKS，以及它安装目录下的 `api/redist` Interop 库。
- .NET Framework 4.8。当前机器使用 Windows 自带的 Framework x64 C# 编译器构建，无需安装 .NET SDK 或下载 NuGet 包。
- 执行进程需要能访问 SOLIDWORKS 的 COM Running Object Table。本次沙箱内连接返回 `0x800401E3`，提升权限后连接成功；启动终端时应使用与 SOLIDWORKS 匹配的权限。

在 `D:\CAD-Harness` 中运行：

```powershell
.\scripts\build.ps1
```

脚本从 SOLIDWORKS 的 COM 注册项发现安装路径。也可以明确指定：

```powershell
.\scripts\build.ps1 -SolidWorksApiDirectory 'D:\Solidworks Crops\SOLIDWORKS\api\redist'
```

编译结果为 `bin/CadHarness.Cli.exe`。源码项目为 `src/CadHarness.Cli/CadHarness.Cli.csproj`；如果另有 .NET SDK 和 .NET Framework 4.8 开发包，也可以构建该项目，并用 `SolidWorksApiDirectory` 属性指定 Interop 目录。

## 运行

Phase 0，连接已经运行的实例并读取当前 Part 的版本、标题、路径和配置：

```powershell
.\bin\CadHarness.Cli.exe inspect
```

Phase 1，事先在 SOLIDWORKS 中准备一个空 Part，并使其成为当前文档，然后运行：

```powershell
.\bin\CadHarness.Cli.exe create-plate
```

Phase 2，在当前空 Part 中执行硬编码的强类型 `CadIntent`：

```powershell
.\bin\CadHarness.Cli.exe create-canonical
```

创建顺序：80 × 50 × 10 mm 基础板 → 位于 (-20, 0) mm 的一个 Ø8 通孔种子 → 沿 +X、count=2（含种子）、spacing=40 mm 的原生线性阵列 → 四条外侧竖直边 R3 圆角。最终孔中心为 (-20, 0)、(20, 0) mm。

Phase 3 已接入此创建入口：成功创建后自动捕获 CADState，输出中的 `cadState.statePath` 给出实际 JSON 路径。也可指定状态文件路径：

```powershell
.\bin\CadHarness.Cli.exe create-canonical .\artifacts\state\my-part.json
```

对已经通过 Phase 2 验证、仍保留规范特征名称的当前模型，单独捕获状态：

```powershell
.\bin\CadHarness.Cli.exe capture-state
```

默认文件为 `artifacts/state/<document-guid>.json`。再次捕获会复用模型已有 GUID，并原子替换属于同一文档和配置的状态文件。指定其他文件名时，后续加载也应传入该路径：

```powershell
.\bin\CadHarness.Cli.exe capture-state .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe resolve-state .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe state .\artifacts\state\my-part.json
```

`resolve-state` 每次启动新进程，从磁盘加载状态，在当前同一 Part、同一配置中通过 persistent references 恢复对象。使用默认 GUID 路径捕获时可直接运行 `resolve-state`。`state <path>` 仅打印磁盘状态，不连接 SOLIDWORKS。

Phase 4，在当前同一 canonical model 中依次编辑已有参数：

```powershell
.\bin\CadHarness.Cli.exe edit mounting_holes.spacing 50
.\bin\CadHarness.Cli.exe edit base_plate.thickness 12
.\bin\CadHarness.Cli.exe edit mounting_hole_seed.diameter 10
.\bin\CadHarness.Cli.exe edit outer_edge_fillet.radius 4
```

每条 `edit <semantic-parameter> <mm> [state-file]` 均从磁盘加载状态，通过 semantic target、persistent Feature reference 和 dimension binding 定位。孔距修改还通过持久化圆心引用保持孔对称。修改后 rebuild、读取真实参数和固定 canonical 几何，成功才原子更新 CADState。Phase 6 记录逐项 Validator 结果与有限恢复；符合 canonical 几何规则的失效 Face/Edge 可在内存中恢复，成功提交时一并保存新引用。原引用的 before/after 审计与修复后审计分开记录。

失败优先写回原参数并重建；孔距失败也恢复圆心，CADState 不提交新值。输出明确记录 `RollbackSucceeded`，若恢复不完整则需要处理当前模型后再继续。不要用 `capture-state` 代替编辑后的状态提交：它是首次采用 Phase 2 canonical model 的入口。

Phase 5，先解析自然语言为已有强类型 intent；`plan` 不连接 SOLIDWORKS，`run` 才调用既有 Runtime：

```powershell
.\bin\CadHarness.Cli.exe plan rule '创建标准板件'
.\bin\CadHarness.Cli.exe plan rule '将孔距从40 mm改为50 mm'
.\bin\CadHarness.Cli.exe run rule 'create canonical part' .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe run rule '孔距改为50 mm' .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe run rule '把板厚从10改为12毫米' .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe run rule '孔径改为10 mm' .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe run rule '将圆角半径改为4 mm' .\artifacts\state\my-part.json
```

`ICadPlanner` 具有确定性的 `RulePlanner` 和可配置的 `LlmPlanner`。创建只允许固定 canonical preset；编辑每次只允许一个 Phase 4 语义参数，长度为有限的 `0 < mm <= 1000`。规则解析只支持文档中的明确句式；相对修改、混合操作、其他单位和 Feature 拒绝。

LLM 使用 OpenAI-compatible Chat Completions，通过本机环境变量配置 `CAD_HARNESS_LLM_BASE_URL`、`CAD_HARNESS_LLM_MODEL`、`CAD_HARNESS_LLM_API_KEY`，base URL 后追加 `/chat/completions`。API key 不写入源码或日志，loopback 服务可省略。默认使用 strict JSON schema；不支持该服务格式时可明确设置 `CAD_HARNESS_LLM_OUTPUT_MODE=json_object`，本地 schema / enum / numeric 校验仍执行。没有自动降级或重试。

```powershell
.\bin\CadHarness.Cli.exe plan llm '把圆角半径改为4毫米' .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe run llm '把圆角半径改为4毫米' .\artifacts\state\my-part.json
.\scripts\test-planner.ps1
```

planner 不接收 COM 对象；LLM 状态上下文只有四个语义参数数值，不包含 feature 名称或 persistent references。输出只允许受限 create/edit schema，不接受代码、CAD API、工具列表或多步操作；完整校验通过后才进入旧 Runtime。每次请求记录 `artifacts/planner/<run-id>.json`，包含 latency、planner/LLM 调用次数、input/output tokens（缺失为 null）、parse failures、unsupported intents 和 Runtime 结果。配置和句式细节见 [Phase 5 verification](docs/phase5-verification.md)。

程序不自动启动 SOLIDWORKS，不切换文档。创建入口拒绝已有实体、已有建模特征或正在编辑的草图；再次对成功创建的同一文档执行创建会返回 `PART_NOT_EMPTY`。编辑入口要求已有的匹配 CADState 和完成重建的 canonical model。

Phase 1 成功后，当前文档内保留中心矩形草图和原生凸台拉伸。宽、高分别有 80 mm、50 mm 的可编辑尺寸，拉伸深度为 10 mm。板件创建在第一个原生参考平面上；标准模板中是前视基准面。Phase 2 还会留下独立的孔种子、线性阵列和圆角特征。程序不自动保存或覆盖文件。

输出为单行 JSON，退出码 `0` 表示成功，`1` 表示执行失败，`2` 表示命令格式错误。创建和编辑均返回原生参数、canonical Validator、引用审计及状态提交事实；编辑失败包含 rollback 和稳定 failure class。Validator 仅覆盖当前已支持的 canonical feature vocabulary。

## Phase 6 验证与有限恢复

```powershell
.\bin\CadHarness.Cli.exe validate .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe recover-state .\artifacts\state\my-part.json
.\scripts\test-validation.ps1
```

`validate [state-file]` 对当前匹配文档做只读验证，不 rebuild、不修改 SOLIDWORKS 或 CADState。它验证 Feature 存在/类型/身份、native 依赖和历史顺序、尺寸绑定、body count、精确 bounding box、孔数量/直径/间距/对称/贯穿、pattern、圆角、引用的恢复与语义几何，以及 state/model 一致性。每项返回 `Name`、`Expected`、`Actual`、`Status`，失败返回 `FailureClass`。结果保存到 `artifacts/validation/<run-id>.json`。

`recover-state [state-file]` 仅修复已失效的 canonical Face/Edge 引用，不修改模型几何；必须先确认原生特征、参数、几何正确，再唯一解析所需实体。验证通过才原子提交引用并将 revision 增加一次；无修复时不写状态。编辑入口也使用同一 R1 恢复，并把新引用与参数作为一次事务提交。

- R1：仅 canonical 顶/底面、两个孔壁、四个圆角面、阵列方向边及八条圆角切向边。先尝试 persistent reference，再按固定几何/owner 规则恢复；不按显示名称搜索。Feature、Sketch 和 SketchPoint 身份失效时拒绝猜测。
- R2：目标参数未应用时，只重新解析 dimension 并直接重设一次，再 rebuild/validate；失败则进入已有原值恢复，不循环尝试。
- R3：rebuild/验证失败后恢复原参数和必要圆心，重建并验证。CADState 不提交新值。
- R4：不可能的圆角返回 `GEOMETRY_IMPOSSIBLE` 并 rollback；不试探更小半径。
- R5：已有 planner/schema 对不支持或歧义输出给出结构化拒绝；不进入 Runtime、不生成部分几何。受限 schema 不保证真实 LLM 对所有复杂句子都能正确拒绝。

canonical 创建在捕获后、保存 CADState 前执行同一 Validator；创建失败时只删除本次在已验证空 Part 中生成的 canonical 特征，不删除已有用户特征。rollback 不完整会明确报告。

可选实机验证会新建独立 Part，要求原 Phase 4/5 Part 仍打开，默认拒绝覆盖已有测试状态：

```powershell
.\scripts\test-phase6-live.ps1 -PartTemplate 'C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2024\templates\gb_part.prtdot'
```

该模板是本机已验证路径，其他机器须提供自己的模板。生产 CLI 不新增自动切换或创建文档功能。验证范围和原始证据见 [Phase 6 verification](docs/phase6-verification.md)。

## Direct API 路径

```text
Marshal.GetActiveObject → ISldWorks.ActiveDoc
→ 原生基准面 IFeature.Select2
→ ISketchManager.InsertSketch / CreateCenterRectangle
→ AddHorizontalDimension2 / AddVerticalDimension2
→ IDimension.SetSystemValue3
→ IFeatureManager.FeatureExtrusion3
→ IModelDoc2.EditRebuild3 / GraphicsRedraw2
```

Phase 2 接着执行：

```text
板件顶面 IEntity.Select4 → CreateCircleByRadius
→ 原生圆心约束和直径尺寸 → FeatureCut4（Through All）
→ 方向边和种子特征对象选择 → FeatureLinearPattern5
→ 四条外侧竖直边对象选择 → FeatureFillet3
→ EditRebuild3 / GetErrorCode2 / GraphicsRedraw2
```

实体选择通过原生对象完成，不依赖中文或英文特征名称，也不使用屏幕坐标。长度统一由 mm 转成 API 使用的米。

尺寸创建前暂时关闭 `swInputDimValOnCreate`，避免尺寸值输入对话框，并在 `finally` 中恢复原值。正常执行无需中途人工确认。没有使用 `SendKeys`、鼠标控制、`RunCommand`、PropertyManager 或 UI 自动化。

## 范围和失败行为

`CadIntent.cs` 定义固定创建顺序所需的四种强类型参数对象；`CanonicalExecutor.cs` 直接执行它们。孔中心使用固定点约束，孔径是原生可编辑尺寸；阵列和圆角也使用原生特征参数。`ParameterEditIntent`、`EditContext`、`ParameterEditRunner` 实现四种限定编辑；`EditConfirmation` 仅确认该 canonical model，`ReferenceAudit` 独立记录每个旧引用的真实恢复状态。

Phase 2 的几何选择和 Phase 3 初次捕获仅支持标准 XY / +Z 的 canonical 板件。首次捕获按 Phase 2 的已知特征名称识别四个原生特征，保存独立语义身份及其 native persistent references。Feature 恢复不使用特征名称；Phase 6 只在有限 Face/Edge 语义上增加几何回退。

CADState 的数据类位于 `CadState.cs`，包含文档身份、四个语义特征及强类型参数、依赖关系、实体引用和尺寸绑定。`StateCapture`、`StateService`、`PersistentReferences` 和 `DocumentIdentityStore` 隔离了 SOLIDWORKS 查询和身份属性操作；`StateSerializer` 负责版本/必需字段检查和原子 JSON 写入。

文档身份通过原生自定义属性 `CAD_HARNESS_DOCUMENT_ID` 记录，语义特征身份为文档 GUID 与 `SemanticId` 的组合。捕获会新增此元数据属性，几何和尺寸不会变化。文件路径、配置、捕获时特征名和 native feature ID 也保留用于检查，特征名字和数值 feature ID 不作为恢复依据。

保存了四个主 Feature、两个草图 Feature、八个 Face、九个 Edge 和一个孔圆心 SketchPoint 的引用，共 24 个。四条原始尖角竖边已被圆角消耗，因此当前边集合明确记录八条圆角竖直切向边界，而不把它们误称为原来的四条尖角边。

本机 `GetPersistReference3(IDimension)` 返回空引用。六个尺寸绑定使用所属 Feature 的稳定引用和局部参数名，例如 `D1` / `D2` / `D3`，恢复后通过 `IFeature.Parameter` 查询或修改尺寸。阵列 count 记录为原生定义属性 `ILinearPatternFeatureData.D1TotalInstances`。

模型仍由 SOLIDWORKS 管理，程序不会自动保存 `.SLDPRT`。已验证控制器退出、重新启动后对同一个仍打开模型的恢复，以及四种参数修改和失败回滚时的引用状态；SOLIDWORKS 重启、零件关闭/重开或其他拓扑变化尚未验证。

未实现 multimodal、Jev、通用 geometry planning、新 CAD Feature 类型、任意模型 Validator、UI、复杂 topology recovery 或崩溃恢复。

Phase 7 的 `SolidWorksBatchScope` 在建模/编辑时暂设 `CommandInProgress`、关闭 FeatureManager tree/window 和当前 view graphics 更新，保存并恢复实际原值，支持嵌套和异常清理。恢复后按原更新设置执行一次 tree refresh/redraw。孔圆草图的单个 primitive 使用 `AddToDB` / `DisplayWhenAdded`；矩形约束和编辑的 fixed-point 关系保留原有 solver 行为。canonical 创建将显式 rebuild 从两次减少至最后一次；普通 edit 的一次 rebuild 和失败 recovery/rollback 的必要 rebuild 全部保留。恢复失败发生在状态提交前，返回可检查的错误；不会为了优化跳过 Validator 或原子提交。

创建输出的 `batch`、编辑 evidence 的 `Batch` 记录 scope 耗时、显式 rebuild 次数/时间、显式 tree refresh/redraw 次数和恢复结果。计数不包括 SOLIDWORKS 在 sketch exit / feature API 内部的隐式更新。专项验证见 [Phase 7 verification](docs/phase7-verification.md) 和 [实机证据](artifacts/phase7/live/verification.json)。`scripts/test-batch.ps1` 执行 scope 单元测试；`-Live` 使用独立 Part 执行固定验收及优化前后测量，拒绝覆盖已有实机证据目录。它不是 Phase 8 的通用 benchmark runner。

失败会输出稳定错误码。canonical 创建失败会尝试只回滚本次新建特征，并报告 `creationRollback`；回滚不完整或 Phase 1 旧 `create-plate` 路径失败时，仍可能输出 `partialModelMayRemain: true`。程序恢复其临时修改的尺寸输入设置并结束它开启的草图。编辑恢复见 `Status`、`FailureClass`、`RecoveryAttempts`、`RollbackSucceeded`、`RollbackError` 和引用审计。

恢复状态时，身份、配置或已保存路径不一致会被拒绝。旧 `resolve-state` 仍仅检查 persistent references；Phase 6 `recover-state` 才执行有界几何恢复，候选不唯一时拒绝。不同文档或配置的状态文件不会被覆盖。未保存的模型若在 SOLIDWORKS 关闭时丢弃，其磁盘 CADState 不会重建出该模型。

本机验证记录：[Phase 0 + Phase 1](docs/phase01-verification.md)、[Phase 2](docs/phase2-verification.md)、[Phase 3](docs/phase3-verification.md)、[Phase 4](docs/phase4-verification.md)、[edit-reference-survival evidence](docs/edit-reference-survival-evidence.json)、[Phase 5](docs/phase5-verification.md)。前几阶段文档保留历史结果；当前原模型已完成 Phase 4，Phase 5 实机验证使用独立测试 Part。76 项 planner 测试通过，并已补充真实 DeepSeek `deepseek-chat` 的一次成功规划：spacing=50 mm，latency=1614.6611 ms，实际 tokens=430/37；使用无 CADState 的合成输入。LlmPlanner 的 HTTPS 传输明确使用 TLS1.2。[真实 API 证据](artifacts/phase5/deepseek-real-api-result.json) 与 `scripts/test-deepseek-api.ps1` 可供核对和复测。一次性准备程序和探针已移除，只保留证据和自动测试。
