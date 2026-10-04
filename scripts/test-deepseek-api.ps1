param(
    [string]$KeyFile,
    [string]$BaseUrl = 'https://api.deepseek.com/v1',
    [string]$Model = 'deepseek-chat'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
function Read-LocalSetting([string]$Name) {
    foreach ($settingScope in @('Process', 'User')) {
        $settingValue = [Environment]::GetEnvironmentVariable($Name, $settingScope)
        if (-not [string]::IsNullOrWhiteSpace($settingValue)) { return $settingValue }
    }
    return $null
}
# Never echo a key or copy it into the persisted verification record.
if ($KeyFile) {
    try { $deepSeekTestKey = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $KeyFile).Path).Trim() }
    catch { throw 'Cannot read the supplied local key file.' }
    if ($deepSeekTestKey -match '^(?:CAD_HARNESS_LLM_API_KEY|DEEPSEEK_API_KEY)\s*=\s*(.+)$') {
        $deepSeekTestKey = $Matches[1].Trim().Trim('"', "'")
    }
}
else {
    $deepSeekTestKey = Read-LocalSetting 'CAD_HARNESS_LLM_API_KEY'
    if (-not $deepSeekTestKey) { $deepSeekTestKey = Read-LocalSetting 'DEEPSEEK_API_KEY' }
}
if ([string]::IsNullOrWhiteSpace($deepSeekTestKey) -or $deepSeekTestKey.Contains("`r") -or $deepSeekTestKey.Contains("`n")) {
    throw 'Set the user-level CAD_HARNESS_LLM_API_KEY or supply a single-key local file. No request was sent.'
}
$settings = @{
    CAD_HARNESS_LLM_BASE_URL = $BaseUrl
    CAD_HARNESS_LLM_MODEL = $Model
    CAD_HARNESS_LLM_API_KEY = $deepSeekTestKey
    CAD_HARNESS_LLM_OUTPUT_MODE = 'json_object'
    CAD_HARNESS_LLM_TIMEOUT_SECONDS = '60'
    CAD_HARNESS_LLM_MAX_OUTPUT_TOKENS = '512'
}
$previousSettings = @{}
$originalStatePath = Join-Path $projectRoot 'artifacts\state\9af37689-f21c-4096-af17-63b878455f3d.json'
$originalStateHashBefore = (Get-FileHash -LiteralPath $originalStatePath -Algorithm SHA256).Hash
$outputPath = Join-Path $projectRoot 'artifacts\phase5\deepseek-real-api-result.json'
try {
    foreach ($name in $settings.Keys) {
        $previousSettings[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    $commandText = [string]::Concat([char]0x5C06,[char]0x5B54,[char]0x8DDD,[char]0x6539,[char]0x4E3A,'50',[char]0x6BEB,[char]0x7C73)
    # Synthetic fixture only. No state-file argument: currentState=null in the HTTP payload.
    # This real API test does not export private CADState or persistent references.
    $plannerOutput = & (Join-Path $projectRoot 'bin\CadHarness.Cli.exe') plan llm $commandText
    $plannerExitCode = $LASTEXITCODE
    $outputText = $plannerOutput -join "`n"
    if ($outputText.Contains($deepSeekTestKey)) { throw 'Unexpected credential in planner output; output was not persisted.' }
    $plannerRecord = $outputText | ConvertFrom-Json
    $originalStateHashAfter = (Get-FileHash -LiteralPath $originalStatePath -Algorithm SHA256).Hash
    $passed = $plannerExitCode -eq 0 -and $plannerRecord.Success -and -not $plannerRecord.RuntimeEntered -and
        $plannerRecord.Planner.Calls -eq 1 -and $plannerRecord.TypedIntent -eq 'ParameterEditIntent' -and
        $plannerRecord.ValidatedIntent.parameter -eq 'mounting_holes.spacing' -and $plannerRecord.ValidatedIntent.valueMm -eq 50 -and
        $originalStateHashBefore -eq $originalStateHashAfter
    $evidence = [ordered]@{
        VerifiedUtc = [DateTime]::UtcNow.ToString('o')
        Provider = 'DeepSeek'
        BaseUrl = $BaseUrl
        Model = $Model
        OutputMode = 'json_object with mandatory local schema/enum/numeric validation'
        RealApi = $true
        InputKind = 'Synthetic fixed test command plus constrained intent schema; currentState=null'
        CadStateSent = $false
        Passed = $passed
        ExitCode = $plannerExitCode
        OriginalStateSha256Before = $originalStateHashBefore
        OriginalStateSha256After = $originalStateHashAfter
        PlannerEvidence = $plannerRecord
    }
    $evidence | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $outputPath -Encoding UTF8
    [ordered]@{
        Passed = $passed
        Model = $Model
        Calls = $plannerRecord.Planner.Calls
        LatencyMs = $plannerRecord.Planner.LatencyMs
        InputTokens = $plannerRecord.Planner.InputTokens
        OutputTokens = $plannerRecord.Planner.OutputTokens
        ValidatedIntent = $plannerRecord.ValidatedIntent
        ErrorCode = $plannerRecord.ErrorCode
        Error = $plannerRecord.Error
        EvidencePath = $outputPath
    } | ConvertTo-Json -Depth 5
    if (-not $passed) { throw 'Real API verification did not pass. See the credential-free evidence record.' }
}
finally {
    foreach ($name in $previousSettings.Keys) { [Environment]::SetEnvironmentVariable($name, $previousSettings[$name], 'Process') }
    $deepSeekTestKey = $null
}
