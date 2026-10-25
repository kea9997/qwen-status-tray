# Exercises common Strata installation with a fake identity server; never starts a model/queue.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'strata-common.ps1')
$shell=(Get-Process -Id $PID).Path
$root=Split-Path -Parent $PSScriptRoot
$temp=Join-Path ([IO.Path]::GetTempPath()) ('qwen-setup-test-'+[guid]::NewGuid().ToString('N'))
$install=Join-Path $temp 'installed'
$node=Get-Command node.exe -ErrorAction Stop
if ((& $node.Source --version) -notmatch '^v(2[2-9]|[3-9][0-9])\.') { throw 'Tests require Node 22+ already installed; no runtime downloads are allowed.' }
$settings=Join-Path $env:LOCALAPPDATA 'QwenStatus\settings.json'
$before=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings).Hash}else{''}
$fixture=$null
try {
    $plan=& $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Plan -InstallRoot $install -Json
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $temp)) { throw 'Plan failed or created files.' }
    $plan=$plan|ConvertFrom-Json
    if ($plan.startsModelServer -or $plan.configureApp -or $plan.existingBackend -ne 'strata' -or $plan.connection.model) { throw 'Plan defaults change services/settings or fabricate a model.' }
    foreach ($legacy in @('vllm','ninfer','both')) {
        $output=& $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Plan -InstallRoot $install -Backend $legacy 2>&1
        if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $temp)) { throw 'Unsupported backend was accepted or wrote files.' }
    }
    New-Item -ItemType Directory -Path $temp|Out-Null
    $statePath=Join-Path $temp 'state.json';$addressPath=Join-Path $temp 'address.json'
    Strata-WriteJson $statePath @{health=@{service='strata';loaded=$true;model='fixture-sc117-iq3';max_context=32768};models=@{data=@(@{id='fixture-sc117-iq3';meta=@{n_ctx=32768}})}}
    $fixture=Start-Process -FilePath $node.Source -ArgumentList ((Strata-Quote (Join-Path $PSScriptRoot 'strata-attach-fixture.mjs'))+' '+(Strata-Quote $statePath)+' '+(Strata-Quote $addressPath)) -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $temp 'fixture.err.log')
    for($i=0;$i -lt 100 -and -not (Test-Path -LiteralPath $addressPath);$i++){if($fixture.HasExited){throw 'Fake Strata exited before readiness'};Start-Sleep -Milliseconds 50}
    if (-not (Test-Path -LiteralPath $addressPath)) { throw 'Fake Strata readiness timeout' }
    $origin=(Get-Content -LiteralPath $addressPath -Raw|ConvertFrom-Json).origin
    & $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Install -InstallRoot $install -Backend existing -UpstreamUrl $origin
    if ($LASTEXITCODE -ne 0) { throw 'Isolated common installation failed.' }
    foreach ($name in Get-Content -LiteralPath (Join-Path $root 'package-files.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $install $name) -PathType Leaf)) { throw ('Missing package file: '+$name) }
    }
    $receiptPath=Join-Path $install 'setup-state.json'
    $receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
    if ($receipt.modelStarted -or $receipt.configuredApp -or (Test-Path -LiteralPath (Join-Path $install 'gateway-process.json')) -or (Test-Path -LiteralPath (Join-Path $install 'runtime\gateway\ui-token.txt'))) { throw 'Install unexpectedly started a service or changed settings.' }
    # Previously stored labels are history, not a selectable engine; preserve them verbatim.
    $receipt.models | Add-Member -NotePropertyName historicalEngine -NotePropertyValue 'keep-prior-selection'
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding UTF8
    & $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Install -InstallRoot $install -Backend existing -UpstreamUrl $origin
    if ($LASTEXITCODE -ne 0) { throw 'Idempotent common installation failed.' }
    $again=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($again.models.historicalEngine -ne 'keep-prior-selection') { throw 'Common install overwrote historical metadata.' }
    $actualLocalAppData=$env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA=Join-Path $temp 'test-appdata'
        $testPrefs=Join-Path $env:LOCALAPPDATA 'QwenStatus'
        New-Item -ItemType Directory -Path $testPrefs -Force | Out-Null
        '{"unrelatedPreference":"keep-me"}' | Set-Content -LiteralPath (Join-Path $testPrefs 'settings.json') -Encoding UTF8
        & $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Install -InstallRoot $install -Backend existing -UpstreamUrl $origin -ConfigureApp
        if ($LASTEXITCODE -ne 0) { throw 'Explicit configuration failed.' }
        $configured=Get-Content -LiteralPath (Join-Path $testPrefs 'settings.json') -Raw | ConvertFrom-Json
        if ($configured.managedInstallRoot -ne $install -or $configured.unrelatedPreference -ne 'keep-me' -or $configured.localNodeCommand -ne $node.Source -or $configured.backend -ne 'strata' -or $configured.modelId -ne 'fixture-sc117-iq3') { throw 'Managed path, verified identity or existing preference was not preserved.' }
        if (@(Get-ChildItem -LiteralPath $testPrefs -Filter 'settings.json.before-setup-*').Count -ne 1) { throw 'Explicit settings backup missing.' }
    } finally { $env:LOCALAPPDATA=$actualLocalAppData }
    & $shell -NoProfile -File (Join-Path $install 'setup\setup.ps1') -Action Verify -InstallRoot $install
    if ($LASTEXITCODE -ne 2) { throw 'Incomplete queue connection was not reported as incomplete.' }
    # An old selection must not be silently overwritten, even when explicitly requesting Strata.
    $selection=Join-Path $install 'backend.txt';[IO.File]::WriteAllText($selection,'ninfer')
    $receiptHash=(Get-FileHash -LiteralPath $receiptPath).Hash
    $output=& $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Install -InstallRoot $install -Backend strata 2>&1
    if ($LASTEXITCODE -eq 0 -or [IO.File]::ReadAllText($selection) -ne 'ninfer' -or (Get-FileHash -LiteralPath $receiptPath).Hash -ne $receiptHash) { throw 'Legacy installation was dispatched or modified.' }
    $after=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings).Hash}else{''}
    if ($before -ne $after) { throw 'Existing application settings changed.' }
    Write-Output 'PASS: read-only Strata defaults; unsupported selections refused; full package copy; Node reuse; no model/queue startup; history/settings preserved with opt-in backup; incomplete Verify; no legacy migration'
    $global:LASTEXITCODE=0
} finally {
    if($fixture -and -not $fixture.HasExited){$fixture.Kill();$fixture.WaitForExit()}
    $absolute=[IO.Path]::GetFullPath($temp)
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if ($absolute.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $absolute) -match '^qwen-setup-test-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
