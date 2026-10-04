# Phase 3 verification — CADState + semantic identity persistence

日期：2026-10-03（Asia/Taipei）。

本文件保留 Phase 3 完成时的历史验证结果。原 `零件2` 后续已完成 Phase 4 参数编辑，当前模型与 CADState 的数值见 [Phase 4 verification](phase4-verification.md)。Phase 3 的快照仍是 40 / 10 / Ø8 / R3 基线。

## 结论

Phase 3 已在 SOLIDWORKS `32.0.1` 中实际验证通过：保存进程退出后，另一个独立 C# 进程能够从磁盘反序列化 CADState，并在当前同一模型中通过 native persistent references 恢复四个主特征、两个草图特征、八个面、九条边和一个草图圆心，共 **24/24 个引用**。六个尺寸绑定也全部恢复成功。

最终原模型测试为 `零件2`：保存进程 PID **14648**，恢复进程 PID **10368**。两个进程分别启动并结束，没有共享进程内 COM 对象或缓存。恢复过程中未重建模型、未改动参数、未使用名称或几何 fallback。

当前活动文档已切回原 `零件2`。额外的自动创建集成测试文档 `零件3` 保留在 SOLIDWORKS 中，其独立 CADState 也已验证。文档均未自动保存为 `.SLDPRT`。

## 实现范围

| 文件 | 职责 |
| --- | --- |
| `CadState.cs` | 数据类：文档身份、四个语义特征、强类型参数、依赖、实体引用、尺寸绑定 |
| `StateCapture.cs` | 对已验证 Phase 2 canonical model 进行初次语义登记，读取当前原生参数与重要实体 |
| `PersistentReferences.cs` | `GetPersistReference3` / `GetObjectByPersistReference3`，Base64 编码、原生状态及对象类型检查 |
| `DocumentIdentityStore.cs` | 通过原生自定义属性记录文档 GUID，检查模型、配置和已保存路径 |
| `StateSerializer.cs` | JSON 保存/加载、固定 Phase 3 schema 检查、同目录临时文件与原子替换 |
| `StateService.cs` | 捕获/保存、独立加载/解析、只读恢复结果输出 |

CLI 新增 `capture-state [path]`、`resolve-state [path]`、`state <path>`。`create-canonical [path]` 在既有 Phase 2 创建成功后自动保存状态。

没有实现自然语言、LLM、Phase 4 参数编辑、通用几何 Validator、UI、拓扑回退恢复或模型事务回滚。初次登记的确定性几何查询用于识别本次对象，恢复阶段不会调用这些选择规则。

## 语义身份和参数

语义身份是 `(Document.Id, SemanticId)`。主 Feature 的显示名称和数值 native ID 仅作诊断信息，实际恢复依据是 CADState 内保存的 native persistent reference。

原 `零件2` 文档 GUID：`9af37689-f21c-4096-af17-63b878455f3d`，配置 `默认`，路径为空（未保存）。模型新增原生文本自定义属性 `CAD_HARNESS_DOCUMENT_ID`；再次捕获复用该 GUID，没有修改几何或尺寸。

| SemanticId | 捕获时 Feature | 原生类型 | 读取的参数 | 依赖 |
| --- | --- | --- | --- | --- |
| `base_plate` | `Extrude.Base` | Extrusion | width=80、height=50、thickness=10 mm | 无 |
| `mounting_hole_seed` | `Hole.MountingSeed` | ICE / extrusion cut | diameter≈8 mm，center=(-20,0) mm，Through All，固定圆心 | base_plate |
| `mounting_holes` | `Pattern.MountingHoles` | LPattern | count=2、spacing=40 mm、方向 +X、GeometryPattern=false | mounting_hole_seed |
| `outer_edge_fillet` | `Fillet.OuterVerticalEdges` | Fillet | radius=3 mm | base_plate、mounting_holes |

依赖图另外记录了 host、pattern_seed、edge_owner、preceding_body_feature 四种边。圆角几何归属基础板，创建历史中的前序实体包含阵列结果，因此这两个关系分别保存。

实际 CADState 位于：

```text
D:\CAD-Harness\artifacts\state\9af37689-f21c-4096-af17-63b878455f3d.json
```

用于审阅的完整快照为 [phase3-state-snapshot.json](phase3-state-snapshot.json)。运行时不会读取这个验证快照，默认使用 `artifacts/state/<document-guid>.json`；指定路径时则使用指定文件。

## Semantic entity references

| SemanticId | 类型 | 数量 | 含义 |
| --- | --- | --- | --- |
| `base_plate.profile_sketch` | Feature | 1 | 宽、高尺寸所属草图 |
| `mounting_hole_seed.profile_sketch` | Feature | 1 | 孔径尺寸所属草图 |
| `mounting_hole_seed.center_point` | SketchPoint | 1 | 孔草图中固定的圆心，供后续编辑定位 |
| `base_plate.top_face` | Face | 1 | 当前最终模型的 +Z 顶面 |
| `base_plate.bottom_face` | Face | 1 | 当前最终模型的 -Z 底面 |
| `mounting_hole_seed.wall_face` | Face | 1 | 种子孔的圆柱壁 |
| `mounting_holes.instance_2.wall_face` | Face | 1 | 第二个孔的圆柱壁 |
| `outer_edge_fillet.rounded_faces` | Face set | 4 | 四处 R3 角部圆柱面 |
| `mounting_holes.direction_edge` | Edge | 1 | 当前最终模型上与阵列 +X 方向共线的水平边 |
| `outer_edge_fillet.vertical_tangent_edges` | Edge set | 8 | 圆角生成后的八条外侧竖直切向边界 |

上述 20 个实体引用加上四个主 Feature 引用，共 24 个；均通过 `GetPersistReference3` 保存为 Base64。

四条原始尖角竖边已被 Phase 2 圆角消耗，不能作为当前最终模型中的四条 Edge 保存。八条最终切向边明确使用另一语义名称。阵列方向边同样记录当前存活的共线边，不宣称它就是已被截短的原始输入边。没有回滚模型去捕获历史拓扑。

## 尺寸绑定的 API 限制

一次性只读 API 探针确认：本机对 Feature、Face、Edge 和 SketchPoint 均能生成并恢复 persistent references；对六个 `IDimension` 的 `GetPersistReference3` 均返回空引用。

因此尺寸不伪造 persistent reference。状态保存其所属对象的语义身份和局部尺寸名，加载后先恢复所属 Feature，再调用 `IFeature.Parameter` 和 `IDimension.GetSystemValue3`：

| 参数语义 | 稳定所属对象 | 局部尺寸名 | 恢复值 |
| --- | --- | --- | --- |
| base_plate.width | base_plate.profile_sketch | D1 | 80 mm |
| base_plate.height | base_plate.profile_sketch | D2 | 50 mm |
| base_plate.thickness | base_plate | D1 | 10 mm |
| mounting_hole_seed.diameter | mounting_hole_seed.profile_sketch | D1 | 7.999999999999998 mm |
| mounting_holes.spacing | mounting_holes | D3 | 40 mm |
| outer_edge_fillet.radius | outer_edge_fillet | D1 | 3 mm |

浮点孔径值对应 Ø8。阵列 count 记录值 2，以及其原生访问路径 `ILinearPatternFeatureData.D1TotalInstances`；本阶段没有写入任何参数。

## 跨进程测试

每个 CLI 调用均为一个新启动的 x64 C# 进程。保存调用已经退出后，才启动恢复调用；SOLIDWORKS 自身保持运行。

| 场景 | 保存/创建进程 PID | 恢复进程 PID | 结果 |
| --- | --- | --- | --- |
| 原 Phase 2 模型初次登记，显式 JSON 路径 | 9544 | 8664 | 24/24 references，6/6 bindings |
| 新 canonical 自动创建并保存默认 GUID 路径 | 24128 | 2340 | 24/24 references，6/6 bindings |
| 原模型再次捕获，默认路径，已有身份及文件复用 | 14648 | 10368 | 24/24 references，6/6 bindings |

最终恢复的四个主 Feature native ID 分别为 83、92、99、101，与捕获记录一致；对顶/底面、孔壁、圆角面及边还输出了恢复对象的当前几何描述，供审阅。

最终恢复汇总：

```json
{
  "success": true,
  "processId": 10368,
  "title": "零件2",
  "resolvedFeatures": 6,
  "resolvedFaces": 8,
  "resolvedEdges": 9,
  "resolvedSketchPoints": 1,
  "resolvedPersistentReferences": 24,
  "resolvedDimensionBindings": 6,
  "nameFallbackUsed": false,
  "geometryFallbackUsed": false,
  "modelModified": false
}
```

所有 native reference status 均为 `0 / swPersistReferencedObject_Ok`。恢复 Feature 包括四个主特征和两个草图。

`state <path>` 也已在普通执行环境中单独反序列化该 JSON，不需要连接 SOLIDWORKS。保存/加载中使用的对象均为普通数据类型，没有序列化 COM 对象或进程内句柄。

## 拒绝与身份测试

| 测试 | 实际结果 |
| --- | --- |
| JSON 副本改成另一合法文档 GUID | DOCUMENT_ID_MISMATCH |
| 在真实新模型 `零件3` 中加载 `零件2` 的状态；两者有相同显示特征名称 | DOCUMENT_ID_MISMATCH，在恢复引用前拒绝 |
| JSON 副本将 base_plate 引用改为 20 个零字节 | PERSISTENT_REFERENCE_UNRESOLVED，native status=1，无回退 |
| JSON 副本删除 base_plate.top_face 项 | STATE_SCHEMA_INVALID，纯磁盘加载阶段拒绝 |
| JSON 副本把四个捕获时特征名称改成不存在的标签 | 仍恢复 24/24 references 与 6/6 bindings，实际 native 特征名正确 |

这些测试修改的是状态文件副本，没有在 SOLIDWORKS 中改名或编辑原模型。不把“修改 JSON 标签后可恢复”描述为已经测试过实际模型改名或拓扑变化。

再次捕获原模型时文档 GUID 不变，`documentIdentityAdded=false`；已存在的同文档默认状态文件通过同目录临时文件和 `File.Replace` 原子替换成功。

完整创建、捕获、恢复和错误证据保存在 [phase3-verification-evidence.json](phase3-verification-evidence.json)。一次性 API 探针与准备程序已移除，不纳入 CLI。

## 验证边界

已验证的是 **控制器程序退出、重新启动，在同一个仍打开的 SOLIDWORKS 模型中恢复**。没有测试关闭/重开 `.SLDPRT` 或重启 SOLIDWORKS；当前模型仍未保存，文档 GUID 目前存在于运行中的文档元数据中。用户后续保存零件时，该自定义属性随零件保存，但本次不宣称文件重开恢复已经通过。

没有测试参数变化后引用是否存活；引用 invalid、suppressed、deleted 时会返回结构化错误，不进行名称或几何 fallback。六个尺寸名是本 canonical 特征内部的局部参数标识，不是通用模型解析器。

首次登记限定于 Phase 2 的规范特征和当前最终拓扑。状态文件的固定版本/字段/引用种类检查是持久化契约检查，不是通用 CAD Validator。Phase 4 参数编辑和 topology recovery 均未实现。
