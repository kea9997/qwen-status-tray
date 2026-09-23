#requires -Version 5.1
# Contract checks only: no Install/Start/Stop action, Docker operation or model download.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$entry = Join-Path $PSScriptRoot 'backend-install.ps1'
$parseErrors = $null
$tokens = $null
$null = [Management.Automation.Language.Parser]::ParseFile($entry,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors -join "`n") }
$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
$shell = if ($pwsh) {$pwsh.Source} else {(Get-Command powershell).Source}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('qwenstatus-plan-' + [guid]::NewGuid().ToString('N'))
$cases = @(
    @{Backend='vllm';Model='standard';Arch='Ada';Context=65536;Supported=$true;Commit='231592a7009d0d7c876189f7d1f72d8cc791c76c'},
    @{Backend='ninfer';Model='standard';Arch='Ada';Context=229376;Supported=$true;Commit='1bd56c9a1bdf457c6188391a9385d44d86e953aa'},
    @{Backend='ninfer';Model='standard';Arch='Blackwell';Context=229376;Supported=$true;Commit='9e163eee4b8acec21ab0ac765107b6a3f287b217'},
    @{Backend='ninfer';Model='uncensored';Arch='Blackwell';Context=229376;Supported=$true;Commit='9e163eee4b8acec21ab0ac765107b6a3f287b217'},
    @{Backend='vllm';Model='uncensored';Arch='Ada';Context=65536;Supported=$false;Commit='231592a7009d0d7c876189f7d1f72d8cc791c76c'}
)
foreach ($case in $cases) {
    $raw = & $shell -NoProfile -File $entry -Action Plan -Backend $case.Backend -Model $case.Model -GpuArchitecture $case.Arch -InstallRoot $testRoot -Json
    if ($LASTEXITCODE -ne 0) { throw 'Plan failed.' }
    $plan = $raw | ConvertFrom-Json
    if ($plan.supported -ne $case.Supported -or $plan.contextTokens -ne $case.Context -or $plan.sourceCommit -ne $case.Commit) { throw 'Wrong support/source/context plan.' }
    if (-not $plan.readOnly -or $plan.installStartsServer) { throw 'Plan/install lifecycle contract failed.' }
    $service = $plan.compose.services.backend
    if ($service.ports[0].host_ip -ne '127.0.0.1' -or $service.ports[0].published -ne '18021' -or $service.restart -ne 'no') { throw 'Network/restart contract failed.' }
    if ($service.PSObject.Properties.Name -contains 'depends_on') { throw 'Start must not implicitly prepare a model.' }
    if ($case.Backend -eq 'vllm' -and ($service.environment.PREPARE -ne '0' -or $service.environment.SPEC -ne 'dflash2' -or $service.environment.MAX_LEN -ne '65536')) { throw 'vLLM fast/start separation contract failed.' }
    foreach ($pin in $plan.profile.baseImages.PSObject.Properties) { if ($pin.Value -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Invalid base image digest.' } }
}
if (Test-Path -LiteralPath $testRoot) { throw 'Plan unexpectedly created its installation directory.' }
Write-Host 'PASS: PowerShell parse and 5 read-only backend plan contracts; no Docker/build/download/start performed.'
