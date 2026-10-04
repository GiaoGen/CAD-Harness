# Phase 4 verification — 强类型参数编辑

2026-10-03，SOLIDWORKS `32.0.1`。Phase 4 已在原 Phase 3 canonical model 上实际通过；没有删除、重建或替换模型。当前结果是 **80×50×12 mm 板、两个 Ø10 Through All 孔、孔中心 X=±25 mm、孔距 50 mm、四处 R4 外侧竖直圆角**。

## 模型与状态

目标为当前仍打开的 `零件2`、配置 `默认`，文档 GUID 为 `9af37689-f21c-4096-af17-63b878455f3d`。另一个测试文档 `零件3` 未参与本阶段编辑。

权威状态：[artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json](../artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json)。最终 `EditRevision=4`，`LastEditId=e6e5ce78-22c7-4766-8306-4273cc80935d`。保留 [Phase 4 状态快照](phase4-state-snapshot.json) 与 [Phase 3 原始快照](phase3-state-snapshot.json)。24 个原始 persistent reference 的 Base64 内容完全相同，未重新捕获或替换任何引用。

四个主 Feature 的原生 ID 在四次编辑前后及回滚后均保持：`base_plate=83`、`mounting_hole_seed=92`、`mounting_holes=99`、`outer_edge_fillet=101`。两个草图 Feature ID 为 73、84。

## 执行与真实结果

每条命令均启动独立进程，从磁盘加载 CADState，没有使用前一进程的 COM 对象或缓存。步骤顺序与 PRD 一致。

| 参数 | 修改 | PID | revision | 定位路径 | 原生参数与几何确认 |
|---|---|---:|---|---|---|
| `mounting_holes.spacing` | 40→50 mm | 25692 | 0→1 | target persistent Feature → owner `mounting_holes` → `D3` | 原生阵列 spacing=50、count=2；两个 Ø8 通孔中心为 (-25,0)、(25,0) mm |
| `base_plate.thickness` | 10→12 mm | 24640 | 1→2 | target persistent Feature → owner `base_plate` → `D1` | 拉伸尺寸=12；精确包围盒厚度=12；孔及圆角 Z 范围均为 0…12 mm |
| `mounting_hole_seed.diameter` | 8→10 mm | 25276 | 2→3 | seed persistent Feature + profile persistent Feature → `D1` | 草图直径=10；种子及第二实例圆柱孔壁直径均为 10 mm |
| `outer_edge_fillet.radius` | 3→4 mm | 15344 | 3→4 | target persistent Feature → owner `outer_edge_fillet` → `D1` | 原生 fillet radius=4；四个圆柱圆角面半径均为 4 mm |

孔距修改同时通过已保存的 `mounting_hole_seed.center_point` 引用将种子中心从 X=-20 移到 -25 mm，保持 PRD 的中心对称设计。程序进入已持久化的种子草图，通过原生 relation API 暂时释放圆心的 FIXED 约束，`SetCoords` 后恢复一个 FIXED 约束并退出草图。没有删除草图、孔、阵列或圆角特征。

四次编辑均 `EditRebuild3=true`、`NeedsRebuild2=0`。六个 Feature（含两个草图）的 `GetErrorCode2` 均为 0，warning 均为 false。每次确认均读取所有六个绑定的实际值，并检查未编辑尺寸未变化。

最终几何确认：

- `solidBodyCount=1`、`allBodyCount=1`。
- 六个方向的 `IBody2.GetExtremePoint` 给出精确包围盒 `[-40,-25,0,40,25,12]` mm，即 80×50×12 mm。
- 两个孔壁属于原来的种子 / 阵列 Feature，半径 5 mm，中心 X=-25 / +25、Y=0；实际孔距 50 mm，各有两个完整圆形边界，Z 范围 0…12 mm。种子原生 end condition=1（Through All）。
- 四个圆角面属于原来的 Fillet Feature，半径 4 mm，轴中心 XY 分别为 (±36,±21) mm，Z 范围 0…12 mm。

这些查询仅确认当前 canonical 编辑结果，不用于寻找替代对象或更新引用。确认代码为固定模型流程，不包含通用 Validator。

## 参数定位与提交契约

`ParameterEditIntent` 只允许四种 enum intent 与有限正数 mm 值。CLI selector 是明确的语义参数键，不接受自然语言。

定位链为：CADState semantic target → `GetObjectByPersistReference3` 恢复原 Feature → CADState `DimensionBinding.OwnerObjectSemanticId` 的持久化 Feature → `IFeature.Parameter(LocalDimensionName)` → `IDimension.SetSystemValue3`。孔径由种子草图持有，所以同时解析种子 Feature 和草图 Feature。没有按显示名称遍历或搜索。

编辑前核对文档 GUID、配置、已保存路径、原参数和 canonical 几何；参数尺寸使用当前配置。编辑后重建、读取实际参数及几何，确认成功才更新强类型参数、`CurrentValueMm`、revision、时间、edit ID 与 `ReferenceHealth`。`ValueMmAtCapture` 保留 Phase 3 的历史值。

同一 state path 使用独占文件锁；提交前检查原文件内容 SHA256 没有变化。状态写入同目录临时文件并由 `File.Replace` 原子替换。编辑证据在修改前写入一次，在确认后、CADState 提交前再次写入，最后记录提交或回滚结果。成功提交后的证据刷新故障单独报告 `EvidenceWriteError`，不撤销已提交编辑。未实现进程崩溃恢复或 SOLIDWORKS / JSON 跨系统事务。

## 原始引用存活结果

存活定义：原生 status=0，返回对象非空且 COM interface 与预期种类一致。不是对任意拓扑修改的稳定性保证。以下索引为零基索引；集合的每一个引用都有独立 before / after 原生结果。

| 原 Phase 3 semantic reference | 种类/数量 | 四次成功编辑，各次 before→after | R100 失败后 | spacing=100 失败后 | 对应回滚后 |
|---|---|---|---|---|---|
| `base_plate` | Feature 1 | Ok→Ok | Ok | Ok | Ok |
| `mounting_hole_seed` | Feature 1 | Ok→Ok | Ok | Ok | Ok |
| `mounting_holes` | Feature 1 | Ok→Ok | Ok | Ok | Ok |
| `outer_edge_fillet` | Feature 1 | Ok→Ok | Ok | Ok | Ok |
| `base_plate.profile_sketch` | Feature 1 | Ok→Ok | Ok | Ok | Ok |
| `mounting_hole_seed.profile_sketch` | Feature 1 | Ok→Ok | Ok | Ok | Ok |
| `mounting_hole_seed.center_point` | SketchPoint 1 | Ok→Ok | Ok | Ok | Ok |
| `base_plate.top_face` | Face 1 | Ok→Ok | Ok | Ok | Ok |
| `base_plate.bottom_face` | Face 1 | Ok→Ok | Ok | Ok | Ok |
| `mounting_hole_seed.wall_face` | Face 1 | Ok→Ok | Ok | **Invalid (1)** | Ok |
| `mounting_holes.instance_2.wall_face` | Face 1 | Ok→Ok | Ok | **Invalid (1)** | Ok |
| `outer_edge_fillet.rounded_faces[0…3]` | Face 4 | 全部 Ok→Ok | **全部 Invalid (1)** | 全部 Ok | 全部 Ok |
| `mounting_holes.direction_edge` | Edge 1 | Ok→Ok | Ok | Ok | Ok |
| `outer_edge_fillet.vertical_tangent_edges[0…7]` | Edge 8 | 全部 Ok→Ok | **全部 Invalid (1)** | 全部 Ok | 全部 Ok |
| 合计 | 24（Feature 6 / Face 8 / Edge 9 / SketchPoint 1） | **每次 24→24** | **12/24 有效** | **22/24 有效** | **每次 24/24 有效** |

R100 失败时 12 个 Invalid 引用，以及 spacing=100 失败时 2 个 Invalid 引用，均返回空对象；不是 Suppressed、Deleted 或类型不匹配。没有用名称、几何或新的 persistent reference 修复它们。恢复原值并 rebuild 后，原始引用自行重新有效。

完整逐引用事实、原生数字状态、对象类型、引用 hash、每次修改前后及回滚后的时间线：[edit-reference-survival evidence](edit-reference-survival-evidence.json)。`ReferenceSha256` 为原 Base64 字符串 UTF-8 的 SHA256。

## 失败测试

| 场景 | PID | 实测失败 | 恢复结果 | 磁盘 CADState |
|---|---:|---|---|---|
| R4→R100 | 16548 | `EditRebuild3=false`，Fillet error=51，12 个引用 Invalid | 直接写回 R4，重建成功，几何和 24 引用恢复 | 原 hash 不变，revision=4 |
| R4→R5，但状态副本为只读文件 | 15624 | R5 原生尺寸和几何确认成功，`File.Replace` 因访问拒绝失败 | 写回 R4，几何和 24 引用恢复 | 副本原 hash 不变；权威状态未修改 |
| 孔距 50→100 | 26400 | 种子孔移至 X=-50，第二实例 X=+50；重建失败，两个孔壁引用 Invalid | 恢复 spacing=50 和圆心 X=-25，实际孔中心回到 ±25，24 引用恢复 | 原 hash 不变，revision=4 |
| target Feature reference 人为替换为无效引用（副本） | 1984 | `PERSISTENT_REFERENCE_UNRESOLVED`，before/after 为 23/24 | `MutationStarted=false`，直接拒绝；没有 fallback | 副本原 hash 不变 |

只读副本还将四个 `SwFeatureNameAtCapture` 改为无关标签；仍能定位目标并实际达到 R5，证明编辑不依赖这些显示名称。目标无效引用测试使用故意损坏的副本，仅该测试的 `mounting_holes` 引用 hash 与原引用不同，不代表权威模型的原引用失效。副本只读属性已在 `finally` 中恢复，便于查看。

不支持的 selector 与 NaN 参数在 COM 连接前被拒绝，分别返回 `UNSUPPORTED_TYPED_EDIT` 与 `INVALID_EDIT_VALUE`。

原始编辑证据（包括每个引用及几何测量）：

1. [spacing 40→50](../artifacts/edits/fb1f4a09-b241-4f97-b6de-a87d171b0ae1.json)
2. [thickness 10→12](../artifacts/edits/83fd1bbd-0db3-40f1-82d2-5738d8236f39.json)
3. [diameter 8→10](../artifacts/edits/a1996c7a-490c-4f3b-8167-470b54aa0a9f.json)
4. [fillet 3→4](../artifacts/edits/e6e5ce78-22c7-4766-8306-4273cc80935d.json)
5. [R100 失败与回滚](../artifacts/edits/ff428129-5bb3-48bc-8302-89eb4610b0d0.json)
6. [只读状态提交失败与回滚](../artifacts/edits/86333e07-2e66-4723-a96e-49053d3e88ca.json)
7. [孔距失败与圆心回滚](../artifacts/edits/7d821ef5-1d51-4093-b7ee-bb36bb5ea518.json)
8. [失效目标引用直接拒绝](../artifacts/edits/471a9e9e-af12-497b-a20c-af0e73f07c5f.json)

## 跨进程重新加载与范围

全部失败测试结束后，新的只读恢复进程 PID **26200** 从权威磁盘状态解析 **24/24 references、6/6 dimension bindings**，实际参数为 spacing=50、thickness=12、diameter=10、radius=4。width=80、height=50。证据：[最终跨进程 resolve](../artifacts/phase4-final-resolve.json)。四次成功提交的状态 hash 逐次衔接，所有失败测试的前后 state hash 相同。

最终源码通过 x64 .NET Framework C# 编译，warnings-as-errors；没有新增外部依赖。直接 API 操作均在能够连接现有 SOLIDWORKS COM 实例的权限环境中执行。

已实现范围是四种强类型参数编辑、固定 canonical 结果确认、失败时恢复原值及引用事实记录。没有 LLM、自然语言、UI、通用 Validator、geometry/name fallback、复杂 topology recovery 或后续阶段功能。没有 SendKeys、鼠标、RunCommand、需要人工确认的交互命令。

模型仍未保存为 `.SLDPRT`；本次验证覆盖控制器进程退出和重新加载、对同一个仍打开模型的引用恢复，不包括 SOLIDWORKS 重启或文件关闭重开。
