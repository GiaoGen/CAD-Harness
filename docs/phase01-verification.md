# Phase 0 + Phase 1 验证记录

日期：2026-10-03（Asia/Taipei）。

## 环境

- SOLIDWORKS Revision：`32.0.1`。
- 当前文档：`零件1`，Part，配置 `默认`，尚未保存。
- C# x64，Framework 编译器；开启警告作为错误，构建成功。
- 沙箱内 `Marshal.GetActiveObject` 返回 `MK_E_UNAVAILABLE / 0x800401E3`；相同程序在提升权限的执行环境中连接成功。未更换 API 路径。

## Phase 0

实际输出：

```json
{"success":true,"phase":0,"solidWorksRevision":"32.0.1","title":"零件1","path":"","documentType":"Part","configuration":"默认"}
```

## Phase 1

首次空零件检查发现默认容器 `DocsFolder` 未被允许，返回 `PART_NOT_EMPTY`，没有修改模型。只读核对全部默认特征后，补充允许 `DocsFolder`、`EnvFolder` 和 `EqnFolder`。

实际创建输出：

```json
{"success":true,"phase":1,"title":"零件1","featureName":"凸台-拉伸1","featureType":"Extrusion","widthMm":80,"heightMm":50,"thicknessMm":10,"saved":false,"execution":"direct-solidworks-api"}
```

创建后用一次性只读 COM 探针核对以下结果。探针已移除，没有纳入程序或形成 Validator 模块。

| 检查项 | 实际结果 |
| --- | --- |
| 原生草图 | `草图1 / ProfileFeature` |
| 原生拉伸 | `凸台-拉伸1 / Extrusion` |
| 草图宽度尺寸 | 80 mm |
| 草图高度尺寸 | 50 mm |
| 拉伸深度尺寸 | 10 mm |
| 草图约束状态 | `3 / swFullyConstrained`，完全定义 |
| 实体数量 | 1 |
| `GetBodyBox` 近似包围盒尺寸 | 80 × 50 × 10 mm |
| 拉伸错误码 | 0，非警告 |
| 执行后的活动草图 | 无 |
| 尺寸输入提示设置 | 执行前 True，执行后 True，已恢复 |

程序直接完成并返回成功，未提供任何中途点击或尺寸确认。尺寸创建时通过原生设置关闭尺寸输入提示，最终恢复。

对同一文档再次执行 `create-plate`，得到 `PART_NOT_EMPTY`，退出码 1，`partialModelMayRemain: false`，已有板件没有重复创建。

## 验证边界

本次验证覆盖本机版本、标准空零件和正常创建路径，以及对已有实体的拒绝。未验证其他 SOLIDWORKS 版本、非标准模板、无活动文档或装配文档的实机路径，也未故意制造 COM 中断来验证异常恢复。没有实现或宣称自动回滚。

Phase 0 和 Phase 1 已在运行中的 SOLIDWORKS 内实际执行成功；后续阶段未实现。板件保留在当前未保存的零件文档中。
