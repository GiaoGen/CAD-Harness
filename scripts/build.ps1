param([string]$SolidWorksApiDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SolidWorksApiDirectory) {
    $swClsid = (Get-ItemProperty -LiteralPath 'Registry::HKEY_CLASSES_ROOT\SldWorks.Application\CLSID').'(default)'
    $swServer = (Get-ItemProperty -LiteralPath "Registry::HKEY_CLASSES_ROOT\CLSID\$swClsid\LocalServer32").'(default)'
    if ($swServer -notmatch '^\s*"?(.+?\.exe)"?(?:\s.*)?$') { throw 'Cannot resolve the registered SOLIDWORKS executable.' }
    $SolidWorksApiDirectory = Join-Path (Split-Path -Parent $Matches[1]) 'api\redist'
}
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'The .NET Framework x64 C# compiler is unavailable.' }
$interopFiles = @('SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll')
foreach ($file in $interopFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $SolidWorksApiDirectory $file))) { throw "Missing Interop library: $file" }
}
$outputDirectory = Join-Path $projectRoot 'bin'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$compilerArguments = @('/nologo', '/target:exe', '/platform:x64', '/warn:4', '/warnaserror+', '/optimize+',
    ('/out:' + (Join-Path $outputDirectory 'CadHarness.Cli.exe')), '/reference:System.Web.Extensions.dll', '/reference:System.Net.Http.dll')
foreach ($file in $interopFiles) { $compilerArguments += '/reference:' + (Join-Path $SolidWorksApiDirectory $file) }
$compilerArguments += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\CadHarness.Cli') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE" }
foreach ($file in $interopFiles) { Copy-Item -LiteralPath (Join-Path $SolidWorksApiDirectory $file) -Destination $outputDirectory -Force }
Write-Output "Built: $(Join-Path $outputDirectory 'CadHarness.Cli.exe')"
