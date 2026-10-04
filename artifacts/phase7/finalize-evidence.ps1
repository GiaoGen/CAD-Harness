$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$live=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'live/verification.json') -Raw | ConvertFrom-Json
$smoke=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'final-production-smoke.json') -Raw | ConvertFrom-Json
$unit=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'unit-tests.json') -Raw | ConvertFrom-Json
$validation=Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts/phase6/validation-test-results.json') -Raw | ConvertFrom-Json
$planner=Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts/phase5/planner-test-results.json') -Raw | ConvertFrom-Json
if(-not $live.success -or -not $smoke.success -or -not $unit.success -or $validation.failed -ne 0 -or $planner.failures -ne 0){throw 'A required Phase 7 verification failed.'}
function Get-Median($numbers){$sorted=@($numbers | Sort-Object);if($sorted.Count%2){return [double]$sorted[[int][Math]::Floor($sorted.Count/2)]};return ([double]$sorted[$sorted.Count/2-1]+[double]$sorted[$sorted.Count/2])/2}
$operations=@('create','mounting_holes.spacing','base_plate.thickness','mounting_hole_seed.diameter','outer_edge_fillet.radius')
$comparisons=@(foreach($operation in $operations){
    $baselineSamples=@($live.samples | Where-Object {-not $_.warmup -and $_.mode -eq 'baseline' -and $_.operation -eq $operation})
    $optimizedSamples=@($live.samples | Where-Object {-not $_.warmup -and $_.mode -eq 'optimized' -and $_.operation -eq $operation})
    if($baselineSamples.Count -ne 3 -or $optimizedSamples.Count -ne 3){throw 'Missing paired measured samples.'}
    $baselineWall=Get-Median @($baselineSamples | ForEach-Object wallMs)
    $optimizedWall=Get-Median @($optimizedSamples | ForEach-Object wallMs)
    [pscustomobject]@{Operation=$operation;SamplesPerMode=3;BaselineWallMedianMs=$baselineWall;OptimizedWallMedianMs=$optimizedWall;
        WallReductionPercent=100*($baselineWall-$optimizedWall)/$baselineWall;
        BaselineRebuildMedianMs=$(if($operation -ne 'create'){Get-Median @($baselineSamples | ForEach-Object rebuildMs)}else{$null});
        OptimizedRebuildMedianMs=$(if($operation -ne 'create'){Get-Median @($optimizedSamples | ForEach-Object rebuildMs)}else{Get-Median @($optimizedSamples | ForEach-Object {$_.batch.RebuildMs})});
        BaselineExplicitRebuildCalls=$baselineSamples[0].explicitRebuildCalls;OptimizedExplicitRebuildCalls=$optimizedSamples[0].explicitRebuildCalls;
        RawBaselineWallMs=@($baselineSamples | ForEach-Object wallMs);RawOptimizedWallMs=@($optimizedSamples | ForEach-Object wallMs)}
})
$workflow=@(foreach($mode in @('baseline','optimized')){
    $totals=@(foreach($pair in 1..3){($live.samples | Where-Object {-not $_.warmup -and $_.mode -eq $mode -and $_.pair -eq $pair} | Measure-Object wallMs -Sum).Sum})
    [pscustomobject]@{Mode=$mode;WallMedianMs=(Get-Median $totals);RawWallMs=$totals}
})
$protected=@(foreach($property in $live.protectedHashesBefore.PSObject.Properties){
    $after=(Get-FileHash -LiteralPath (Join-Path $projectRoot $property.Name)).Hash
    if($property.Value -ne $after){throw 'Protected CADState SHA256 changed.'}
    [pscustomobject]@{Path=$property.Name;BeforeSha256=$property.Value;AfterSha256=$after;Unchanged=$true}
})
$baselineHashes=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'baseline/source-hashes.json') -Raw | ConvertFrom-Json
$changed=@(foreach($entry in $baselineHashes){if($entry.Path -like '*.cs'){
    $currentHash=(Get-FileHash -LiteralPath $entry.Path).Hash
    if($entry.SHA256 -ne $currentHash){[pscustomobject]@{Path=$entry.Path;BeforeSha256=$entry.SHA256;AfterSha256=$currentHash}}
}})
$evidence=[ordered]@{Phase=7;Status='PHASE 7 COMPLETE';CompletedUtc=[DateTime]::UtcNow.ToString('o');SolidWorksRevision=$live.solidWorksRevision;
    BatchExecutionVerified=$true;ExactSettingRestorationVerified=$true;RollbackAndAtomicCommitVerified=$true;ProtectedModelsUnchanged=$live.protectedModelsUnchanged;
    ScopeUnitTests=$unit;ValidationRegression=$validation;PlannerRegression=$planner;LiveAcceptanceCases=$live.cases;
    Measurements=$comparisons;WorkflowMeasurements=$workflow;ProtectedCadStates=$protected;ChangedExistingSourceFiles=$changed;
    NewRuntimeSource='src/CadHarness.Cli/SolidWorksBatchScope.cs';BaselineExeSha256=$live.baselineExeSha256;
    MeasurementOptimizedExeSha256=$live.optimizedExeSha256;FinalProductionExeSha256=$smoke.exeSha256;FreshProductionProcessVerified=$smoke.success;
    FinalProductionCommandWallMs=$smoke.commandWallMs;FinalProductionPlateWallMs=$smoke.plateWallMs;FinalProductionPlateBoxMm=$smoke.measuredPlateBoxMm;NoPhase8BenchmarkRunner=$true;
    MeasurementScope=$live.measurement;EvidencePaths=@('artifacts/phase7/live/verification.json','artifacts/phase7/final-production-smoke.json','artifacts/phase7/final-production-validation.json');
    Limitations=@('Three measured paired samples after one warm-up pair; no statistical or cross-machine performance claim.',
        'Baseline create rebuild time is uninstrumented; baseline explicit counts come from source call sites.',
        'Counts exclude native implicit rebuilds/refreshes and rollback outside an already-restored scope.',
        'Final source adds an unconditional outer finally cleanup safety net to Program after measurement build; no batch settings/rebuild/validation behavior changed. Final production build verified in a fresh CLI process.')}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'phase7-verification-evidence.json'),($evidence | ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
$culture=[Globalization.CultureInfo]::InvariantCulture
$lines=@('结论：**PHASE 7 COMPLETE**。17 项 scope 单元测试、34 项 Validator/recovery 回归、76 项 planner 回归通过；实机 fixed acceptance、失败 rollback、设置恢复和最终新进程生产 CLI 验证通过。','',
    '| 操作 | 基线 wall 中位数 (s) | 优化 wall 中位数 (s) | wall 耗时下降 | 显式 rebuild |',
    '|---|---:|---:|---:|---:|')
foreach($item in $comparisons){$lines+='| '+$item.Operation+' | '+($item.BaselineWallMedianMs/1000).ToString('F2',$culture)+' | '+($item.OptimizedWallMedianMs/1000).ToString('F2',$culture)+' | '+$item.WallReductionPercent.ToString('F1',$culture)+'% | '+$item.BaselineExplicitRebuildCalls+' → '+$item.OptimizedExplicitRebuildCalls+' |'}
$lines+='';$lines+='负的下降百分比表示该操作实测变慢。结果保留真实差异，不以 scope/rebuild 时间替代完整命令 wall-clock。各次原始 wall 时间、edit rebuild 中位数和完整 workflow 时间见汇总 JSON。'
$lines+='';$lines+='第三对创建的 wall 时间从基线 19.73 s 增至优化版 32.02 s；该变慢样本计入统计并保留，不能把中位数改善解释为每次调用都更快。'
$lines+='';$lines+='所有 8 次创建与 32 次正常 edit 均通过真实几何验证、revision 和 24/24 引用检查；12 项实机专项检查全部通过。最终生产 CLI 使用独立进程在已验证的 Phase 7 Part 上执行 radius=4 的既有单参数 edit，成功提交并再次验证 24/24 引用。'
$lines+='';$lines+='最终生产版本还通过新进程执行旧 Phase 1 `create-plate`：一次显式 rebuild、单 body、Native GetExtremePoint 确认 80×50×10 mm、NeedsRebuild=0；更新设置和尺寸输入 prompt 原值均恢复。实测 bounding box 与该次 wall-clock 记录在 final-production-smoke.json。'
$lines+='';$lines+='测量可执行文件之后仅补充 `Program` 外层无条件 `finally` 清理兜底；没有改变 batch 设置、重建边界或验证行为。最终源码重新编译并通过新进程生产 CLI 验证；两份二进制 hash 均保留。'
$lines+='';$lines+='保护 CADState SHA256（before = after）：'
foreach($item in $protected){$lines+='';$lines+='- `'+$item.Path+'`: `'+$item.AfterSha256+'`'}
$docPath=Join-Path $projectRoot 'docs/phase7-verification.md'
$document=[IO.File]::ReadAllText($docPath)
$document=$document.Replace('<!-- RESULTS: updated from live evidence after completion -->',($lines -join "`r`n"))
[IO.File]::WriteAllText($docPath,$document,[Text.UTF8Encoding]::new($false))
$comparisons | Select-Object Operation,BaselineWallMedianMs,OptimizedWallMedianMs,WallReductionPercent | Format-Table
