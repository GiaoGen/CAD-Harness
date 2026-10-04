param([switch]$BuildOnly,[string]$PartTemplate,[switch]$Supplement)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output=Join-Path $projectRoot $(if($Supplement){'bin\CadHarness.Phase6SupplementTests.exe'}else{'bin\CadHarness.Phase6LiveTests.exe'})
$arguments=@('/nologo','/target:exe','/platform:x64','/warn:4','/warnaserror+','/optimize+','/main:CadHarness.Tests.Phase6LiveTests',('/out:'+$output),'/reference:System.Web.Extensions.dll','/reference:System.Net.Http.dll')
foreach($file in @('SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll')){$arguments+='/reference:'+(Join-Path $projectRoot ('bin\'+$file))}
$arguments+=@(Get-ChildItem (Join-Path $projectRoot 'src\CadHarness.Cli') -Filter '*.cs' -File | ForEach-Object FullName)
$arguments+=Join-Path $projectRoot 'tests\Phase6LiveTests.cs'
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Phase 6 live tests did not compile.'}
if(-not $BuildOnly){$runArguments=@($projectRoot);if($Supplement){$runArguments+='supplement'}elseif($PartTemplate){$runArguments+=$PartTemplate};& $output @runArguments; if($LASTEXITCODE -ne 0){throw 'Phase 6 live tests failed; inspect artifacts/phase6/live/summary.json and supplement-summary.json.'}}
