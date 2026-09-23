param(
    [ValidateSet('Plan','Install','Verify','StartGateway','StopGateway','StartBackend','StopBackend')][string]$Action='Plan',
    [string]$InstallRoot=(Split-Path -Parent $PSScriptRoot),
    [ValidateSet('existing','vllm','ninfer','both')][string]$Backend='existing',
    [ValidateSet('standard','uncensored')][string]$Model='standard',
    [ValidateSet('vllm','ninfer')][string]$ExistingBackend='vllm',
    [string]$HfTokenFile,
    [switch]$IncludeHermes,
    [switch]$ConfigureApp,
    [switch]$Json
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding $false
$OutputEncoding=[Console]::OutputEncoding
$setupShell=(Get-Process -Id $PID).Path
if (-not [IO.Path]::IsPathRooted($InstallRoot)) { throw '설치 폴더는 절대 경로로 지정하세요.' }
$sourceRoot=Split-Path -Parent $PSScriptRoot
$InstallRoot=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ($InstallRoot -eq [IO.Path]::GetPathRoot($InstallRoot).TrimEnd('\')) { throw '드라이브 루트 대신 앱 전용 폴더를 선택하세요.' }
$receiptPath=Join-Path $InstallRoot 'setup-state.json'
$runtimeRoot=Join-Path $InstallRoot 'runtime'
$gatewayRoot=Join-Path $runtimeRoot 'gateway'
$workerRoot=Join-Path $runtimeRoot 'worker'

function Read-Receipt {
    if (Test-Path -LiteralPath $receiptPath) { return Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json }
    return $null
}
function Find-Node {
    $candidates=@((Join-Path $InstallRoot 'dependencies\node\node.exe'))
    $installed=Get-Command node.exe -ErrorAction SilentlyContinue
    if ($installed) { $candidates += $installed.Source }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $version=(& $candidate --version 2>$null)
        if ($LASTEXITCODE -eq 0 -and $version -match '^v(\d+)\.' -and [int]$Matches[1] -ge 22) { return $candidate }
    }
    return $null
}
function Probe([string]$Url) {
    try { $r=Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3; return ($r.StatusCode -eq 200) } catch { return $false }
}
function Backend-Plans {
    foreach ($selected in @(if ($Backend -eq 'both') {@('vllm','ninfer')} elseif ($Backend -eq 'existing') {@()} else {@($Backend)})) {
        $output=& $setupShell -NoProfile -File (Join-Path $sourceRoot 'setup\backend-install.ps1') -Action Plan -Backend $selected -Model $Model -InstallRoot $InstallRoot -Json
        if ($LASTEXITCODE -ne 0) { throw ('백엔드 설치 전 확인 실패: '+$selected+' · '+($output -join [Environment]::NewLine)) }
        $output | ConvertFrom-Json
    }
}
function Emit-Plan {
    $node=Find-Node
    $docker=Get-Command docker.exe -ErrorAction SilentlyContinue
    $plan=[ordered]@{ action='plan'; installRoot=$InstallRoot; backend=$Backend; existingBackend=$ExistingBackend; model=$Model; hermes=[bool]$IncludeHermes;
        node=$(if ($node) {$node} else {'설치 시 공식 Node.js 22 LTS portable 다운로드'});
        docker=$(if ($docker) {'CLI 있음; 실제 엔진/GPU는 백엔드 검사에서 확인'} else {'없음; vLLM/ninfer 설치 전 Docker Desktop 및 GPU 지원 환경 필요'});
        modelServerReady=(Probe 'http://127.0.0.1:18021/health'); queueReady=(Probe 'http://127.0.0.1:18022/queue');
        configureApp=[bool]$ConfigureApp; startsModelServer=$false; downloads='Node와 선택한 모델/백엔드. 모델은 수십 GB 이상이며 준비 공간은 더 필요합니다.';
        manualSteps=@('필요한 경우 Docker Desktop/WSL 설치 및 Windows 재부팅','gated 모델 선택 시 본인 Hugging Face 로그인과 접근 동의','Hermes의 계정/Telegram 설정은 본인이 직접 입력');
        components=@('상태 앱','공통 대기열','MCP worker','선택한 모델 서버','선택한 Hermes'); backendPlans=@(Backend-Plans) }
    $plan | ConvertTo-Json -Depth 12
}
function Copy-SourcePackage {
    if ($sourceRoot -eq $InstallRoot) { return }
    $names=@(Get-Content -LiteralPath (Join-Path $sourceRoot 'package-files.txt') | Where-Object { $_.Trim() })
    foreach ($name in $names) {
        if ([IO.Path]::IsPathRooted($name) -or $name -match '(^|[\\/])\.\.([\\/]|$)|:') { throw '패키지 파일 경로가 잘못됐습니다.' }
        $full=[IO.Path]::GetFullPath((Join-Path $sourceRoot $name))
        if (-not $full.StartsWith($sourceRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $full -PathType Leaf)) { throw ('패키지 파일 없음: '+$name) }
    }
    $backup=Join-Path $InstallRoot ('setup-backups\'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    foreach ($name in $names) {
        $from=Join-Path $sourceRoot $name;$to=Join-Path $InstallRoot $name
        if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { throw ('패키지 파일 없음: '+$name) }
        if (Test-Path -LiteralPath $to) {
            if ((Get-FileHash -LiteralPath $from).Hash -eq (Get-FileHash -LiteralPath $to).Hash) { continue }
            $saved=Join-Path $backup $name;New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
            Copy-Item -LiteralPath $to -Destination $saved
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
        Copy-Item -LiteralPath $from -Destination $to -Force
    }
}
function Install-Node {
    $node=Find-Node;if ($node) { Write-Host ('Node 재사용: '+$node);return $node }
    if (-not [Environment]::Is64BitOperatingSystem) { throw '64비트 Windows가 필요합니다.' }
    [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $base='https://nodejs.org/dist/latest-v22.x/'
    $sums=(Invoke-WebRequest -Uri ($base+'SHASUMS256.txt') -UseBasicParsing -TimeoutSec 30).Content
    $entry=[regex]::Match($sums,'(?m)^([a-f0-9]{64})\s+(node-v22\.\d+\.\d+-win-x64\.zip)\s*$')
    if (-not $entry.Success) { throw '공식 Node.js Windows 압축파일과 체크섬을 찾지 못했습니다.' }
    $file=$entry.Groups[2].Value;$version=([regex]::Match($file,'v22\.\d+\.\d+')).Value
    $deps=Join-Path $InstallRoot 'dependencies';New-Item -ItemType Directory -Path $deps -Force | Out-Null
    $zip=Join-Path $deps $file
    Write-Host ('Node.js '+$version+' 다운로드 및 SHA-256 확인')
    Invoke-WebRequest -Uri ('https://nodejs.org/dist/'+$version+'/'+$file) -OutFile $zip -UseBasicParsing -TimeoutSec 600
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $entry.Groups[1].Value) { throw 'Node.js 체크섬이 다릅니다. 설치를 중단했습니다.' }
    $stage=Join-Path $deps ('extract-'+[guid]::NewGuid().ToString('N'));Expand-Archive -LiteralPath $zip -DestinationPath $stage
    $source=Join-Path $stage ([IO.Path]::GetFileNameWithoutExtension($file));$destination=Join-Path $deps 'node'
    if (Test-Path -LiteralPath $destination) { throw '기존 Node 폴더의 버전을 확인하세요. 자동으로 덮어쓰지 않습니다.' }
    Move-Item -LiteralPath $source -Destination $destination
    return (Join-Path $destination 'node.exe')
}
function Write-Receipt($Values) {
    $temp=$receiptPath+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::WriteAllText($temp,($Values | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $receiptPath) { [IO.File]::Replace($temp,$receiptPath,[System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temp,$receiptPath) }
}
function Configure-Status([string]$Node) {
    $data=Join-Path $env:LOCALAPPDATA 'QwenStatus';New-Item -ItemType Directory -Path $data -Force | Out-Null
    $path=Join-Path $data 'settings.json';$settings=@{}
    if (Test-Path -LiteralPath $path) {
        $existing=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        foreach ($p in $existing.PSObject.Properties) { $settings[$p.Name]=$p.Value }
        Copy-Item -LiteralPath $path -Destination ($path+'.before-setup-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    }
    $settings.serverUrl='http://127.0.0.1:18021/';$settings.queueUrl='http://127.0.0.1:18022/queue';$settings.uiUrl='http://127.0.0.1:18022/ui'
    $settings.gatewayDirectory=$gatewayRoot;$settings.jobsDirectory=Join-Path $workerRoot 'jobs';$settings.localWorkerScript=Join-Path $workerRoot 'server.mjs'
    $settings.backendFile=Join-Path $InstallRoot 'backend.txt'
    $settings.localNodeCommand=$Node;$settings.setupPowerShell=$setupShell;$settings.managedInstallRoot=$InstallRoot
    $temp=$path+'.tmp';[IO.File]::WriteAllText($temp,($settings|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temp,$path,[System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temp,$path) }
    Write-Host '상태 앱 연결 설정을 저장했습니다. 앱을 다시 실행하면 적용됩니다.'
}
function Owned-GatewayProcess {
    $path=Join-Path $InstallRoot 'gateway-process.json';if (-not (Test-Path -LiteralPath $path)) { return $null }
    $saved=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $process=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$saved.pid) -ErrorAction SilentlyContinue
    if ($process -and $process.CommandLine -and $process.CommandLine.Contains((Join-Path $gatewayRoot 'gateway.mjs')) -and $process.ExecutablePath -eq $saved.executable -and $process.CreationDate.ToUniversalTime().ToString('o') -eq $saved.created) { return $process }
    return $null
}
function Start-Gateway {
    if (Owned-GatewayProcess) { Write-Host '이 설치의 대기열이 이미 실행 중입니다.';return }
    if (Get-NetTCPConnection -LocalPort 18022 -State Listen -ErrorAction SilentlyContinue) { throw '18022 포트가 이미 사용 중입니다. 기존 대기열을 중단하거나 덮어쓰지 않았습니다.' }
    $receipt=Read-Receipt;$node=Find-Node;if (-not $node) { throw '먼저 공통 구성 설치를 완료하세요.' }
    $entry=Join-Path $gatewayRoot 'gateway.mjs';if (-not (Test-Path -LiteralPath $entry)) { throw '대기열 파일이 없습니다.' }
    $env:QWEN_APP_ROOT=$InstallRoot
    $env:QWEN_QUEUE_PORT='18022';$env:QWEN_QUEUE_URL='http://127.0.0.1:18022'
    $env:QWEN_GATEWAY_DATA_DIR=$gatewayRoot;$env:QWEN_WORKER_DATA_DIR=$workerRoot;$env:QWEN_WORK_DIR=Join-Path $InstallRoot 'work'
    foreach ($name in @('QWEN_HERMES_PYTHON','QWEN_HERMES_ROOT','QWEN_HERMES_HOME','QWEN_HERMES_GIT_BASH','QWEN_VISION_READY')) { [Environment]::SetEnvironmentVariable($name,$null,'Process') }
    $env:QWEN_MODEL_ID='qwen3.8-27b';$env:QWEN_UPSTREAM_URL='http://127.0.0.1:18021'
    $env:QWEN_BACKEND=Read-Backend
    $env:QWEN_BACKEND_FILE=Join-Path $InstallRoot 'backend.txt'
    $hermesReceipt=Join-Path $InstallRoot 'hermes\install-result.json'
    if (Test-Path -LiteralPath $hermesReceipt) {
        $hermes=Get-Content -LiteralPath $hermesReceipt -Raw | ConvertFrom-Json
        if ($hermes.status -eq 'installed') { foreach ($name in @('QWEN_HERMES_PYTHON','QWEN_HERMES_ROOT','QWEN_HERMES_HOME','QWEN_HERMES_GIT_BASH')) { [Environment]::SetEnvironmentVariable($name,$hermes.environment.$name,'Process') } }
    }
    $log=Join-Path $InstallRoot 'setup-logs';New-Item -ItemType Directory -Path $log -Force | Out-Null
    $process=Start-Process -FilePath $node -ArgumentList ('"'+$entry+'"') -WorkingDirectory $gatewayRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $log 'gateway.out.log') -RedirectStandardError (Join-Path $log 'gateway.err.log')
    $observed=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.Id)
    if (-not $observed) { throw '대기열 프로세스가 시작 직후 종료됐습니다. setup-logs를 확인하세요.' }
    @{pid=$process.Id;executable=$node;created=$observed.CreationDate.ToUniversalTime().ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallRoot 'gateway-process.json') -Encoding UTF8
    $ready=$false
    for ($i=0;$i -lt 8;$i++) { if (Probe 'http://127.0.0.1:18022/queue') {$ready=$true;break};if ($process.HasExited) {break};Start-Sleep -Milliseconds 500 }
    if (-not $ready) { throw '대기열 준비를 확인하지 못했습니다. 기록된 프로세스를 다시 시작하지 말고 setup-logs를 확인하세요.' }
    Write-Host '공통 대기열 준비됨: http://127.0.0.1:18022/v1'
}
function Read-Backend {
    if ($Backend -in @('vllm','ninfer')) { return $Backend }
    $file=Join-Path $InstallRoot 'backend.txt';if (Test-Path -LiteralPath $file) { $selected=(Get-Content -LiteralPath $file -Raw).Trim();if ($selected -in @('vllm','ninfer')) {return $selected} }
    return $ExistingBackend
}
function Call-Installer([string]$Script,[string[]]$Arguments) {
    & $setupShell -NoProfile -File (Join-Path $InstallRoot ('setup\'+$Script)) @Arguments
    if ($LASTEXITCODE -ne 0) { throw ($Script+' 종료 코드 '+$LASTEXITCODE+' · 위 로그의 조치를 확인하세요.') }
}
function Call-Backend([string]$Operation,[string]$Selected) {
    $parameters=@('-Action',$Operation,'-Backend',$Selected,'-InstallRoot',$InstallRoot,'-Model',$Model)
    if ($HfTokenFile) { $parameters+=@('-HfTokenFile',$HfTokenFile) }
    Call-Installer 'backend-install.ps1' $parameters
}
# Start/stop uses the installed selection unless the user explicitly supplies a model.
if ($Action -in @('StartBackend','StopBackend') -and -not $PSBoundParameters.ContainsKey('Model')) {
    $receipt=Read-Receipt
    $selected=Read-Backend
    if ($receipt -and $receipt.PSObject.Properties['models'] -and $receipt.models.PSObject.Properties[$selected]) { $Model=$receipt.models.$selected }
    elseif ($receipt -and $receipt.model -in @('standard','uncensored')) { $Model=$receipt.model }
}
if ($Action -eq 'Plan') { Emit-Plan;exit 0 }
if ($Action -eq 'Verify') {
    $checks=[ordered]@{node=[bool](Find-Node);gatewaySource=(Test-Path -LiteralPath (Join-Path $gatewayRoot 'gateway.mjs'));workerSource=(Test-Path -LiteralPath (Join-Path $workerRoot 'server.mjs'));modelReady=(Probe 'http://127.0.0.1:18021/health');queueReady=(Probe 'http://127.0.0.1:18022/queue');recordFolder=(Test-Path -LiteralPath (Join-Path $gatewayRoot 'requests'));uiTokenPresent=(Test-Path -LiteralPath (Join-Path $gatewayRoot 'ui-token.txt'))}
    $checks | ConvertTo-Json
    if (-not $checks.node -or -not $checks.gatewaySource -or -not $checks.workerSource -or -not $checks.modelReady -or -not $checks.queueReady -or -not $checks.uiTokenPresent) { exit 2 };exit 0
}
if ($Action -eq 'StartGateway') { Start-Gateway;exit 0 }
if ($Action -eq 'StopGateway') {
    $owned=Owned-GatewayProcess;if (-not $owned) { throw '이 설치에서 시작한 대기열 프로세스가 확인되지 않아 종료하지 않았습니다.' }
    Stop-Process -Id $owned.ProcessId;Write-Host '이 설치의 공통 대기열을 종료했습니다.';exit 0
}
if ($Action -eq 'StartBackend') {
    $selected=Read-Backend
    # Commit the mode only after the requested backend Start succeeds.
    Call-Backend 'Start' $selected
    Set-Content -LiteralPath (Join-Path $InstallRoot 'backend.txt') -Value $selected -Encoding ASCII
    Start-Gateway;exit 0
}
if ($Action -eq 'StopBackend') { Call-Backend 'Stop' (Read-Backend);exit 0 }

$plans=@(Backend-Plans)
foreach ($plan in $plans) { if (-not $plan.supported) { throw ('지원하지 않는 조합: '+$plan.backend+' / '+$plan.model+' · '+$plan.unsupportedReason) } }
if ($Model -eq 'uncensored' -and $Backend -ne 'existing' -and (-not $HfTokenFile -or -not (Test-Path -LiteralPath $HfTokenFile -PathType Leaf))) { throw '접근 동의 후 Hugging Face 토큰 파일을 선택하세요. 토큰 내용을 채팅이나 로그에 붙여넣지 마세요.' }
# Install changes only the named destination and, when explicitly selected, app connection settings.
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
$lockPath=Join-Path $InstallRoot 'setup.lock'
try { $installLock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) } catch { throw '이 폴더의 설치가 이미 실행 중입니다. 기존 설치 창을 확인하세요.' }
try {
    Write-Host '[1/5] 앱과 공통 구성 파일 준비';Copy-SourcePackage
    Write-Host '[2/5] Node.js 확인';$node=Install-Node
    foreach ($entry in @((Join-Path $gatewayRoot 'gateway.mjs'),(Join-Path $workerRoot 'server.mjs'))) { & $node --check $entry;if ($LASTEXITCODE -ne 0) { throw ('런타임 구문 검사 실패: '+$entry) } }
    New-Item -ItemType Directory -Path (Join-Path $InstallRoot 'work') -Force | Out-Null
    $previous=Read-Receipt;$models=@{}
    if ($previous -and $previous.PSObject.Properties['models']) { foreach ($p in $previous.models.PSObject.Properties) { $models[$p.Name]=$p.Value } }
    $record=[ordered]@{version=2;status='partial';models=$models;installedAt=(Get-Date).ToUniversalTime().ToString('o');node=$node;backend=$Backend;model=$Model;hermes=(Test-Path -LiteralPath (Join-Path $InstallRoot 'hermes\install-result.json'));configuredApp=[bool]$ConfigureApp;modelStarted=$false}
    Write-Receipt $record
    if ($Backend -eq 'existing' -and -not (Test-Path -LiteralPath (Join-Path $InstallRoot 'backend.txt'))) { Set-Content -LiteralPath (Join-Path $InstallRoot 'backend.txt') -Value $ExistingBackend -Encoding ASCII }
    if ($ConfigureApp) { Configure-Status $node }
    Write-Host '[3/5] 선택한 모델 서버 준비 (설치 후 자동 시작하지 않음)'
    $selectedBackends=@(if ($Backend -eq 'both') {@('vllm','ninfer')} elseif ($Backend -eq 'existing') {@()} else {@($Backend)})
    foreach ($selected in $selectedBackends) {
        Call-Backend 'Install' $selected
        $models[$selected]=$Model;Write-Receipt $record
        if (-not (Test-Path -LiteralPath (Join-Path $InstallRoot 'backend.txt'))) { Set-Content -LiteralPath (Join-Path $InstallRoot 'backend.txt') -Value $selected -Encoding ASCII }
    }
    Write-Host '[4/5] 선택한 Hermes 설치'
    if ($IncludeHermes) { Call-Installer 'hermes-install.ps1' @('-Action','Install','-InstallRoot',$InstallRoot);$record.hermes=$true;Write-Receipt $record }
    Write-Host '[5/5] 연결 정보 기록'
    $record.status='installed';Write-Receipt $record
    Write-Host '설치 단계 완료. 서버 켜기는 사용자 동작입니다. 연결 검증 후에만 사용 준비 완료로 판단하세요.'
} finally { $installLock.Dispose() }
