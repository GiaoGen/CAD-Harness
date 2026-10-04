param([switch]$BuildOnly,[switch]$Live,[switch]$Smoke,[string]$PartTemplate='C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2024\templates\gb_part.prtdot')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$className=if($Smoke){'Phase7FinalSmokeTests'}elseif($Live){'Phase7LiveTests'}else{'BatchTests'}
$output=Join-Path $projectRoot ('bin\CadHarness.'+$className+'.exe')
$arguments=@('/nologo','/target:exe','/platform:x64','/warn:4','/warnaserror+','/optimize+',('/main:CadHarness.Tests.'+$className),('/out:'+$output),'/reference:System.Web.Extensions.dll','/reference:System.Net.Http.dll')
foreach($file in @('SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll')){$arguments+='/reference:'+(Join-Path $projectRoot ('bin\'+$file))}
$arguments+=@(Get-ChildItem (Join-Path $projectRoot 'src\CadHarness.Cli') -Filter '*.cs' -File | ForEach-Object FullName)
$arguments+=Join-Path $projectRoot ('tests\'+$className+'.cs')
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Phase 7 verification tests did not compile.'}
if(-not $BuildOnly){& $output $projectRoot $PartTemplate;if($LASTEXITCODE -ne 0){throw 'Phase 7 verification failed; inspect artifacts/phase7.'}}
