param([switch]$BuildOnly)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output=Join-Path $projectRoot 'bin\CadHarness.ValidationTests.exe'
$arguments=@('/nologo','/target:exe','/platform:x64','/warn:4','/warnaserror+','/optimize+','/main:CadHarness.Tests.ValidationTests',('/out:'+$output),'/reference:System.Web.Extensions.dll','/reference:System.Net.Http.dll')
foreach($file in @('SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll')){$arguments+='/reference:'+(Join-Path $projectRoot ('bin\'+$file))}
$arguments+=@(Get-ChildItem (Join-Path $projectRoot 'src\CadHarness.Cli') -Filter '*.cs' -File | ForEach-Object FullName)
$arguments+=Join-Path $projectRoot 'tests\ValidationTests.cs'
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Validation tests did not compile.'}
if(-not $BuildOnly){& $output $projectRoot; if($LASTEXITCODE -ne 0){throw 'Validation tests failed.'}}
