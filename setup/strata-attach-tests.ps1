# Isolated setup.ps1 attach/receipt verification. Only an ephemeral fake identity server is started.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'strata-common.ps1')
$shell=(Get-Process -Id $PID).Path
$node=Get-Command node.exe -ErrorAction Stop
if ((& $node.Source --version) -notmatch '^v(2[2-9]|[3-9][0-9])\.') { throw 'Node 22+ must already be installed; this test must not download it.' }
$temp=Join-Path ([IO.Path]::GetTempPath()) ('qwen-strata-attach-'+[guid]::NewGuid().ToString('N'))
$install=Join-Path $temp 'Strata 연결 설치'
$statePath=Join-Path $temp 'state.json';$addressPath=Join-Path $temp 'address.json'
$actualLocalAppData=$env:LOCALAPPDATA
$actualSettings=Join-Path $actualLocalAppData 'QwenStatus\settings.json'
$before=if(Test-Path -LiteralPath $actualSettings){(Get-FileHash -LiteralPath $actualSettings).Hash}else{''}
$fixture=$null
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Verify-Model([bool]$Ready){
    $output=& $shell -NoProfile -File (Join-Path $install 'setup\setup.ps1') -Action Verify -InstallRoot $install
    # No gateway is started; overall Verify remains incomplete, even with a valid fake model.
    Assert ($LASTEXITCODE -eq 2) 'Verify unexpectedly succeeded without a queue'
    $checks=$output|ConvertFrom-Json
    Assert ($checks.modelReady -eq $Ready) ('Stored Strata identity/context validation failed: '+($output -join ' '))
}
try {
    New-Item -ItemType Directory -Path $temp|Out-Null
    $env:LOCALAPPDATA=Join-Path $temp 'appdata'
    $prefs=Join-Path $env:LOCALAPPDATA 'QwenStatus\settings.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $prefs) -Force|Out-Null
    [IO.File]::WriteAllText($prefs,'{"unrelatedPreference":"keep-me"}',(New-Object Text.UTF8Encoding $false))
    $prefsBefore=(Get-FileHash -LiteralPath $prefs).Hash
    $state=[ordered]@{health=[ordered]@{service='strata';loaded=$true;model='fixture-sc117-iq3';max_context=32768;images=$true};models=@{data=@(@{id='fixture-sc117-iq3';meta=@{n_ctx=16384}})}}
    Strata-WriteJson $statePath $state
    $fixture=Start-Process -FilePath $node.Source -ArgumentList ((Strata-Quote (Join-Path $PSScriptRoot 'strata-attach-fixture.mjs'))+' '+(Strata-Quote $statePath)+' '+(Strata-Quote $addressPath)) -WorkingDirectory (Split-Path -Parent $PSScriptRoot) -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $temp 'fixture.err.log')
    for($i=0;$i -lt 100 -and -not (Test-Path -LiteralPath $addressPath);$i++){if($fixture.HasExited){throw 'Fake Strata exited before readiness'};Start-Sleep -Milliseconds 50}
    Assert (Test-Path -LiteralPath $addressPath) 'Fake Strata readiness timeout'
    $origin=(Get-Content -LiteralPath $addressPath -Raw|ConvertFrom-Json).origin
    & $shell -NoProfile -File (Join-Path $PSScriptRoot 'setup.ps1') -Action Install -InstallRoot $install -Backend existing -ExistingBackend strata -UpstreamUrl $origin
    Assert ($LASTEXITCODE -eq 0) 'Isolated Strata attach installation failed'
    $receipt=Get-Content -LiteralPath (Join-Path $install 'setup-state.json') -Raw|ConvertFrom-Json
    Assert ($receipt.connection.backend -eq 'strata' -and $receipt.connection.model -ceq 'fixture-sc117-iq3' -and $receipt.connection.contextTokens -eq 16384 -and $receipt.connection.upstream -eq $origin -and $receipt.connection.mode -eq 'existing') 'Attach receipt lost effective model/context/origin/ownership'
    Assert ((Get-FileHash -LiteralPath $prefs).Hash -eq $prefsBefore) 'Attach silently configured the app'
    Assert ((Get-Content -LiteralPath (Join-Path $install 'backend.txt') -Raw).Trim() -eq 'strata') 'Attach selected a legacy backend'
    Verify-Model $true
    $state.health.model='different-model';Strata-WriteJson $statePath $state;Verify-Model $false
    $state.health.model='fixture-sc117-iq3';$state.health.service='foreign';Strata-WriteJson $statePath $state;Verify-Model $false
    $state.health.service='strata';$state.health.max_context=8192;Strata-WriteJson $statePath $state;Verify-Model $false
    $state.health.max_context=32768;$state.health.loaded=$false;Strata-WriteJson $statePath $state;Verify-Model $false
    $state.health.loaded=$true;$state.models.data=@();Strata-WriteJson $statePath $state;Verify-Model $false
    $state.models.data=@(@{id='fixture-sc117-iq3';meta=@{n_ctx=16384}});Strata-WriteJson $statePath $state
    & $shell -NoProfile -File (Join-Path $install 'setup\setup.ps1') -Action Install -InstallRoot $install -Backend existing -ExistingBackend strata -UpstreamUrl $origin -ConfigureApp
    Assert ($LASTEXITCODE -eq 0) 'Explicit isolated attach configuration failed'
    $configured=Get-Content -LiteralPath $prefs -Raw|ConvertFrom-Json
    Assert ($configured.backend -eq 'strata' -and $configured.modelId -ceq 'fixture-sc117-iq3' -and $configured.contextTokens -eq 16384 -and $configured.serverOwnership -eq 'external' -and $configured.serverUrl -eq ($origin+'/') -and $configured.unrelatedPreference -eq 'keep-me') 'Explicit configuration lost identity/context or unrelated settings'
    Assert (-not $receipt.modelStarted -and -not (Test-Path -LiteralPath (Join-Path $install 'gateway-process.json')) -and -not (Test-Path -LiteralPath (Join-Path $install 'strata\process.json')) -and -not (Test-Path -LiteralPath (Join-Path $install 'runtime\gateway\ui-token.txt'))) 'Attach unexpectedly started a gateway/model'
    Write-Output 'PASS: isolated fake Strata attach Install/receipt/Verify, persisted effective model/context, foreign/changed/unloaded identity refusal, opt-in settings preservation, no gateway/model starts'
    $global:LASTEXITCODE=0
} finally {
    $env:LOCALAPPDATA=$actualLocalAppData
    if($fixture -and -not $fixture.HasExited){$fixture.Kill();$fixture.WaitForExit()}
    $after=if(Test-Path -LiteralPath $actualSettings){(Get-FileHash -LiteralPath $actualSettings).Hash}else{''}
    if($temp.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $temp) -match '^qwen-strata-attach-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $temp)){Remove-Item -LiteralPath $temp -Recurse -Force}
    Assert ($before -eq $after) 'Real application settings changed during attach checks'
}
