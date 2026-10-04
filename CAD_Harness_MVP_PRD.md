# CAD Harness MVP — Product Requirements Document

> Version: v0.1  
> Status: Ready for implementation  
> Primary implementation agent: Codex  
> Target CAD: SOLIDWORKS  
> Primary language: C#  
> Application type: Out-of-process controller for SOLIDWORKS  
> MVP input: Text only  
> MVP level: Level 3 — Create + Maintain CAD State + Edit Existing Parametric Model  
> Core principle: **Direct API execution, not UI automation**

---

## 0. One-sentence project definition

Build a small experimental **Parametric CAD Runtime / Harness** that converts a natural-language mechanical modeling intent into a structured parametric plan, executes it directly through the SOLIDWORKS API, maintains its own semantic CAD state, validates the result deterministically, and can later modify the model without re-planning or visually re-observing the entire CAD document.

This is **not** a generic CAD chatbot, not a GUI-driving agent, and not a multimodal CAD generator.

The MVP exists to test one architectural hypothesis:

> A stateful, constrained, deterministic CAD Harness can make LLM-assisted parametric modeling substantially faster, cheaper, more stable, and more editable than a normal step-by-step tool-calling CAD agent.

---

# 1. Why this MVP exists

Current LLMs are already capable of producing simple CAD modeling plans or scripts.

The current bottleneck is not simply model intelligence.

A naive CAD agent often works like this:

```text
LLM
→ choose action
→ execute one CAD operation
→ inspect result
→ LLM again
→ choose next action
→ execute
→ inspect
→ ...
```

This produces:

- repeated LLM latency;
- repeated context reconstruction;
- repeated CAD state inspection;
- unnecessary screenshots / multimodal inference;
- many tool calls;
- poor recovery when CAD topology changes;
- fragile references to faces/edges/features;
- models that may look correct but encode weak design intent.

The MVP will test a different architecture:

```text
Natural language
      ↓
Intent Parser / Planner
      ↓
Structured CAD Plan
      ↓
CAD Harness
 ├─ CAD State
 ├─ Semantic references
 ├─ Rule / constraint layer
 ├─ Direct SOLIDWORKS executor
 ├─ Deterministic validator
 └─ Local recovery
      ↓
SOLIDWORKS
```

The LLM should be used primarily for **understanding intent**, not for micromanaging SOLIDWORKS.

---

# 2. Fundamental project insight

The core problem is NOT:

> How can AI create the same final geometry faster?

The core problem is:

> How can ambiguous user intent be compiled into a stable, editable, verifiable parametric design history?

Two CAD models can have identical geometry but different design intent.

Example:

```text
Two Ø8 holes, spacing 40 mm
```

Possible implementations:

```text
A. Two independent circles in one sketch → Cut
B. One hole → Linear Pattern
C. Hole1 + Hole2 as separate features
```

All three may look identical.

But if the user later says:

```text
Change hole spacing from 40 mm to 50 mm.
```

the correct editable representation matters.

Therefore this MVP must evaluate not only geometric success, but also:

- feature structure;
- semantic ownership;
- parameter editability;
- dependency preservation;
- ability to execute later edits without reconstructing the model.

---

# 3. Hard architectural constraint: NO interactive CAD operation flow

## 3.1 Required user experience

The expected execution behavior is:

```text
User submits command
        ↓
Harness plans
        ↓
SOLIDWORKS briefly processes
        ↓
Complete feature/model appears
```

The user must NOT see an automated version of the manual SOLIDWORKS workflow such as:

```text
Open Extrude PropertyManager
→ click depth box
→ type 10
→ click ✓
→ select face
→ open sketch
→ open dimension dialog
→ wait for confirmation
→ ...
```

The normal path must require **zero user clicks**.

---

## 3.2 Forbidden implementation patterns

Do not use the following as the normal executor:

- `SendKeys`
- mouse automation
- coordinate-based clicking
- UI Automation for modeling commands
- launching a SOLIDWORKS command and waiting for the user to confirm
- PropertyManagerPage as the modeling executor
- screen recognition for ordinary CAD operations
- replaying human menu/toolbar interactions
- requiring manual confirmation of dimension dialogs
- one GUI command per modeling action

Avoid `RunCommand`-style interactive paths when a direct modeling API is available.

A UI may later be added for the user's prompt, logs, diff, or approval, but the CAD operation itself must happen through API calls.

---

## 3.3 Required execution pattern

Use native SOLIDWORKS API objects directly.

Examples of the intended style:

```text
ISketchManager
IFeatureManager
IModelDoc2
IModelDocExtension
IDimension
IFeature
IEntity / IFace2 / IEdge
```

Feature creation should call feature APIs with parameters already supplied.

Dimension edits should modify the dimension object directly, e.g. through `IDimension.SetSystemValue3` / equivalent supported API.

A feature should be created as an API operation, not by opening the same interface a human would use.

---

# 4. Batch / low-visual-update execution

Because this MVP is an out-of-process C# controller, implement an execution scope that minimizes intermediate UI updates.

Conceptually:

```csharp
using (new SolidWorksBatchScope(swApp, model))
{
    // direct API operations
    // no interactive UI
    // minimum redraw / tree refresh
}
// restore all SOLIDWORKS UI state
// rebuild and redraw once
```

The scope should, when supported and safe:

1. set `ISldWorks.CommandInProgress = true`;
2. temporarily disable unnecessary FeatureManager tree updates;
3. temporarily disable model-view graphics updates where appropriate;
4. use fast sketch creation settings where appropriate;
5. execute the operation batch;
6. rebuild once at a meaningful boundary rather than after every primitive;
7. restore every changed SOLIDWORKS state in `finally`;
8. update the tree / redraw once after completion.

Important:

> Never leave SOLIDWORKS in a modified UI/update state after an exception.

This optimization is secondary to correctness. Implement it after the first direct-API vertical slice works.

---

# 5. MVP scope

## 5.1 Included

The MVP supports:

- Windows
- SOLIDWORKS desktop
- active Part document
- C# out-of-process application
- text input
- one part at a time
- millimeter-based user commands
- creation of a constrained subset of parametric parts
- persistent `CADState`
- semantic IDs owned by the Harness
- deterministic result validation
- editing an already-created Harness model
- limited local recovery
- execution metrics and logs

---

## 5.2 Explicitly excluded

Do NOT implement in MVP:

- images
- screenshots
- multimodal input
- engineering-drawing recognition
- photographs → CAD
- voice
- assemblies
- mates
- drawings
- sheet metal
- surfacing
- loft
- sweep
- freeform splines
- arbitrary imported geometry understanding
- arbitrary existing third-party models
- Jev
- fine-tuned models
- autonomous multi-agent orchestration
- a polished UI
- cloud backend
- database server
- user accounts
- collaboration
- generic hundreds-of-tools agent

These are future experiments, not MVP requirements.

---

# 6. Primary supported feature vocabulary

Keep the initial vocabulary deliberately small.

## P0 — required for MVP

### Sketch / profile primitives

- centered rectangle
- rectangle with semantic width / height
- circle
- simple 2D positioning using numeric coordinates
- basic horizontal / vertical / coincident constraints where needed

### Features

- Boss Extrude
- Cut Extrude
- simple Through Hole
- simple Blind Hole
- Linear Pattern
- Fillet
- Chamfer

### Editing

- change extrusion depth
- change width / height
- change hole diameter
- change hole position
- change hole spacing in a pattern
- change pattern count
- change fillet radius
- change chamfer distance

---

## P1 — only after all P0 acceptance tests pass

- Revolve
- Mirror
- circular pattern

Do not allow P1 work to delay P0 validation.

---

# 7. Canonical MVP test part

The first complete vertical slice must use one intentionally simple test object.

## Initial command

```text
Create an 80 mm × 50 mm × 10 mm rectangular plate.
Add two Ø8 mm through holes centered vertically on the plate.
The holes should be symmetric about the plate center and 40 mm apart.
Add 3 mm fillets to the four vertical outer edges.
```

Preferred design intent:

```text
BasePlate
├── Sketch.BaseProfile
├── Extrude.Base
├── Hole.MountingSeed
├── Pattern.MountingHoles
└── Fillet.OuterVerticalEdges
```

The Harness is allowed to choose another representation only if it records why and still supports the required downstream edits.

---

## Required edit commands

After creation, the following commands must work on the same model without rebuilding it from scratch:

```text
Change the hole spacing to 50 mm.
```

```text
Change the plate thickness to 12 mm.
```

```text
Change the hole diameter to 10 mm.
```

```text
Change the fillet radius to 4 mm.
```

The edit path should resolve existing semantic state and change the relevant parametric feature.

---

# 8. System architecture

Use the following conceptual architecture.

```text
┌────────────────────────────┐
│ CLI / minimal local shell  │
└─────────────┬──────────────┘
              │
              ▼
┌────────────────────────────┐
│ Intent Service             │
│ - deterministic parser     │
│ - optional LLM planner     │
└─────────────┬──────────────┘
              │
              ▼
┌────────────────────────────┐
│ CAD Intent / Plan          │
│ strongly typed schema      │
└─────────────┬──────────────┘
              │
              ▼
┌────────────────────────────┐
│ CAD Runtime / Harness      │
│                            │
│  ┌──────────────────────┐  │
│  │ CADState             │  │
│  ├──────────────────────┤  │
│  │ Semantic Resolver    │  │
│  ├──────────────────────┤  │
│  │ Plan Compiler        │  │
│  ├──────────────────────┤  │
│  │ SW Executor          │  │
│  ├──────────────────────┤  │
│  │ Validator            │  │
│  ├──────────────────────┤  │
│  │ Local Recovery       │  │
│  └──────────────────────┘  │
└─────────────┬──────────────┘
              │ COM
              ▼
┌────────────────────────────┐
│ SOLIDWORKS                 │
│ Native Parametric Model    │
└────────────────────────────┘
```

---

# 9. Suggested repository structure

```text
cad-harness/
├─ README.md
├─ docs/
│  ├─ architecture.md
│  └─ benchmark.md
├─ src/
│  ├─ CadHarness.Cli/
│  │  ├─ Program.cs
│  │  └─ Commands/
│  │
│  ├─ CadHarness.Core/
│  │  ├─ Intent/
│  │  │  ├─ CadIntent.cs
│  │  │  ├─ CadOperation.cs
│  │  │  └─ IntentValidation.cs
│  │  │
│  │  ├─ State/
│  │  │  ├─ CadState.cs
│  │  │  ├─ SemanticFeature.cs
│  │  │  ├─ SemanticEntityRef.cs
│  │  │  ├─ DesignDependency.cs
│  │  │  └─ StateSerializer.cs
│  │  │
│  │  ├─ Planning/
│  │  │  ├─ ICadPlanner.cs
│  │  │  ├─ RulePlanner.cs
│  │  │  └─ LlmPlanner.cs
│  │  │
│  │  ├─ Runtime/
│  │  │  ├─ CadRuntime.cs
│  │  │  ├─ PlanCompiler.cs
│  │  │  ├─ SemanticResolver.cs
│  │  │  └─ ExecutionContext.cs
│  │  │
│  │  ├─ Validation/
│  │  │  ├─ ValidationResult.cs
│  │  │  ├─ GeometryValidator.cs
│  │  │  ├─ FeatureValidator.cs
│  │  │  └─ StateValidator.cs
│  │  │
│  │  ├─ Recovery/
│  │  │  ├─ FailureClassifier.cs
│  │  │  ├─ RecoveryPolicy.cs
│  │  │  └─ RecoveryResult.cs
│  │  │
│  │  └─ Metrics/
│  │     ├─ ExecutionMetrics.cs
│  │     └─ RunLogger.cs
│  │
│  └─ CadHarness.SolidWorks/
│     ├─ SolidWorksConnection.cs
│     ├─ SolidWorksBatchScope.cs
│     ├─ SolidWorksExecutor.cs
│     ├─ SolidWorksStateReader.cs
│     ├─ PersistentReferenceStore.cs
│     ├─ Units.cs
│     └─ Features/
│        ├─ SketchBuilder.cs
│        ├─ ExtrudeBuilder.cs
│        ├─ CutBuilder.cs
│        ├─ HoleBuilder.cs
│        ├─ LinearPatternBuilder.cs
│        ├─ FilletBuilder.cs
│        └─ ChamferBuilder.cs
│
├─ tests/
│  ├─ CadHarness.Core.Tests/
│  └─ benchmark-cases/
│
└─ artifacts/
   ├─ runs/
   └─ state/
```

Do not over-engineer solution/project splitting if it slows down the first vertical slice. The conceptual boundaries matter more than exact folder names.

---

# 10. CAD Intent schema

The LLM must NOT emit C# or SOLIDWORKS API calls.

It should emit a constrained structured representation.

Illustrative shape:

```json
{
  "intentVersion": "0.1",
  "mode": "create",
  "units": "mm",
  "partSemanticId": "part.main",
  "operations": [
    {
      "op": "create_base_plate",
      "semanticId": "base_plate",
      "width": 80,
      "height": 50,
      "thickness": 10
    },
    {
      "op": "create_hole_pattern",
      "semanticId": "mounting_holes",
      "host": "base_plate.top_face",
      "holeDiameter": 8,
      "count": 2,
      "spacing": 40,
      "alignment": "horizontal",
      "centered": true,
      "throughAll": true
    },
    {
      "op": "fillet_edges",
      "semanticId": "outer_edge_fillet",
      "target": "base_plate.outer_vertical_edges",
      "radius": 3
    }
  ]
}
```

Important:

- reject unknown operation types;
- validate every numeric range;
- unit conversion occurs in one centralized layer;
- internally normalize length to SI units before SOLIDWORKS API calls;
- never let raw LLM JSON reach COM execution without schema validation.

---

# 11. Edit Intent schema

Example:

```json
{
  "intentVersion": "0.1",
  "mode": "edit",
  "target": "mounting_holes",
  "changes": {
    "spacing": 50
  }
}
```

or:

```json
{
  "intentVersion": "0.1",
  "mode": "edit",
  "target": "base_plate",
  "changes": {
    "thickness": 12
  }
}
```

The edit path must:

```text
text
→ edit intent
→ semantic target resolution
→ existing feature / dimension
→ modify parameter
→ rebuild
→ validate
→ update CADState
```

It must NOT:

```text
text
→ delete entire model
→ regenerate from scratch
```

unless the runtime explicitly reports that a local edit is impossible.

Regeneration is not considered a successful Level-3 edit.

---

# 12. CADState — the central MVP data structure

This is the most important part of the project.

The state should answer:

> What did the Harness create, what does each CAD object mean, how are the features related, and how can I find them again?

Illustrative structure:

```json
{
  "document": {
    "path": "...",
    "configuration": "Default"
  },
  "features": {
    "base_plate": {
      "semanticType": "base_plate",
      "swFeatureName": "Boss-Extrude1",
      "featureType": "extrude",
      "parameters": {
        "width": 80,
        "height": 50,
        "thickness": 10
      },
      "dependencies": []
    },

    "mounting_hole_seed": {
      "semanticType": "through_hole",
      "swFeatureName": "Cut-Extrude1",
      "parameters": {
        "diameter": 8
      },
      "dependencies": [
        "base_plate"
      ]
    },

    "mounting_holes": {
      "semanticType": "linear_pattern",
      "swFeatureName": "LPattern1",
      "parameters": {
        "count": 2,
        "spacing": 40
      },
      "dependencies": [
        "mounting_hole_seed"
      ]
    }
  },

  "semanticEntities": {
    "base_plate.top_face": {
      "kind": "face",
      "owner": "base_plate",
      "persistentReference": "base64..."
    },

    "base_plate.outer_vertical_edges": {
      "kind": "edge_set",
      "owner": "base_plate",
      "persistentReferences": [
        "base64...",
        "base64...",
        "base64...",
        "base64..."
      ]
    }
  }
}
```

This exact JSON format is not mandatory, but the information model is.

---

# 13. Semantic references

Never let later operations depend only on fragile UI-style identifiers such as:

```text
Face<7>
Edge<12>
Sketch3
```

The Harness owns semantic names:

```text
base_plate.top_face
base_plate.bottom_face
base_plate.outer_vertical_edges
mounting_hole_seed.axis
mounting_holes
outer_edge_fillet
```

Map semantic IDs to SOLIDWORKS objects.

Where useful, store SOLIDWORKS persistent reference IDs through `IModelDocExtension` persistent-reference APIs.

Important:

Persistent references solve:

> How do I try to find the same SOLIDWORKS entity later?

Semantic references solve:

> What does this entity mean in this design?

Both layers are required.

---

# 14. Semantic re-resolution fallback

A persistent reference may become invalid after topology changes.

Therefore implement a small fallback resolver for P0 semantics.

Example:

If:

```text
base_plate.top_face
```

cannot be restored using its persistent reference, re-resolve it from deterministic geometric constraints such as:

- planar face;
- normal approximately +Z in the local plate coordinate system;
- highest Z;
- expected area range;
- belongs to body created by `base_plate`;
- not a hole wall.

Do NOT attempt a universal geometric reasoning system.

Only implement enough fallback rules for the canonical P0 features.

---

# 15. Design intent graph

Represent dependencies explicitly.

Example:

```text
BaseProfile
    ↓
BaseExtrude
    ↓
MountingHoleSeed
    ↓
MountingHolePattern

BaseExtrude
    ↓
OuterEdgeFillet
```

Minimum dependency structure:

```csharp
DesignDependency
{
    string ParentSemanticId;
    string ChildSemanticId;
    DependencyKind Kind;
}
```

Use this graph during edits.

Example:

```text
Change plate thickness
```

should mark relevant dependent validations dirty, but should not require the LLM to rediscover every relationship.

---

# 16. Planner strategy

## 16.1 MVP rule

Do not make the LLM the executor.

LLM responsibility:

```text
user language
→ constrained CAD intent
```

Runtime responsibility:

```text
CAD intent
→ implementation plan
→ SOLIDWORKS operations
```

---

## 16.2 Planner interface

Implement a provider abstraction:

```csharp
public interface ICadPlanner
{
    Task<CadIntent> PlanAsync(
        string userCommand,
        CadState currentState,
        CancellationToken cancellationToken);
}
```

Recommended implementations:

```text
RulePlanner
LlmPlanner
```

`RulePlanner` exists so the full CAD runtime can be tested without network/model variability.

`LlmPlanner` should support an OpenAI-compatible endpoint through environment/configuration rather than hard-coding one vendor.

Suggested configuration:

```text
CAD_HARNESS_LLM_BASE_URL
CAD_HARNESS_LLM_API_KEY
CAD_HARNESS_LLM_MODEL
```

Use structured JSON output when available.

---

# 17. Direct SOLIDWORKS executor

The executor must expose domain-level methods rather than hundreds of thin wrappers.

Example:

```csharp
CreateBasePlate(...)
CreateThroughHole(...)
CreateLinearPattern(...)
ApplyFillet(...)
ApplyChamfer(...)
ChangeFeatureParameter(...)
```

Internally those methods can use multiple SOLIDWORKS API calls.

The planner should not know COM details.

Bad:

```text
LLM chooses SelectByID2
LLM chooses InsertSketch
LLM chooses CreateCircle
LLM chooses AddDimension
LLM chooses FeatureCut
```

Good:

```text
Planner:
CreateMountingHolePattern(...)

Runtime:
performs the complete deterministic implementation.
```

---

# 18. Build with SOLIDWORKS native parametric features

Whenever reasonable, prefer real parametric features over geometry hacks.

For example:

```text
hole + linear pattern
```

should be represented as actual editable feature intent where supported, instead of generating disconnected geometry that merely looks equivalent.

MVP evaluation must inspect the resulting FeatureManager tree.

---

# 19. Rebuild policy

Avoid rebuilding after every primitive.

Recommended boundaries:

```text
create complete sketch
→ create feature

complete logically dependent feature batch
→ rebuild / validate

finish transaction
→ final rebuild + redraw
```

Use the lightest rebuild sufficient for correctness.

Record rebuild time separately.

---

# 20. Validator

Validation must NOT ask an LLM whether the model "looks right."

P0 validator checks should include as applicable:

- expected feature exists;
- expected feature type;
- document rebuild status;
- body count;
- bounding box dimensions;
- target dimension value;
- hole diameter;
- hole count;
- spacing;
- pattern count;
- extrusion depth;
- fillet radius;
- chamfer distance;
- through-all vs blind intent when queryable;
- semantic reference can be resolved;
- state and model agree.

Return structured output:

```json
{
  "success": false,
  "checks": [
    {
      "name": "pattern.spacing",
      "expected": 50,
      "actual": 40,
      "status": "failed"
    }
  ],
  "failureClass": "PARAMETER_NOT_APPLIED"
}
```

---

# 21. Limited local recovery

MVP recovery is intentionally narrow.

Implement only these classes first:

### R1 — stale semantic entity reference

Attempt:

```text
persistent reference
→ if invalid
→ semantic geometric re-resolution
```

### R2 — dimension / parameter update did not apply

Attempt:

```text
re-resolve owning dimension
→ set value directly
→ rebuild
→ validate
```

### R3 — feature rebuild failure after parameter edit

Attempt:

```text
rollback last edit
→ return structured failure
```

Do not silently destroy the model.

### R4 — fillet / chamfer geometrically impossible

MVP behavior:

```text
detect failure
→ rollback
→ report requested value invalid
```

Optional P1:

generate a bounded set of smaller candidate values and test them.

### R5 — unsupported / ambiguous intent

Do not invent geometry.

Return:

```text
UNSUPPORTED_INTENT
```

or:

```text
AMBIGUOUS_INTENT
```

with the smallest useful reason.

---

# 22. Transaction and rollback behavior

Each user command should behave as a logical transaction where feasible.

Before mutation:

- capture relevant CADState;
- record operation ID;
- store enough information to restore / undo the operation.

On success:

```text
execute
→ rebuild
→ validate
→ commit CADState
```

On failure:

```text
execute
→ failure
→ rollback / undo
→ restore previous CADState
→ return structured error
```

Do not commit state before validation passes.

---

# 23. Logging and observability

Every run should produce a machine-readable log.

Minimum fields:

```json
{
  "runId": "...",
  "mode": "create",
  "command": "...",
  "planner": {
    "provider": "...",
    "model": "...",
    "calls": 1,
    "latencyMs": 1234,
    "inputTokens": 0,
    "outputTokens": 0
  },
  "runtime": {
    "compileMs": 10,
    "solidWorksExecutionMs": 600,
    "rebuildMs": 120,
    "validationMs": 35,
    "recoveryAttempts": 0
  },
  "result": {
    "success": true
  }
}
```

If token usage is unavailable, use null rather than inventing a number.

---

# 24. CLI contract

A minimal CLI is enough.

Suggested commands:

```text
cad-harness inspect
```

Print current Harness CADState / detected active document.

```text
cad-harness create "<natural language command>"
```

Create a model from a text command.

```text
cad-harness edit "<natural language command>"
```

Edit an existing Harness-managed model.

```text
cad-harness validate
```

Run deterministic validation against current state.

```text
cad-harness state
```

Print saved semantic CAD state.

```text
cad-harness benchmark ./tests/benchmark-cases/mvp.json
```

Run a repeatable benchmark set.

No GUI is required.

---

# 25. Required benchmark cases

Create a small deterministic benchmark suite.

## Case A — canonical plate

Create:

```text
80 × 50 × 10 plate
2 × Ø8 through holes
40 mm spacing
R3 outer vertical fillets
```

Then edit:

```text
spacing 50
thickness 12
hole diameter 10
fillet R4
```

---

## Case B — rectangular mounting plate

Create:

```text
100 × 60 × 8 plate
4 × Ø6 through holes
rectangular symmetric placement
```

Then:

```text
change plate thickness to 10
change hole diameter to 8
```

Do not over-expand feature support solely for this case.

---

## Case C — invalid fillet

Create a valid plate, then request an impossible fillet radius.

Expected:

```text
failure detected
model remains valid
previous state restored
clear structured failure
```

---

## Case D — stale reference recovery

Create a model, perform an edit that causes entity/topology regeneration, then verify that a required semantic face/edge can still be resolved either through persistent reference or fallback semantic resolution.

---

## Case E — unsupported intent

Example:

```text
Create an aerodynamic turbine blade with optimized cooling passages.
```

Expected:

```text
UNSUPPORTED_INTENT
```

No random partial geometry.

---

# 26. Performance instrumentation

Measure:

- total wall-clock time;
- LLM wall-clock time;
- number of LLM calls;
- number of direct SOLIDWORKS API operations;
- number of rebuilds;
- execution time inside SOLIDWORKS;
- validation time;
- recovery attempts;
- total success;
- edit success;
- rollback success.

Do not optimize only "model latency."

The metric that matters is:

```text
user command
→ verified editable CAD result
```

---

# 27. MVP success criteria

The MVP is successful if all of the following are demonstrated.

## Functional

- canonical P0 part can be created;
- no user interaction is required during modeling;
- no PropertyManager confirmation is required;
- output is a real editable SOLIDWORKS parametric model;
- CADState is created and persisted;
- semantic features resolve correctly;
- all four canonical edit commands work without regenerating the whole model;
- deterministic validation correctly verifies the changes;
- at least three known failure classes produce controlled outcomes;
- failed edit leaves the previous model usable.

---

## Architectural

The implementation must demonstrate:

```text
LLM understands intent
but
LLM does not micromanage CAD operations
```

and:

```text
Runtime remembers model state
instead of
re-observing the model after every action
```

and:

```text
Validation uses CAD/API state
instead of
vision / natural-language judgment
```

---

## Performance

No absolute latency target is required for the first build because hardware and LLM provider vary.

However, for the canonical test:

- normal execution should use one planner call for a create instruction;
- ordinary parameter edits should target one planner call or fewer;
- the runtime must not perform an LLM call after every SOLIDWORKS feature;
- the user should not need to confirm any intermediate operation;
- unnecessary screen redraw / tree refresh should be minimized.

A later benchmark should compare this architecture against a naive iterative agent baseline using the same model.

---

# 28. Stop / continue decision

This MVP is explicitly an experiment.

## Strong continue signal

Continue development if:

- create + edit are reliable;
- semantic CADState survives repeated edits;
- most execution is deterministic after initial intent parsing;
- validation catches failures without LLM involvement;
- total latency is clearly dominated by one planning call rather than dozens of agent turns;
- feature structure remains editable and sensible;
- local edits usually avoid complete regeneration.

Then proceed to:

```text
more feature types
→ stronger semantic resolver
→ richer recovery
→ baseline comparison
→ multimodal experiment
→ optional fast decision model / Jev-like layer
```

---

## Weak / negative signal

Re-evaluate the architecture if:

- every small operation still needs model reasoning;
- maintaining CADState is more fragile than directly inspecting SOLIDWORKS;
- semantic references constantly break;
- most edits require full regeneration;
- direct API execution is not materially faster or more reliable than the baseline;
- the Harness becomes hundreds of bespoke handlers without meaningful generalization.

Do not hide these results.

The purpose of the MVP is to discover whether the architecture actually creates leverage.

---

# 29. Implementation order for Codex

Do not build everything in parallel.

## Phase 0 — Environment proof

Goal:

```text
C# process
→ connect to running SOLIDWORKS
→ obtain active Part document
→ print document metadata
```

No LLM.

---

## Phase 1 — Direct execution proof

Hard requirement.

Programmatically create:

```text
80 × 50 × 10 rectangular plate
```

using direct SOLIDWORKS APIs.

Expected user experience:

```text
run command
→ short processing
→ finished plate appears
```

No PropertyManager.

No dimension-confirm dialog.

No manual click.

Do not proceed if this is not achieved.

---

## Phase 2 — Direct complete canonical model

Without an LLM, construct the canonical part through a hard-coded typed `CadIntent`.

This isolates the runtime from language-model errors.

Implement:

```text
base plate
→ hole seed
→ linear pattern
→ fillet
→ rebuild
→ validate
```

---

## Phase 3 — CADState + semantics

After successful creation:

- generate CADState;
- persist semantic features;
- persist important entity references;
- save state to disk;
- reload state;
- resolve the same model objects again.

---

## Phase 4 — Edit existing model

Hard-coded typed edit intents first.

Verify:

```text
spacing 40 → 50
thickness 10 → 12
hole Ø8 → Ø10
fillet R3 → R4
```

No regeneration.

---

## Phase 5 — Text planner

Add:

```text
natural language
→ structured CadIntent
```

First use a deterministic parser for known test patterns.

Then add an optional LLM planner behind `ICadPlanner`.

This order ensures the CAD runtime can always be tested independently from the LLM.

---

## Phase 6 — Validator + rollback

Implement deterministic validation and the required recovery classes.

---

## Phase 7 — Batch execution optimization

Only after correctness:

- `CommandInProgress`;
- reduce tree refresh;
- reduce graphics refresh;
- sketch batch settings where useful;
- reduce rebuild frequency.

Measure before and after.

---

## Phase 8 — Benchmark runner

Execute repeatable benchmark cases and write JSON results.

---

# 30. Coding principles

Codex must follow these rules.

1. Prefer explicit typed models over dictionaries.
2. Keep SOLIDWORKS COM interop isolated from core domain logic.
3. Centralize units.
4. User-facing lengths are mm.
5. SOLIDWORKS system-unit conversion must be explicit.
6. Never silently interpret an unknown operation.
7. Never use UI automation to "make the demo work."
8. Do not introduce screenshots or computer vision.
9. Always restore SOLIDWORKS temporary settings in `finally`.
10. Every mutating operation returns a structured result.
11. Every failure should have a stable error/failure code.
12. Avoid giant `switch` statements where a typed feature handler registry is cleaner.
13. But do not overbuild plugin architecture before the P0 vertical slice works.
14. Feature names are not semantic identity.
15. Persist enough mapping to inspect why an edit targeted a given feature.
16. Prefer deterministic calculation over LLM calls.
17. Prefer bounded failure over plausible-looking wrong geometry.

---

# 31. Important SOLIDWORKS implementation notes

The following API concepts should be evaluated and used appropriately:

- `IModelDoc2.FeatureManager`
- `IFeatureManager`
- direct feature creation methods
- `IDimension.SetSystemValue3`
- `IModelDoc2.EditRebuild3`
- `IModelDocExtension.NeedsRebuild2`
- `IModelDocExtension.GetPersistReference3` / corresponding persistent-reference APIs
- `IModelDocExtension.GetObjectByPersistReference3`
- `ISldWorks.CommandInProgress`
- `IFeatureManager.EnableFeatureTree`
- `IFeatureManager.EnableFeatureTreeWindow`
- `IModelView.EnableGraphicsUpdate`
- `ISketchManager.AddToDB`
- `ISketchManager.DisplayWhenAdded`

Do not assume every API behaves identically across SOLIDWORKS versions. Wrap version-sensitive behavior.

---

# 32. Why previous scripts may have shown interactive UI

A SOLIDWORKS automation script can work in two fundamentally different styles.

## Style A — automate the human workflow

```text
invoke command
→ SOLIDWORKS opens PropertyManager
→ select entities
→ populate fields
→ confirm command
```

This still uses the application's interactive command machinery.

It may cause:

- property pages;
- dimension input dialogs;
- visible selections;
- incremental redraws;
- confirmation requirements.

This is not the target architecture.

---

## Style B — mutate the model through native API objects

```text
create/query objects
→ pass parameters directly
→ create/edit feature
→ rebuild
```

This bypasses the human interaction sequence.

This is the target architecture.

Forge publicly describes its changes as being applied through the native SOLIDWORKS API and its SolidWorks integration is a C# add-in. The MVP should pursue the same broad *direct API execution* property, while implementing a different Harness architecture focused on state, semantics, planning, validation, and recovery.

Do not copy Forge's architecture blindly.

---

# 33. Definition of "one-step application"

"One step" does NOT mean SOLIDWORKS internally performs only one API function.

The Harness may internally run:

```text
20 API calls
```

or:

```text
100 API calls
```

The requirement is that those operations are deterministic local execution, not repeated human-style interactions or repeated LLM round trips.

From the user perspective:

```text
one instruction
→ one execution transaction
→ one verified model update
```

That is the desired UX and performance model.

---

# 34. Expected MVP outcome

The expected first successful demo is intentionally modest.

A terminal session such as:

```text
> cad-harness create "Create an 80 by 50 by 10 mm plate with two 8 mm through holes spaced 40 mm apart and 3 mm outer-edge fillets."

Planner       OK   1 call
Compile       OK
SOLIDWORKS    OK
Rebuild       OK
Validation    PASS
State         saved

Total: 2.4 s
```

SOLIDWORKS should show the finished parametric model without the user clicking through feature dialogs.

Then:

```text
> cad-harness edit "Change the hole spacing to 50 mm."

Target        mounting_holes
Parameter     spacing: 40 → 50
SOLIDWORKS    OK
Rebuild       OK
Validation    PASS
State         updated

Total: 0.4 s
```

The exact timing above is illustrative, not an acceptance threshold.

The important result is the execution shape:

```text
intent
→ semantic target
→ deterministic local mutation
→ deterministic verification
```

rather than:

```text
LLM
→ GUI operation
→ screenshot
→ LLM
→ GUI operation
→ ...
```

---

# 35. What this MVP is expected to prove

If successful, it should prove five things:

1. **Direct CAD execution is fast enough** that SOLIDWORKS itself is not the dominant delay for simple parts.
2. **An LLM does not need to remain in the loop for every feature.**
3. **Semantic CADState can eliminate repeated re-understanding of the model.**
4. **Existing parametric features can be edited from natural language without regenerating the part.**
5. **Deterministic validation can replace visual "did it work?" reasoning for a large part of the workflow.**

It does NOT need to prove:

- arbitrary mechanical intelligence;
- photo-to-CAD;
- production readiness;
- full SolidWorks coverage;
- replacement of professional CAD designers.

---

# 36. Final instruction to Codex

Treat this as an architectural experiment, not a feature-count competition.

The highest-priority milestone is:

> **A hard-coded typed CAD intent creates the canonical part through direct SOLIDWORKS APIs with zero interactive dialogs, saves semantic CADState, and later edits the existing model successfully.**

Do not add LLM complexity until that path works.

Do not add multimodal input.

Do not add Jev.

Do not add a polished GUI.

Do not substitute UI automation when a direct API path is difficult.

If a direct SOLIDWORKS API implementation is blocked, document the exact API limitation and stop that feature rather than hiding the limitation with mouse/keyboard automation.

The MVP is successful when the system begins to behave like a small **CAD runtime/compiler**, not when it merely demonstrates that an LLM can press SOLIDWORKS buttons.
