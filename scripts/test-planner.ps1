param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testOutput = Join-Path $projectRoot 'bin\CadHarness.PlannerTests.exe'
$arguments = @('/nologo','/target:exe','/platform:x64','/warn:4','/warnaserror+','/optimize+',
    '/main:CadHarness.Tests.PlannerTests',('/out:'+$testOutput),'/reference:System.Web.Extensions.dll','/reference:System.Net.Http.dll')
foreach ($file in @('SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll')) { $arguments += '/reference:'+(Join-Path $projectRoot ('bin\'+$file)) }
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\CadHarness.Cli') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$arguments += Join-Path $projectRoot 'tests\PlannerTests.cs'
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'Planner tests did not compile.' }
if (-not $BuildOnly) {
    & $testOutput $projectRoot
    if ($LASTEXITCODE -ne 0) { throw 'Planner tests failed; see artifacts/phase5/planner-test-results.json.' }
}
