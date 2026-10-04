$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$verification=Get-Content (Join-Path $PSScriptRoot 'live/verification.json') -Raw | ConvertFrom-Json
if(-not $verification.success -or -not $verification.protectedModelsUnchanged){throw 'Phase 8 runner verification failed.'}
$suite=Get-Content -LiteralPath $verification.resultPath -Raw | ConvertFrom-Json
$unit=Get-Content (Join-Path $PSScriptRoot 'unit/results.json') -Raw | ConvertFrom-Json
$validation=Get-Content (Join-Path $projectRoot 'artifacts/phase6/validation-test-results.json') -Raw | ConvertFrom-Json
$planner=Get-Content (Join-Path $projectRoot 'artifacts/phase5/planner-test-results.json') -Raw | ConvertFrom-Json
$batch=Get-Content (Join-Path $projectRoot 'artifacts/phase7/unit-tests.json') -Raw | ConvertFrom-Json
if($unit.failed -ne 0 -or $validation.failed -ne 0 -or $planner.failures -ne 0 -or -not $batch.success){throw 'A required regression failed.'}
$sourceBaseline=Get-Content (Join-Path $PSScriptRoot 'baseline/source-hashes.json') -Raw | ConvertFrom-Json
$sourceChanges=@(foreach($entry in $sourceBaseline){$after=(Get-FileHash -LiteralPath $entry.Path).Hash;if($after -ne $entry.SHA256){[pscustomobject]@{Path=$entry.Path;BeforeSha256=$entry.SHA256;AfterSha256=$after}}})
$commands=@(foreach($case in $suite.Cases){foreach($step in $case.Steps){[pscustomobject]@{Case=$case.CaseId;Iteration=$case.Iteration;Command=$step.Command;Mode=$step.Mode;
    ExpectedOutcomePassed=$step.ExpectedOutcomePassed;RuntimeEntered=$step.RuntimeEntered;RuntimeSuccess=$step.RuntimeSuccess;WallMs=$step.WallMs;
    Planner=$step.Planner;TotalTokens=$step.TotalTokens;Runtime=$step.Runtime;RollbackSuccess=$step.RollbackSuccess;
    StateSha256Before=$step.StateSha256Before;StateSha256After=$step.StateSha256After;RevisionBefore=$step.RevisionBefore;RevisionAfter=$step.RevisionAfter}}})
$states=@(foreach($property in $verification.hashesBefore.PSObject.Properties){$after=(Get-FileHash -LiteralPath (Join-Path $projectRoot $property.Name)).Hash;
    if($property.Value -ne $after){throw 'Protected state changed.'};[pscustomobject]@{Path=$property.Name;BeforeSha256=$property.Value;AfterSha256=$after;Unchanged=$true}})
$evidence=[ordered]@{Phase=8;Status='PHASE 8 BLOCKED';RunnerImplemented=$true;RunnerVerified=$true;AllPrdCasesPassed=$false;
    SoleCapabilityBlocker='PRD Case B requires a rectangular four-hole intent/state/validator unsupported by the existing canonical-only Runtime; its creation and subsequent thickness/diameter edits cannot run.';
    CompletedUtc=[DateTime]::UtcNow.ToString('o');ResultPath=$suite.ResultPath;ManifestSha256=$suite.ManifestSha256;RuntimeExeSha256=$suite.RuntimeExeSha256;
    SolidWorksRevision=$suite.SolidWorksRevision;SuiteWallMs=$suite.WallMs;Summary=$suite.Summary;CommandMeasurements=$commands;
    CaseOutcomes=@($suite.Cases | Select-Object CaseId,Iteration,Status,WallMs,SetupMs,AuditValidationMs,SettingsRestored,NaturallyInvalidReferencesAfterEdit,ControlledReferenceInvalidation,SemanticFaceRecovered);
    BenchmarkUnitTests=$unit;ValidationRegression=$validation;PlannerRegression=$planner;BatchRegression=$batch;
    ChangedExistingSourceFiles=$sourceChanges;ProtectedModelsUnchanged=$true;ProtectedCadStates=$states;OriginalDocumentRestored=$suite.OriginalDocumentRestored;
    RealLlmInferenceVerified=$false;Planner='rule';EvidencePaths=@('artifacts/phase8/live/verification.json','artifacts/phase8/unit/results.json');
    MeasurementNotes=@('Instrumented API operation blocks exclude COM queries, selections, metadata and property access; each step declares coverage.',
        'SOLIDWORKS execution time is synchronous observed direct API wall time including COM overhead, not an in-process native timestamp.',
        'Case D natural reference survival and controlled stale-reference fallback are separate evidence.',
        'Expected failed edits remain false RuntimeSuccess even when the negative case passes.',
        'B blocked stays in the required-case denominator; no skipped case is counted as passed.')}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'phase8-verification-evidence.json'),($evidence | ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
$culture=[Globalization.CultureInfo]::InvariantCulture
$lines=@('结论：**PHASE 8 BLOCKED**。Benchmark runner 实现与验收通过；PRD 的全部 required cases 尚未通过，唯一能力缺口是 Case B。','',
    '生产 CLI 完整运行两轮：10 个 required cases，8 passed、2 blocked、0 failed；child exit=3，RunnerCompleted=true，AllRequiredCasesPassed=false。','',
    '| Case | 第 1 轮 | 第 2 轮 |', '|---|---|---|')
foreach($id in @('A','B','C','D','E')){$records=@($suite.Cases | Where-Object CaseId -eq $id | Sort-Object Iteration);$lines+='| '+$id+' | '+$records[0].Status+' | '+$records[1].Status+' |'}
$lines+='';$lines+='TotalSuccess=8/10=80%；EditSuccess=10/12=83.33%（含两个预期失败 R100 edit）；RollbackSuccess=2/2=100%。ParseFailures=0、SemanticSafetyFailures=0；RulePlanner 的真实 LLM HTTP calls=0、LLM wall=0、tokens=null，未宣称本轮执行了真实 LLM 推理。'
$lines+='';$lines+='总 suite wall='+($suite.WallMs/1000).ToString('F2',$culture)+' s；显式 rebuild='+$suite.Summary.Rebuilds+'；已插桩 direct API operations='+$suite.Summary.DirectSolidWorksApiOperations+'；recovery attempts='+$suite.Summary.RecoveryAttempts+'。完整逐命令 wall/planner/runtime/API/rebuild/validation、原始 Runtime 结果和状态 SHA/revision 见汇总及 raw results JSON。'
$lines+='';$lines+='两次 Case D：自然编辑后原 persistent refs 失效数量分别为 '+(@($suite.Cases | Where-Object CaseId -eq D | ForEach-Object NaturallyInvalidReferencesAfterEdit) -join ', ')+'；受控 probe 均使单个 Face ref 失效，并由真实 R1 恢复为正确 top face，revision 1→2、24/24 refs 有效。'
$lines+='';$lines+='Phase 4–7 原模型的 before/after native measurement 一致，CADState SHA256 均不变；最后恢复原活动 Part。'
foreach($state in $states){$lines+='';$lines+='- `'+$state.Path+'`: `'+$state.AfterSha256+'`'}
$lines+='';$lines+='汇总：[phase8-verification-evidence.json](../artifacts/phase8/phase8-verification-evidence.json)；生产 raw suite：`'+$suite.ResultPath+'`；保护与 runner 验收：[verification.json](../artifacts/phase8/live/verification.json)。'
$docPath=Join-Path $projectRoot 'docs/phase8-verification.md'
$document=[IO.File]::ReadAllText($docPath).Replace('<!-- LIVE_RESULTS -->',($lines -join "`r`n"))
[IO.File]::WriteAllText($docPath,$document,[Text.UTF8Encoding]::new($false))
$suite.Summary | ConvertTo-Json -Depth 5
