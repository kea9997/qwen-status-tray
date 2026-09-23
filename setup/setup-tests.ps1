# Exercises common installation in a new temporary directory; never starts a server.
$ErrorActionPreference='Stop'
$shell=(Get-Process -Id $PID).Path
$root=Split-Path -Parent $PSScriptRoot
$temp=Join-Path ([IO.Path]::GetTempPath()) ('qwen-setup-test-'+[guid]::NewGuid().ToString('N'))
$node=Get-Command node.exe -ErrorAction Stop
if ((& $node.Source --version) -notmatch '^v(2[2-9]|[3-9][0-9])\.') { throw 'Tests require Node 22+ already installed; no runtime downloads are allowed.' }
$settings=Join-Path $env:LOCALAPPDATA 'QwenStatus\settings.json'
$before=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings).Hash}else{''}
try {
    $plan=& $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Plan -InstallRoot $temp -Backend existing -Json
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $temp)) { throw 'Plan failed or created files.' }
    $plan=$plan|ConvertFrom-Json
    if ($plan.startsModelServer -or $plan.configureApp) { throw 'Plan defaults change existing services/settings.' }
    & $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Install -InstallRoot $temp -Backend existing
    if ($LASTEXITCODE -ne 0) { throw 'Isolated common installation failed.' }
    foreach ($name in Get-Content -LiteralPath (Join-Path $root 'package-files.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $temp $name) -PathType Leaf)) { throw ('Missing package file: '+$name) }
    }
    $receipt=Get-Content -LiteralPath (Join-Path $temp 'setup-state.json') -Raw|ConvertFrom-Json
    if ($receipt.modelStarted -or $receipt.configuredApp -or (Test-Path -LiteralPath (Join-Path $temp 'gateway-process.json')) -or (Test-Path -LiteralPath (Join-Path $temp 'runtime\gateway\ui-token.txt'))) { throw 'Install unexpectedly started a service or changed settings.' }
    # Adding common components must preserve previously prepared model selections.
    $receipt.models | Add-Member -NotePropertyName ninfer -NotePropertyValue uncensored
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $temp 'setup-state.json') -Encoding UTF8
    & $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Install -InstallRoot $temp -Backend existing
    if ($LASTEXITCODE -ne 0) { throw 'Idempotent common installation failed.' }
    $again=Get-Content -LiteralPath (Join-Path $temp 'setup-state.json') -Raw | ConvertFrom-Json
    if ($again.models.ninfer -ne 'uncensored') { throw 'Common install overwrote prepared model selection.' }
    # Exercise the opt-in settings migration against a private test AppData only.
    $actualLocalAppData=$env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA=Join-Path $temp 'test-appdata'
        $testPrefs=Join-Path $env:LOCALAPPDATA 'QwenStatus'
        New-Item -ItemType Directory -Path $testPrefs -Force | Out-Null
        '{"unrelatedPreference":"keep-me"}' | Set-Content -LiteralPath (Join-Path $testPrefs 'settings.json') -Encoding UTF8
        & $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Install -InstallRoot $temp -Backend existing -ConfigureApp
        if ($LASTEXITCODE -ne 0) { throw 'Explicit configuration failed.' }
        $configured=Get-Content -LiteralPath (Join-Path $testPrefs 'settings.json') -Raw | ConvertFrom-Json
        if ($configured.managedInstallRoot -ne $temp -or $configured.unrelatedPreference -ne 'keep-me' -or $configured.localNodeCommand -ne $node.Source) { throw 'Managed path or existing preference was not preserved.' }
    } finally { $env:LOCALAPPDATA=$actualLocalAppData }
    & $shell -NoProfile -File (Join-Path $temp 'setup\setup.ps1') -Action Verify -InstallRoot $temp
    if ($LASTEXITCODE -ne 2) { throw 'Incomplete connection was not reported as incomplete.' }
    $after=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings).Hash}else{''}
    if ($before -ne $after) { throw 'Existing application settings changed.' }
    Write-Output 'PASS: read-only plan, full common package copy, Node reuse, no model/queue startup, unchanged settings and incomplete-verify exit code'
    $global:LASTEXITCODE=0
} finally {
    $absolute=[IO.Path]::GetFullPath($temp)
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if ($absolute.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $absolute) -match '^qwen-setup-test-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
