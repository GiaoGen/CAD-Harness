param([switch]$BuildOnly,[switch]$Live)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$className=if($Live){'Phase8LiveTests'}else{'BenchmarkTests'}
$output=Join-Path $projectRoot ('bin\CadHarness.'+$className+'.exe')
$arguments=@('/nologo','/target:exe','/platform:x64','/warn:4','/warnaserror+','/optimize+',('/main:CadHarness.Tests.'+$className),('/out:'+$output),'/reference:System.Web.Extensions.dll','/reference:System.Net.Http.dll')
foreach($file in @('SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll')){$arguments+='/reference:'+(Join-Path $projectRoot ('bin\'+$file))}
$arguments+=@(Get-ChildItem (Join-Path $projectRoot 'src\CadHarness.Cli') -Filter '*.cs' -File | ForEach-Object FullName)
$arguments+=Join-Path $projectRoot ('tests\'+$className+'.cs')
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Benchmark tests did not compile.'}
if(-not $BuildOnly){& $output $projectRoot;if($LASTEXITCODE -ne 0){throw 'Benchmark tests failed; inspect artifacts/phase8/unit/results.json.'}}
