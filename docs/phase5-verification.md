# Phase 5 verification — Natural Language → typed intent

2026-10-04。**PHASE 5 COMPLETE**。原有 76 项测试全部通过，独立 SOLIDWORKS Part 上的创建、四种编辑及既有 rollback 验证保持有效。本次仅补测两次真实 DeepSeek 调用：合法 spacing edit 已进入既有 Phase 4 Runtime 并提交成功；指定的非法 multi-edit 被现有 enum 校验拒绝，未进入 Runtime。未修改 Phase 1–5 源码、架构或生产 CLI。

## 实现与 Runtime 边界

新增 `ICadPlanner`，异步接收用户文本、可选的 data-only CADState 和 CancellationToken。返回 `PlannerResult`，包含不可变 `PlannedIntent`（已有 `CadIntent` 或已有 `ParameterEditIntent`）、metrics 或结构化错误。这样同时支持创建与编辑，未改动原 `CadIntent` / `ParameterEditIntent` 数据结构。

实现：

- `RulePlanner`：中英文已知句式的确定性解析，固定 canonical create，以及单个绝对 mm 参数编辑；不调用网络或 LLM。
- `LlmPlanner`：可配置的 OpenAI-compatible `POST <base-url>/chat/completions`，默认 `response_format=json_schema`、`strict=true`；可显式选择 `json_object` 兼容模式，仍须经过同一完整本地校验。
- `IntentSchema` + `StrictJson`：两种 planner 共用的强制边界。完整校验后才构造旧强类型 intent。
- `PlanRuntimeBridge`：只把已校验的不可变 intent 转成既有 `create-canonical` / `edit` 的参数并调用原入口。不会将原始 JSON、代码或自由操作列表传给 Runtime。

`Program.cs` 只增加 `plan` / `run` 命令分派和一个原入口 passthrough。删除这两处新增内容后，原文件内容 hash 与 Phase 5 开始前完全一致。其余 **13 个既有 C# 文件字节完全未改动**，包括执行器、CADState、引用、编辑、几何确认和 rollback。证据中的 `ProtectedRuntimeSources` 与 `OriginalProgramBodySha256` 给出逐文件 hash：[verification evidence](phase5-verification-evidence.json)。

规划阶段不连接 SOLIDWORKS。LlmPlanner 收到的状态投影只有 `units` 和四个语义参数的当前数值，不包含 feature 显示名称、persistent-reference 字节、文档 GUID、COM 对象或调用信息。没有给 LLM 注册 tools / functions，也不支持自由 tool sequence。

## Schema 与校验

实际 HTTP 请求携带的 schema：[phase5-intent.schema.json](phase5-intent.schema.json)。只有以下六个字段，全部 required，`additionalProperties=false`：

```json
{
  "intentVersion": "0.1",
  "mode": "edit",
  "units": "mm",
  "createPreset": null,
  "parameter": "mounting_holes.spacing",
  "valueMm": 50
}
```

| 字段 | 允许值 / 约束 |
|---|---|
| `intentVersion` | 仅 `0.1` |
| `mode` | 仅 `create` / `edit` |
| `units` | 仅 `mm`，没有隐式单位转换 |
| `createPreset` | `canonical` / null |
| `parameter` | 四个固定语义参数键 / null |
| `valueMm` | JSON number / null；编辑时有限且 `0 < valueMm <= 1000` |

本地还强制检查 mode 与字段的关联：create 只能使用 canonical preset，parameter/valueMm 必须为 null；edit 的 createPreset 必须为 null，必须恰好指定一个允许的参数和数值。1000 mm 是本阶段 planner 的输入范围上限，不表示该值在当前几何中可构成有效模型；实际可行性继续由原 Runtime 确认与 rollback。

canonical create 只能映射到原 `CadIntent.Canonical()`：80×50×10 mm 板、count=2、Ø8 Through All、spacing=40 mm、中心对称、四处 R3 外侧竖直圆角。没有开放新的尺寸组合或 Feature 类型。四个 edit enum 与 semantic selectors 为：

```text
HoleSpacing    → mounting_holes.spacing
PlateThickness → base_plate.thickness
HoleDiameter   → mounting_hole_seed.diameter
FilletRadius   → outer_edge_fillet.radius
```

严格 JSON 解析拒绝重复键、注释、尾随文本/逗号、Markdown fences、多个 intent、NaN/Infinity、溢出、非 JSON 数字、数值字符串和布尔值。未知 mode、preset、parameter 记录为 unsupported intent。响应中的 tool/function call、多模态 content、截断或不完整响应均不会进入 Runtime。

schema 没有第三种自由 `unsupported` intent；已知不支持的输入由 RulePlanner 返回结构化错误，或由 LLM provider 的 refusal / 非法 enum 触发拒绝。真实模型是否总能正确理解输入仍需真实服务评估，本次不作该推断。

## CLI 与配置

只规划，不连接 COM：

```powershell
.\bin\CadHarness.Cli.exe plan rule '创建标准板件'
.\bin\CadHarness.Cli.exe plan rule '将孔距从40 mm改为50 mm'
.\bin\CadHarness.Cli.exe plan rule '把板厚从10改为12毫米'
.\bin\CadHarness.Cli.exe plan rule '孔径改为10 mm'
.\bin\CadHarness.Cli.exe plan rule '将圆角半径改为4 mm'
```

规划成功后调用现有 Runtime：

```powershell
.\bin\CadHarness.Cli.exe run rule 'create canonical part' .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe run rule '孔距改为50 mm' .\artifacts\state\my-part.json
.\bin\CadHarness.Cli.exe run llm '把圆角半径改为4毫米' .\artifacts\state\my-part.json
```

`plan|run rule|llm "<command>" [state-file]`。显式 state-file 已存在时先加载其 CADState 数据供 planner 使用；create 可提供尚不存在的输出状态路径。edit 未指定路径时，原 Runtime 继续按当前模型 GUID 加载其默认 CADState。一个命令只规划一个 intent，不自动执行多个修改，也不自动在 rule 与 llm 之间切换。

RulePlanner 还接受 `spacing 50`、`thickness 12`、`hole diameter 10`、`fillet R4`，以及文档所示中文 / 英文 from→to 句式。from 值只是句式中的旧值描述，输出目标绝对值；原 Runtime 仍按当前真实 CADState 执行，不将它作为新的旧值断言。相对修改、未知单位、混合多个编辑或附加 Feature 均拒绝。

| 环境变量 | 配置 |
|---|---|
| `CAD_HARNESS_LLM_BASE_URL` | API base URL，如兼容服务的 `/v1`；程序追加 `/chat/completions` |
| `CAD_HARNESS_LLM_MODEL` | 服务模型 ID，不硬编码供应商 |
| `CAD_HARNESS_LLM_API_KEY` | 本机设置，不写入源码、日志或聊天；loopback 服务可省略 |
| `CAD_HARNESS_LLM_OUTPUT_MODE` | `json_schema`（默认）或 `json_object` |
| `CAD_HARNESS_LLM_TIMEOUT_SECONDS` | 1…120，默认 30 |
| `CAD_HARNESS_LLM_MAX_OUTPUT_TOKENS` | 128…4096，默认 512，使用 Chat Completions `max_tokens` 字段 |

远程 URL 使用 HTTPS，本地 loopback 可用 HTTP；禁止 URL 内嵌认证、query/fragment。没有自动重试、自动降级、重定向或失败后的 RulePlanner fallback。服务应兼容 Chat Completions 请求格式。取消与超时覆盖规划请求；已进入的旧 COM Runtime 不增加新的中断语义。

## 可观测性

每次 `plan` / `run` 输出单行 JSON，并保存 `artifacts/planner/<run-id>.json`。规划成功且执行前先持久化 log；不能保存该 log 时不进入 Runtime。完成后记录旧 Runtime 的原始结构化结果，包含其原编辑证据路径。旧命令的输出和失败行为保持原样。

`Planner` 字段记录：

- `Provider`、`Model`、`PlannerCalls`：每次请求调用一次 planner。
- `Calls`：LLM HTTP 发送尝试次数，正常 LLM 请求为 1，RulePlanner / 配置失败为 0；不按 CAD Feature 循环调用。
- `LatencyMs`：包含文本解析、HTTP 等待与本地 schema 校验的规划时间，不含 COM / rebuild / rollback。
- `InputTokens` / `OutputTokens`：读取 provider 的 `usage.prompt_tokens` / `completion_tokens`；缺失或无效为 null，不估算。RulePlanner 为 null。
- `ParseFailures`：JSON、schema、numeric 或协议解析失败计 1。
- `UnsupportedIntents`：不支持的规则/enum/preset/参数或 provider refusal 计 1；与 parse failure 分类区分。
- `OutputSha256`：对原输出文本取 hash；非法原始输出不成为 Runtime 参数。

HTTP 错误只记录状态码，不记录原始错误 body、Authorization 或 API key。没有配置 LLM 时实测 `PLANNER_CONFIG_INVALID`、`Calls=0`、`RuntimeEntered=false`。

## 测试与实机证据

构建与测试：

```powershell
.\scripts\build.ps1
.\scripts\test-planner.ps1
```

x64 .NET Framework 编译，warnings-as-errors。新增 HTTP 使用 Framework 的 `System.Net.Http`，没有下载 NuGet 或第三方依赖。

[76 项测试原始记录](../artifacts/phase5/planner-test-results.json) 包含 canonical 与四个 edit 的强类型映射、中英文解析、未知/混合/相对 intent 拒绝、schema/enum/numeric 限制、工具调用拒绝、token 缺失或损坏、provider refusal、HTTP 429 不重试、输入/输出大小限制、非法 UTF-8、取消、超时、配置错误、状态投影，以及真正通过 TCP loopback 的 HTTP 请求与环境变量配置。测试未依赖 SOLIDWORKS，也不调用真实模型。

实机使用独立 `零件4`、配置 `默认`，GUID=`2dc656eb-6981-4585-b789-c480aacd7356`。通过 direct API 和 Phase 2 已验证模板准备空 Part；生产 CLI 本身没有新增自动新建或切换文档路径。

| 自然语言流程 | planner latency ms | LLM calls | 实测结果 |
|---|---:|---:|---|
| `create canonical part` | 22.0074 | 0 | Rule → 旧创建 Runtime → canonical model + CADState |
| 孔距 40→50 | 10.6060 | 0 | 强类型 spacing edit 成功；孔中心 ±25；24/24 引用有效 |
| 板厚 10→12 | 8.4922 | 0 | 原生厚度 12，24/24 引用有效 |
| 孔径 8→10 | 9.2128 | 0 | 两个真实孔壁 Ø10，24/24 引用有效 |
| 圆角 3→4 | 8.9484 | 0 | 四处真实 R4，24/24 引用有效 |
| loopback LLM 返回额外 `apiCall` | 61.3140 | 1 | `PLANNER_SCHEMA_INVALID`，parse failure=1；未进入 Runtime |
| loopback LLM 返回合法 R100 edit | 49.5301 | 1 | schema 成功；旧 Runtime 重建失败并恢复 R4；状态 hash 不变，24/24 引用恢复 |
| loopback LLM 返回 canonical create，但当前已有实体 | 49.9979 | 1 | 原 `PART_NOT_EMPTY` gate 拒绝；没有重新生成 |
| loopback LLM 返回合法 R4 plan-only | 48.6036 | 1 | 强类型 intent 成功，不进入 Runtime |

这些时间是本机一次实测，不是性能 benchmark 或真实模型 latency。loopback fixture 可明确返回 usage=23/17，也可省略 usage；日志分别原样记为 23/17 或 null，**这些数字是协议测试数据，不是真实模型用量**。对应请求记录：[loopback CLI 请求](../artifacts/phase5/loopback-cli-requests.json)。

实机创建和编辑：[create](../artifacts/phase5/live-create-rule.json)、[spacing](../artifacts/phase5/live-edit-spacing.json)、[thickness](../artifacts/phase5/live-edit-thickness.json)、[diameter](../artifacts/phase5/live-edit-diameter.json)、[fillet](../artifacts/phase5/live-edit-fillet.json)。LLM 边界：[额外字段拒绝](../artifacts/phase5/llm-extra-field.json)、[既有 rollback](../artifacts/phase5/llm-rollback.json)、[已有模型拒绝](../artifacts/phase5/llm-existing-part.json)、[plan-only](../artifacts/phase5/llm-plan.json)。

上述首次实机验证结束时，测试 Part 的状态 revision=4，为 80×50×12、Ø10、spacing=50、R4。新进程 PID **26684** 加载状态并恢复 **24/24 references、6/6 bindings**：[首次测试模型最终 resolve](../artifacts/phase5/live-final-resolve.json)、[当时状态快照](phase5-test-state-snapshot.json)。下述真实端到端补测后，该测试 Part 为 spacing=55、revision=5。

最后已恢复原 `零件2` 为活动文档。原 Phase 4 CADState 的字节 hash 仍为 `430F65489CA502DB8FA8FADE51B25768DE6597563CB665A48F0B9D808BBA3CE7`，与 Phase 4 快照相同；新进程 PID **12572** 恢复 **24/24 references、6/6 bindings**，参数仍为 50 / 12 / Ø10 / R4：[原模型只读核对](../artifacts/phase5/original-final-resolve.json)。测试 Part 保留在 SOLIDWORKS 中，未保存为 `.SLDPRT`。

## 首次真实 DeepSeek API 规划验证（历史记录）

用户设置本机 API key 后，调用官方 `https://api.deepseek.com/v1/chat/completions`，配置模型 ID 为 `deepseek-chat`，使用 `json_object` 模式。DeepSeek 的 JSON 输出仍须通过相同的严格本地 schema / enum / numeric 校验。

本次仅发送固定合成测试句 **“将孔距改为50毫米”** 与 planner 内置的受限 schema，`currentState=null`。没有发送磁盘 CADState、模型参数投影、persistent references、文档 GUID 或真实模型的 feature 名称。执行 `plan llm`，`RuntimeEntered=false`。

实际返回并经校验的 intent：

```json
{
  "intentVersion": "0.1",
  "mode": "edit",
  "units": "mm",
  "createPreset": null,
  "parameter": "mounting_holes.spacing",
  "valueMm": 50
}
```

| 记录 | 实测值 |
|---|---|
| 成功请求 run ID | `06c314c9-9b3d-4bea-b5fc-a651762e09ea` |
| 强类型对象 | `ParameterEditIntent` |
| 成功记录中的 HTTP calls | 1 |
| planner latency | 1614.6611 ms |
| 实际 input / output tokens | **430 / 37**，来自真实 provider usage |
| parse failures / unsupported intents | 0 / 0 |
| 原 Phase 4 CADState | 前后 SHA256 完全一致 |

首次真实发送尝试在 TLS 握手阶段失败，尚未得到模型响应或 usage；诊断发现 Framework 编译的默认协议为 `Ssl3, Tls`。无认证 HTTPS 探针在明确使用 TLS1.2 时获得 HTTP 响应。仅在新增的 `LlmPlanner` 的 `HttpClientHandler` 上指定 TLS1.2 后重试成功，不修改任何 Phase 1–4 执行、状态、编辑和 rollback 逻辑，不关闭证书校验。修复后重新构建，76 项测试仍全部通过。

因此，记录中有一次失败的传输发送尝试和一次成功的模型响应；每个 planner invocation 仍只有一个 HTTP attempt，没有自动重试。首次脚本因会外发 CADState 投影被自动审批拒绝，未执行；安全替代方案移除了 state-file 参数，后续实际调用均为无 CADState 的合成输入。

证据：[真实 API 结果](../artifacts/phase5/deepseek-real-api-result.json)、[成功 planner 原始记录](../artifacts/planner/06c314c9-9b3d-4bea-b5fc-a651762e09ea.json)、[首次传输失败](../artifacts/phase5/deepseek-real-api-transport-failure.json)、[HTTPS 诊断](../artifacts/phase5/deepseek-https-diagnostic.jsonl)。日志不包含 API key。可复用脚本为 `scripts/test-deepseek-api.ps1`，直接读取本机用户级密钥，无需将密钥写入项目。

## 两次真实 DeepSeek API 端到端补测

执行窗口为 **2026-10-04 00:00:00–00:00:23（Asia/Taipei）**，对应 UTC 2026-10-03 16:00:00–16:00:23。使用既有 `deepseek-chat`、`https://api.deepseek.com/v1`、`json_object` 和强制本地 schema / enum / numeric 校验。仅发送下列两个真实 HTTP 请求，每条命令各一次；没有 retry、fallback、RulePlanner、loopback 或 synthetic response。读取用户级 API key，仅保存在进程内，未写入源码、日志、文档或输出。

使用原 Phase 5 测试 `零件4`，GUID=`2dc656eb-6981-4585-b789-c480aacd7356`，CADState 为 `artifacts/phase5/live-model-state.json`。补测前通过独立进程 direct API 只读探针确认：revision=4，spacing=50、thickness=12、diameter=10、radius=4，6 个 dimension bindings 与实际参数一致，24/24 persistent references 有效，无活动 sketch、无 rebuild error。原 Phase 4 `零件2` 同样通过预检。本次仅向 LLM 发送既有 planner 的四个语义参数数值投影，不发送原生引用、feature 显示名称或模型 GUID。

```powershell
.\bin\CadHarness.Cli.exe run llm '把孔距改成55毫米' .\artifacts\phase5\live-model-state.json
.\bin\CadHarness.Cli.exe plan llm '同时把板厚改成15毫米，并把孔径改成12毫米' .\artifacts\phase5\live-model-state.json
```

| 真实记录 | 成功 spacing edit | 非法 multi-edit |
|---|---:|---:|
| HTTP calls | 1 | 1 |
| planner latency | 1822.9402 ms | 2335.6974 ms |
| input tokens | 471 | 478 |
| output tokens | 37 | 32 |
| 总命令 wall-clock | 9133.3596 ms | 2447.7205 ms |
| ParseFailures | 0 | 0 |
| UnsupportedIntents | 0 | 1 |
| RuntimeEntered | true | false |

wall-clock 包含 CLI 启动、规划、日志写入以及成功命令中的既有 Runtime；不包含随后独立进程的只读探针。tokens 均来自真实 DeepSeek `usage`。

成功命令生成经校验的 `ParameterEditIntent`：`parameter=mounting_holes.spacing`、`valueMm=55`。planner run ID 为 `78d630ed-1bff-4745-a26c-61474fe742f0`；Runtime edit ID 为 `aaf0b105-1e45-4126-a438-12da0f909b64`。既有 Phase 4 Runtime 返回 `Success=true`、exit code=0、`Status=Committed`、`StateCommitted=true`、`RebuildSucceeded=true`，无执行或证据写入错误。

Runtime 的真实参数回读及随后独立进程探针一致确认：

- pattern spacing 与实际孔中心距离均为 **55 mm**；两个孔的轴点为 **(-27.5, 0, 0)** 和 **(27.5, 0, 0) mm**，保持中心对称。
- 仍为两个 Ø10 Through All 孔、四处 R4 圆角、一个 solid body；bounding box 为 `[-40,-25,0,40,25,12] mm`。
- `NeedsRebuild=0`，四个主 Feature 与两个 Sketch 的 error 均为 0。
- 编辑前、Runtime 编辑后、独立进程探针均为 **24/24 references 有效**：6 Feature、8 Face、9 Edge、1 SketchPoint。
- CADState revision **4→5**，feature 参数和 dimension binding 的当前 spacing 都为 **55**，`LastEditId` 与实际 edit ID 一致。

该成功提交沿用已有“修改 → rebuild → 真实参数/几何确认 → 保存编辑证据 → 原子更新 CADState”的主链路。本轮成功编辑无需 rollback（`RollbackAttempted=false`），未额外做失败注入；既有 rollback / atomic commit 的源文件字节未变，先前的 [rollback 实机证据](../artifacts/phase5/llm-rollback.json) 保持有效。本轮没有声称重新验证所有故障情况下的 rollback。

非法 multi-edit 的 run ID 为 `2f94bb3f-be5a-4f7f-9616-8ffb9423e597`。真实输出由现有 semantic parameter enum 校验返回 **`PLANNER_UNSUPPORTED_INTENT` / `Unknown semantic parameter.`**；`ValidatedIntent=null`、`TypedIntent=null`、`Success=false`、`RuntimeEntered=false`、`RuntimeResult=null`。这是 unsupported enum 拒绝，故 `ParseFailures=0`、`UnsupportedIntents=1`。未将多参数输入压缩成可执行的合法单参数 intent，`SemanticSafetyFailure=false`。现有日志仅保存输出 SHA256 和校验错误，未保留或重建非法原始响应。

多参数命令前后，测试 CADState SHA256 均为：

```text
635D63C20AF29AAA7FE4F539FEE656991536FDA27BE763D0D4B9615C00A4FDED
```

前后独立只读探针的完整 native confirmation 一致，revision 仍为 5、spacing 仍为 55、引用仍为 24/24；SOLIDWORKS 几何与 CADState 均未修改。

原 Phase 4 `零件2` 已恢复为活动文档；补测前后 native confirmation 完全一致，参数仍为 spacing=50、thickness=12、Ø10、R4，revision=4，24/24 references 有效。原 CADState SHA256 前后均为：

```text
430F65489CA502DB8FA8FADE51B25768DE6597563CB665A48F0B9D808BBA3CE7
```

对全部 24 个 `src/CadHarness.Cli` 文件、3 个既有脚本和生产 `bin/CadHarness.Cli.exe` 的 **28 项前后 hash 核对全部一致**。没有 Runtime/source 文件变化，没有新增功能、新 CAD Feature、多参数编辑或 tool calling。临时 direct API 探针及执行包装脚本已移除，保留原始 JSON 证据。测试 Part 保留成功的 55 mm、revision=5 状态，未执行额外恢复编辑，仍未保存为 `.SLDPRT`。

| 补测结论字段 | 实测结果 |
|---|---|
| `RealLlmInferenceVerified` | true |
| `RealLlmRuntimeExecutionVerified` | true |
| `IllegalMultiEditSafelyRejected` | true |
| `SemanticSafetyFailure` | false |
| `RuntimeSourceFilesChanged` | false |
| `Phase4OriginalStateHashUnchanged` | true |
| 结论 | **PHASE 5 COMPLETE** |

完整补测证据（含两条命令、真实用量、wall-clock、完整 Runtime result、逐引用审计、预检/后检、状态及 source hashes）：[deepseek-real-e2e-verification.json](../artifacts/phase5/deepseek-real-e2e-verification.json)。原始记录：[成功编辑](../artifacts/phase5/real-e2e/success-edit.json)、[非法多参数规划](../artifacts/phase5/real-e2e/multi-edit-plan.json)、[编辑后只读核对](../artifacts/phase5/real-e2e/test-probe-after-edit.json)、[多参数命令后只读核对](../artifacts/phase5/real-e2e/test-probe-after-multi-edit.json)、[原 Phase 4 模型只读核对](../artifacts/phase5/real-e2e/original-probe-after.json)。

## 未覆盖范围

本阶段没有 multimodal、Jev、通用 geometry planning、新 CAD Feature 类型、几何/名称 fallback 或后续阶段的通用 Validator / recovery。没有 SendKeys、鼠标、RunCommand、人工确认的交互式建模路径。

真实 DeepSeek 验证现已覆盖首次无状态的 spacing 规划，以及本次带测试状态的 spacing=55 实际执行和指定 multi-edit 的安全拒绝。未评估广泛自然语言理解、真实 LLM 的 canonical create / 其余三种 edit、其他复杂指令或其他供应商兼容性。一次成功拒绝不代表所有 multi-edit 都能被拒绝；现有 schema 仍没有显式 unsupported 分支，本次未扩大 scope 修改 planner/schema。
