param(
    [ValidateSet('Plan','Install','Verify','Start','Stop')][string]$Action='Plan',
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [ValidateSet('IQ3_XXS','IQ2_XS')][string]$Profile='IQ3_XXS',
    [string]$StrataSource,[string]$StrataPython,[string]$StrataConfig,[string]$EngineSha256,
    [int]$GpuIndex=-1,[int]$ContextTokens=262144,[int]$PoolWorkers=10,[double]$ResidentBudgetGiB=26,[string]$Prefill='',
    [switch]$Json
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding $false
. (Join-Path $PSScriptRoot 'strata-common.ps1')
$InstallRoot=Strata-FullPath $InstallRoot
if ($InstallRoot -eq [IO.Path]::GetPathRoot($InstallRoot).TrimEnd('\')) { throw '앱 전용 폴더를 선택하세요.' }
$pins=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backends.json') -Raw|ConvertFrom-Json).strata
$profileData=Strata-Profile $pins $Profile $ContextTokens $PoolWorkers $ResidentBudgetGiB $Prefill
$ownedRoot=Join-Path $InstallRoot 'strata';$receiptPath=Join-Path $ownedRoot 'install.json';$configPath=Join-Path $ownedRoot 'server.json';$processPath=Join-Path $ownedRoot 'process.json'
function Read-Prepared([switch]$Integrity,[switch]$LaunchIntegrity) {
    if (-not (Test-Path -LiteralPath $receiptPath)) { throw '먼저 Strata 준비를 완료하세요. 기존 연결 서버는 이 설치에서 제어하지 않습니다.' }
    $r=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
    if ($r.root -ne $InstallRoot -or $r.backend -ne 'strata' -or $r.config -ne $configPath -or $r.sourceCommit -ne $pins.commit -or $r.engineVersion -ne $pins.engineVersion) { throw '설치 기록의 경로·백엔드·버전이 다릅니다.' }
    # Status/Stop need the receipt and exact process ownership, not multi-GB MTP rehashes.
    # Explicit Install revalidates everything; a new Start checks only launch control files.
    foreach ($artifact in $r.artifacts) {
        if ($Integrity -or ($LaunchIntegrity -and $artifact.path -in @($r.python,$r.engine,$r.entry,$configPath))) { $null=Strata-Hash $artifact.path $artifact.sha256 }
    }
    return $r
}
function Owned-Server {
    if (-not (Test-Path -LiteralPath $processPath)) { return $null }
    $saved=Get-Content -LiteralPath $processPath -Raw|ConvertFrom-Json
    $process=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$saved.pid) -ErrorAction SilentlyContinue
    if (Strata-Owned $saved $process $InstallRoot $configPath) { return $saved }
    return $null
}
function Assert-Idle {
    $m=Strata-Get 'http://127.0.0.1:18021' '/metrics'
    if ($m.live.state -notin @('idle','unloaded') -or $null -eq $m.live.queued -or $m.live.queued -ne 0 -or ($null -ne $m.live.running -and $m.live.running -ne 0)) { throw 'Strata가 준비/작업/대기 중입니다. 안전한 종료·전환을 위해 유휴 상태를 기다리세요.' }
    if (Get-NetTCPConnection -LocalPort 18022 -State Listen -ErrorAction SilentlyContinue) {
        $q=Strata-Get 'http://127.0.0.1:18022' '/queue'
        if ($null -eq $q.running -or $null -eq $q.waiting -or $q.running -ne 0 -or $q.waiting -ne 0 -or $q.ui_running -ne 0) { throw '공통 대기열이 사용 중이거나 확인되지 않습니다.' }
    }
}
if ($Action -eq 'Plan') {
    [ordered]@{backend='strata';supported=$true;mode='verified-installed-prerequisite';installRoot=$InstallRoot;profile=$profileData;sourceCommit=$pins.commit;engineVersion=$pins.engineVersion;modelRevision=$pins.model.revision;startsModelServer=$false;downloads=$false;nativeWindows=$true;dockerRequired=$false;port=18021;minimumRamGiB=$pins.minimumRamGiB;minimumVramMiB=$pins.minimumVramMiB;minimumFreeGiB=$pins.minimumFreeGiB;prerequisites=@('공식 원본의 고정 커밋 Strata 소스','CUDA 13 의존성이 설치된 전용 Python 3.10+ 가상환경의 python.exe','공식 0.1.48 엔진 및 별도로 검증한 strata.exe SHA256','고정 리비전 SC117 두 GGUF와 이 파일에서 준비한 pack/tokenizer/MTP 설정','GPU index 명시; Install은 모델 파일을 SHA256 검사하므로 오래 걸릴 수 있음');automaticEngineAcquisition=$false;note=$pins.profileNote} | ConvertTo-Json -Depth 10
    exit 0
}
if ($Action -eq 'Verify') {
    $r=Read-Prepared;$ready=$false;$state='prepared';$reason=$null
    try { $health=Strata-Get 'http://127.0.0.1:18021' '/health';$models=Strata-Get 'http://127.0.0.1:18021' '/v1/models';$identity=Strata-Identity $health $models $r.model;$ready=$identity.loaded -and $identity.contextTokens -eq $r.profile.contextTokens;$state=if($ready){'loaded'}else{'running-unloaded'} } catch { $reason=$_.Exception.Message }
    @{prepared=$true;ownedRunning=[bool](Owned-Server);loaded=$ready;state=$state;reason=$reason;model=$r.model;profile=$r.profile} | ConvertTo-Json -Depth 8
    if (-not $ready) { exit 2 };exit 0
}
if ($Action -in @('Start','Stop')) {
    $r=Read-Prepared;$saved=Owned-Server
    if ($Action -eq 'Stop') {
        if (-not $saved) { throw 'PID·생성 시각·실행 파일·설정·설치 경로가 일치하는 소유 서버가 없어 종료하지 않았습니다.' }
        Assert-Idle
        # Only the recorded engine child and server may be stopped. Never taskkill /IM or an arbitrary tree.
        if ($saved.engine) {
            $engine=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$saved.engine.pid) -ErrorAction SilentlyContinue
            if ($engine -and ($engine.ExecutablePath -ne $saved.engine.executable -or $engine.CreationDate.ToUniversalTime().ToString('o') -ne $saved.engine.created -or $engine.ParentProcessId -ne $saved.pid -or -not $engine.CommandLine.Contains($r.pack))) { throw '엔진 프로세스 소유권이 달라 종료하지 않았습니다.' }
            if ($engine) { Stop-Process -Id $engine.ProcessId -ErrorAction Stop }
        }
        if (-not (Owned-Server)) { throw '종료 직전 서버 소유권이 변경됐습니다.' }
        Stop-Process -Id $saved.pid -ErrorAction Stop
        Write-Output '이 설치의 Strata 프로세스만 종료했습니다.';exit 0
    }
    if (-not $saved) {
        if (Get-NetTCPConnection -LocalPort 18021 -State Listen -ErrorAction SilentlyContinue) { throw '18021 포트 충돌. 기존 서버를 종료하거나 바꾸지 않았습니다.' }
        if (Get-NetTCPConnection -LocalPort 18022 -State Listen -ErrorAction SilentlyContinue) {
            $q=Strata-Get 'http://127.0.0.1:18022' '/queue'
            if ($null -eq $q.running -or $q.running -ne 0 -or $q.waiting -ne 0 -or $q.ui_running -ne 0) { throw '대기열이 유휴 상태가 아니므로 시작하지 않았습니다.' }
        }
        $r=Read-Prepared -LaunchIntegrity
        $arguments=Strata-LaunchArguments $r.entry $configPath $r.gpu
        $p=Start-Process -FilePath $r.python -ArgumentList $arguments -WorkingDirectory $ownedRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $ownedRoot 'server.out.log') -RedirectStandardError (Join-Path $ownedRoot 'server.err.log')
        $observed=Get-CimInstance Win32_Process -Filter ('ProcessId='+$p.Id)
        if (-not $observed) { throw 'Strata 서버가 시작 직후 종료됐습니다. strata/server.err.log를 확인하세요.' }
        $saved=[ordered]@{pid=$p.Id;created=$observed.CreationDate.ToUniversalTime().ToString('o');executable=$observed.ExecutablePath;root=$InstallRoot;backend='strata';config=$configPath;entry=$r.entry;engine=$null}
        Strata-WriteJson $processPath $saved
    }
    # A repeat Start observes the same owned PID, never submits a second model start.
    for ($i=0;$i -lt 120;$i++) {
        if (-not (Owned-Server)) { throw '소유 Strata 프로세스가 종료됐습니다. strata/server.err.log를 확인하세요.' }
        $children=@(Get-CimInstance Win32_Process -Filter ('ParentProcessId='+[int]$saved.pid) | Where-Object { $_.ExecutablePath -eq $r.engine -and $_.CommandLine.Contains($r.pack) })
        if ($children.Count -eq 1) {
            $child=$children[0];$saved.engine=@{pid=$child.ProcessId;created=$child.CreationDate.ToUniversalTime().ToString('o');executable=$child.ExecutablePath}
            Strata-WriteJson $processPath $saved
        }
        try {
            $identity=Strata-Identity (Strata-Get 'http://127.0.0.1:18021' '/health') (Strata-Get 'http://127.0.0.1:18021' '/v1/models') $r.model
            if ($identity.loaded -and $identity.contextTokens -eq $r.profile.contextTokens -and $saved.engine) { Write-Output ('Strata loaded · '+$r.model+' · '+$identity.contextTokens);exit 0 }
        } catch { }
        if ($i%10 -eq 0) { Write-Output ('Strata 실행됨 · 모델 준비 확인 중 '+$i+'/120초') };Start-Sleep -Seconds 1
    }
    Write-Output 'Strata 프로세스는 실행 중이나 loaded를 확인하지 못했습니다. 재설치/중복 시작 대신 로그와 Verify를 확인하세요.';exit 2
}
# Deliberately bounded acquisition: upstream setup can fall back to a moving signed feed and test-start an engine.
# Until an exact official engine archive manifest is reviewed, use verified installed prerequisites only.
foreach ($value in @($StrataSource,$StrataPython,$StrataConfig)) { $null=Strata-FullPath $value }
$StrataSource=Strata-FullPath $StrataSource;$StrataPython=Strata-FullPath $StrataPython;$StrataConfig=Strata-FullPath $StrataConfig
if ($EngineSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $GpuIndex -lt 0) { throw '검증한 엔진 SHA256과 명시적 GPU index가 필요합니다.' }
if ($StrataPython -notmatch '(?i)[\\/]Scripts[\\/]python\.exe$') { throw 'CUDA 의존성이 준비된 전용 가상환경의 Scripts/python.exe를 지정하세요.' }
if ($StrataSource.StartsWith($ownedRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '기존 원본과 관리 산출물 경로는 분리하세요.' }
if (Test-Path -LiteralPath $receiptPath) {
    $r=Read-Prepared -Integrity
    if ($r.source -ne $StrataSource -or $r.python -ne $StrataPython -or $r.inputConfig -ne $StrataConfig -or $r.engineSha256 -ne $EngineSha256.ToLowerInvariant() -or ($r.profile|ConvertTo-Json -Compress) -ne ($profileData|ConvertTo-Json -Compress) -or $r.gpu -ne $GpuIndex) { throw '기존 Strata 준비와 선택이 다릅니다. 새 전용 설치 폴더를 사용하세요.' }
    foreach ($weight in $r.weights) { Write-Output ('GGUF SHA256 재검사: '+[IO.Path]::GetFileName($weight.path));$null=Strata-Hash $weight.path $weight.sha256 $weight.bytes }
    Write-Output 'SHA256이 일치하는 기존 준비를 재사용했습니다. 모델을 시작하지 않았습니다.';exit 0
}
if (Get-NetTCPConnection -LocalPort 18021 -State Listen -ErrorAction SilentlyContinue) { throw '18021이 사용 중입니다. 새 준비를 시작하기 전 사용할 설치 경로/포트를 확인하세요. 기존 서버는 변경하지 않았습니다.' }
$git=Get-Command git.exe -ErrorAction Stop
$commit=(& $git.Source -C $StrataSource rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $commit.Trim() -ne $pins.commit) { throw 'Strata 소스 커밋이 고정값과 다릅니다.' }
& $git.Source -C $StrataSource diff --quiet HEAD -- .
if ($LASTEXITCODE -ne 0) { throw 'Strata 추적 소스에 변경이 있습니다. 검증된 원본을 지정하세요.' }
& $StrataPython -I -c 'import sys; assert sys.version_info >= (3,10), "Python 3.10+ required"; import numpy, jinja2'
if ($LASTEXITCODE -ne 0) { throw 'Python 3.10+와 Strata 의존성이 설치된 전용 가상환경이 필요합니다.' }
$config=Get-Content -LiteralPath $StrataConfig -Raw|ConvertFrom-Json;$argsMap=Strata-Arguments $config
$engine=Strata-FullPath $config.exe;$engineHash=Strata-Hash $engine $EngineSha256
$build=Get-Content -LiteralPath (Join-Path (Split-Path -Parent $engine) 'BUILD.json') -Raw|ConvertFrom-Json
if ($build.version -ne $pins.engineVersion -or $build.cuda -notmatch '^13\.' -or $build.backend -eq 'hip') { throw '공식 CUDA 13 엔진 0.1.48을 준비하세요. 최신 엔진으로 자동 전환하지 않습니다.' }
$weights=@();$first=$pins.profiles.$Profile;$second=$pins.model.sharedSecond
foreach ($pair in @(@('--native','00001',$first),@('--ple-gguf','00002',$second))) {
    $file=Strata-FullPath $argsMap[$pair[0]];$expected=$pins.model.filenamePattern.Replace('{quant}',$Profile).Replace('{shard}',$pair[1])
    if ([IO.Path]::GetFileName($file) -cne $expected) { throw ('선택한 SC117 GGUF 파일이 아닙니다: '+$expected) }
    Write-Output ('[1/4] GGUF 검증 · '+$expected+' · '+$pair[2].bytes+' bytes (다운로드 없음)')
    $hash=Strata-Hash $file $pair[2].sha256 $pair[2].bytes;$weights+=@{path=$file;sha256=$hash;bytes=$pair[2].bytes}
}
$pack=Strata-FullPath $argsMap['--pack'];$mtp=Strata-FullPath $argsMap['--mtp'];$tokenizer=Strata-FullPath $config.tokenizer
foreach ($file in @((Join-Path $pack 'native_experts.txt'),(Join-Path $tokenizer 'vocab.json'),(Join-Path $tokenizer 'merges.txt'))) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw ('SC117 pack/tokenizer 선행 준비 필요: '+[IO.Path]::GetFileName($file)) } }
if (-not (Test-Path -LiteralPath $mtp -PathType Container)) { throw 'SC117 MTP pack 선행 준비가 필요합니다.' }
# Do not accept a standard model's packed cache under an SC117 display name.
if ($config.model_name -cne ('qwen3.8-flash-next-sc117-uncensored-'+$Profile.ToLowerInvariant())) { throw 'SC117 served model_name이 선택한 양자화와 다릅니다.' }
$ram=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB
if ($ram -lt $pins.minimumRamGiB -or $ResidentBudgetGiB+20 -gt $ram) { throw '이 프로필은 RAM 48GiB 이상과 상주 예산 외 최소 20GiB 여유가 필요합니다. 프로필을 조정하세요.' }
$smi=Get-Command nvidia-smi.exe -ErrorAction Stop
$gpu=& $smi.Source ('--id='+$GpuIndex) --query-gpu=memory.total --format=csv,noheader,nounits
if ($LASTEXITCODE -ne 0 -or "$gpu".Trim() -notmatch '^\d+$' -or [int]"$gpu".Trim() -lt $pins.minimumVramMiB) { throw '선택한 NVIDIA GPU의 VRAM이 이 예시 프로필(23,000MiB)에 부족하거나 확인되지 않았습니다.' }
$drive=Get-PSDrive -Name ([IO.Path]::GetPathRoot($InstallRoot).Substring(0,1)) -ErrorAction Stop
if ($drive.Free -lt $pins.minimumFreeGiB*1GB) { throw '새 준비용 여유 공간이 4GiB 미만입니다 (모델 선행 준비 공간 제외).' }
Write-Output '[2/4] loopback 전용 관리 설정 생성 · 자동 시작 없음'
New-Item -ItemType Directory -Path $ownedRoot -Force|Out-Null
try { $lock=[IO.File]::Open((Join-Path $ownedRoot 'prepare.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) } catch { throw 'Strata 준비가 이미 진행 중입니다.' }
try {
    if (Test-Path -LiteralPath $configPath) { throw '기록 없는 기존 server.json을 덮어쓰지 않습니다. 새 설치 폴더를 선택하세요.' }
    $newArgs=@('--pack',$pack,'--native',$weights[0].path,'--ple-gguf',$weights[1].path,'--expert-cache','auto','--prefill',$profileData.prefill,'--spec','4','--spec-min-p','0.5','--mtp',$mtp,'--max-context',"$ContextTokens",'--kv','int8','--kv-resident',"$($profileData.kvResident)",'--vram-reserve-mib','1280','--pool-workers',"$PoolWorkers",'--resident-budget-gib',$ResidentBudgetGiB.ToString([Globalization.CultureInfo]::InvariantCulture))
    $libs=@();foreach ($lib in $config.lib_dirs) { $path=Strata-FullPath $lib;if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw '선행 CUDA DLL 디렉터리가 없습니다.' };$libs+=$path }
    $generated=[ordered]@{exe=$engine;args=$newArgs;cwd=$ownedRoot;tokenizer=$tokenizer;model_name=$config.model_name;log=(Join-Path $ownedRoot 'engine.log');lib_dirs=$libs;port=18021;host='127.0.0.1';gpu=$GpuIndex;gpus_asked=$true;draft_vocab='cjk';open_browser=$false;reasoning_effort='none';sampling=@{experimental_speed_projection=$false}}
    Strata-WriteJson $configPath $generated
    Write-Output '[3/4] 소스·엔진·설정·pack 파일 식별 기록'
    $entry=Join-Path $StrataSource 'serve\server.py';$artifacts=@()
    foreach ($file in @($StrataPython,$engine,$entry,$StrataConfig,$configPath,(Join-Path $pack 'native_experts.txt'))+@(Get-ChildItem -LiteralPath $tokenizer -File|Select-Object -ExpandProperty FullName)+@(Get-ChildItem -LiteralPath $mtp -File|Select-Object -ExpandProperty FullName)) { $artifacts+=@{path=$file;sha256=(Strata-Hash $file '')} }
    $record=[ordered]@{version=1;status='prepared';root=$InstallRoot;backend='strata';model=$config.model_name;profile=$profileData;source=$StrataSource;sourceCommit=$pins.commit;engine=$engine;engineSha256=$engineHash;engineVersion=$pins.engineVersion;python=$StrataPython;entry=$entry;inputConfig=$StrataConfig;config=$configPath;pack=$pack;gpu=$GpuIndex;weights=$weights;artifacts=$artifacts;modelRevision=$pins.model.revision;created=(Get-Date).ToUniversalTime().ToString('o')}
    Strata-WriteJson $receiptPath $record
    Write-Output '[4/4] prepared · 기존 SC117 파일 검증 완료 · 서버 미실행 · Start는 별도 사용자 동작'
} finally { $lock.Dispose() }
