#requires -Version 5.1
<#
Install prepares one pinned Docker backend and its public model; it never starts a model server.
Plan is read-only JSON and does not require Docker, Git, a GPU or network access.
Stop targets only the container ID recorded by this installation's successful Start.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Plan','Install','Start','Stop','Verify')][string]$Action,
    [Parameter(Mandatory=$true)][ValidateSet('vllm','ninfer')][string]$Backend,
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [ValidateSet('standard','uncensored')][string]$Model = 'standard',
    [ValidateRange(0,32)][int]$GpuIndex = 0,
    [ValidateSet('Auto','Ada','Blackwell')][string]$GpuArchitecture = 'Auto',
    [string]$HfTokenFile,
    [switch]$Json
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Write-Utf8([string]$Path, [string]$Value) {
    [System.IO.File]::WriteAllText($Path, $Value, (New-Object System.Text.UTF8Encoding($false)))
}
function Invoke-Checked([string]$File, [string[]]$Arguments, [switch]$Capture) {
    if ($Capture) { $result = & $File @Arguments 2>&1 } else { & $File @Arguments | Out-Host }
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE. Review the command output; no automatic cleanup or restart is performed." }
    if ($Capture) { return ($result -join "`n") }
}
function Assert-NoReparse([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse-point installation paths are unsupported: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
function Assert-Docker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker CLI is missing. Install Docker Desktop for Windows with the WSL2 Linux engine yourself, review its license, then enable GPU support. This installer does not accept Docker license terms or install Docker.'
    }
    if ($env:DOCKER_HOST -and $env:DOCKER_HOST -notmatch '^(npipe|unix)://') {
        throw 'Remote/TCP Docker endpoints are unsupported. Select the local Docker Desktop Linux context.'
    }
    $endpoint = Invoke-Checked docker @('context','inspect','--format','{{.Endpoints.docker.Host}}') -Capture
    if ($endpoint -notmatch '^(npipe|unix)://') { throw 'This profile requires a local Docker engine, not a remote Docker context.' }
    $os = Invoke-Checked docker @('version','--format','{{.Server.Os}}') -Capture
    if ($os.Trim() -ne 'linux') { throw 'Start Docker Desktop and select Linux containers / WSL2. A reachable Linux Docker engine is required.' }
    $null = Invoke-Checked docker @('compose','version','--short') -Capture
}
function Assert-Hardware {
    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if (-not $smi) { throw 'nvidia-smi is unavailable. Install a supported NVIDIA Windows driver with WSL2 GPU support, then retry.' }
    $rows = Invoke-Checked $smi.Source @('--query-gpu=index,name,memory.total','--format=csv,noheader,nounits') -Capture
    $gpu = @($rows -split "`n" | ForEach-Object {
        $parts = $_ -split ','
        if ($parts.Count -eq 3) { [pscustomobject]@{Index=[int]$parts[0].Trim(); Name=$parts[1].Trim(); Memory=[int]$parts[2].Trim()} }
    } | Where-Object { $_.Index -eq $GpuIndex })
    if ($gpu.Count -ne 1 -or $gpu[0].Name -notmatch $profile.gpuNamePattern -or $gpu[0].Memory -lt $profile.minimumVramMiB) {
        throw "$Backend requires GPU $GpuIndex to match '$($profile.gpuNamePattern)' with at least $($profile.minimumVramMiB) MiB VRAM. This profile does not support the detected GPU."
    }
    $ram = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB
    if ($ram -lt ($profile.minimumRamGiB - 0.5)) { throw "At least $($profile.minimumRamGiB) GiB physical RAM is required. Detected $([Math]::Round($ram,1)) GiB." }
}
function Assert-FreeSpace {
    $drive = New-Object System.IO.DriveInfo([IO.Path]::GetPathRoot($root))
    $free = [Math]::Floor($drive.AvailableFreeSpace / 1GB)
    if ($free -lt $profile.minimumFreeGiB) { throw "Insufficient free disk space on $($drive.Name): $free GiB available; $($profile.minimumFreeGiB) GiB required for model preparation." }
    Write-Host "Docker's separate Linux disk also needs at least $($profile.dockerStorageFreeGiB) GiB free. Its available capacity cannot be determined from the Windows installation drive. A Docker disk-full error must be resolved before retrying."
}
function Read-OwnedState {
    if (-not (Test-Path -LiteralPath $stateFile)) { throw 'No owned backend installation exists here. Run -Action Install first.' }
    $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    if ($state.installId -ne $installId -or $state.backend -ne $Backend -or $state.model -ne $Model -or $state.root -ne $root) {
        throw 'Backend ownership marker does not match this installation. No container or file will be modified.'
    }
    return $state
}
function Assert-NoRunningBackend {
    $running = Invoke-Checked docker @('ps','--filter','label=io.qwenstatus.role=backend','--format','{{.ID}} {{.Names}}') -Capture
    if ($running.Trim()) { throw "A Qwen Status model backend is already running: $running. Stop it explicitly from its owning installation before starting another." }
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,18021)
    try { $listener.Start() } catch { throw '127.0.0.1:18021 is already in use. This installer will not stop the existing listener.' } finally { $listener.Stop() }
}
function Assert-ContainerOwner([string]$Id) {
    if ($Id -notmatch '^[a-f0-9]{64}$') { throw 'Invalid recorded container ID; refusing to stop anything.' }
    $obj = (Invoke-Checked docker @('inspect',$Id) -Capture | ConvertFrom-Json)[0]
    $labels = $obj.Config.Labels
    if ($labels.'io.qwenstatus.install-id' -ne $installId -or $labels.'io.qwenstatus.backend' -ne $Backend -or $labels.'io.qwenstatus.model' -ne $Model) {
        throw 'Container ownership labels do not match. Refusing to modify the container.'
    }
    return $obj
}
function Get-Compose {
    $labels = [ordered]@{'io.qwenstatus.role'='backend';'io.qwenstatus.install-id'=$installId;'io.qwenstatus.backend'=$Backend;'io.qwenstatus.model'=$Model}
    $mounts = @(@{type='bind';source=(Join-Path $dataDir 'models');target='/app/models'},@{type='bind';source=(Join-Path $dataDir 'cache');target='/cache'})
    $service = [ordered]@{
        image=$imageName
        pull_policy='never'
        restart='no'
        labels=$labels
        ports=@(@{target=18021;published='18021';host_ip='127.0.0.1';protocol='tcp'})
        stop_grace_period='30s'
        deploy=@{resources=@{reservations=@{devices=@(@{driver='nvidia';device_ids=@([string]$GpuIndex);capabilities=@('gpu')})}}}
    }
    if ($Backend -eq 'vllm') {
        $service['shm_size']='2gb'
        $service['environment']=[ordered]@{HOME='/cache';PREPARE='0';VERIFY='1';SPEC='dflash2';DFLASH_TOKENS='7';CTX='fast';MAX_LEN='65536';PREFIX_CACHE='1';VLLM_WSL2_ENABLE_PIN_MEMORY='1';HOST='0.0.0.0';PORT='18021';MODEL='/app/models/Qwen3.8-27B-W4A16-AutoRound-fast';FAST_VARIANT='1';DFLASH2='1'}
        $service['volumes']=$mounts
        $service['command']=@('single')
    } else {
        $service['volumes']=@(@{type='bind';source=(Join-Path $dataDir 'models');target='/models';read_only=$true})
        $kvType = $profile.kvDtype
        $service['command']=@('ninfer-serve',('/models/' + $profile.model.filename),'--host','0.0.0.0','--port','18021','--model-id','qwen3.8-27b','--max-context','229376','--kv-capacity','229376','--max-concurrency','1','--max-pending-requests','16','--pending-timeout-ms','1800000','--prefill-chunk','1024','--kv-dtype',$kvType,'--spec','mtp','--draft-tokens','3','--lm-head-draft','--no-thinking')
        if ($profile.requiresConversion) { $service['command'] += @('--device-state-slots','0','--host-state-slots','0','--host-kv-mib','0') }
    }
    return [ordered]@{name=$projectName;services=@{backend=$service}}
}
function Assert-Prepared {
    $state = Read-OwnedState
    if (-not $state.prepared) { throw 'Model preparation is incomplete. Run -Action Install to resume; Start never downloads models.' }
    if ($state.sourceCommit -ne $profile.commit -or $state.manifestHash -ne $manifestHash) { throw 'This profile changed since installation. Use a separate installation root or review and reinstall it.' }
    $imageId = Invoke-Checked docker @('image','inspect','--format','{{.Id}}',$imageName) -Capture
    if ($imageId.Trim() -ne $state.imageId) { throw 'Installed Docker image differs from the recorded image ID. Refusing to start an unverified replacement.' }
    $modelsDirectory = Join-Path $dataDir 'models'
    $requiredFiles = @()
    if ($Backend -eq 'vllm') {
        $fastDirectory = Join-Path $modelsDirectory 'Qwen3.8-27B-W4A16-AutoRound-fast'
        foreach ($name in @('config.json','tokenizer.json','tokenizer_config.json','model.safetensors.index.json')) { $requiredFiles += Join-Path $fastDirectory $name }
        $requiredFiles += Join-Path $modelsDirectory 'Qwen3.8-27B-DFlash2-W4A16\config.json'
        $requiredFiles += Join-Path $modelsDirectory 'Qwen3.8-27B-DFlash2-W4A16\model.safetensors'
        $indexPath = Join-Path $fastDirectory 'model.safetensors.index.json'
        if (Test-Path -LiteralPath $indexPath) {
            $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
            foreach ($shard in @($index.weight_map.PSObject.Properties.Value | Select-Object -Unique)) {
                $shardPath = [IO.Path]::GetFullPath((Join-Path $fastDirectory $shard))
                if (-not $shardPath.StartsWith($fastDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Prepared model index contains a path outside its directory.' }
                $requiredFiles += $shardPath
            }
        }
    } else {
        $modelPath = Join-Path $modelsDirectory $profile.model.filename
        $requiredFiles += $modelPath
        if ($profile.requiresConversion) {
            $requiredFiles += $modelPath + '.sha256'
            $requiredFiles += $modelPath + '.conversion.json'
            if (Test-Path -LiteralPath ($modelPath + '.conversion.json')) {
                $report = Get-Content -LiteralPath ($modelPath + '.conversion.json') -Raw | ConvertFrom-Json
                foreach ($part in $report.files) { $requiredFiles += Join-Path $modelsDirectory ([IO.Path]::GetFileName($part.path)) }
            }
        }
    }
    foreach ($path in $requiredFiles) {
        Assert-NoReparse $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Prepared model file is missing or empty: $path. Verify does not report a missing model as prepared." }
    }
    return $state
}

if ($InstallRoot -notmatch '^[A-Za-z]:[\\/]' -or $InstallRoot -match '[\r\n]') { throw 'InstallRoot must be an absolute local Windows drive path, such as D:\QwenStatus. UNC and relative paths are unsupported.' }
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\','/')
if ($root.Length -le 3) { throw 'InstallRoot must be a dedicated subdirectory, not a drive root.' }
Assert-NoReparse $root
$manifestPath = Join-Path $PSScriptRoot 'backends.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$profile = $manifest.$Backend
if ($Backend -eq 'ninfer' -and $Model -eq 'uncensored') { $profile = $manifest.ninferUncensored }
if ($Backend -eq 'ninfer' -and $Model -eq 'standard') {
    if ($GpuArchitecture -eq 'Auto') {
        $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
        if ($smi) {
            $detectedName = & $smi.Source "--id=$GpuIndex" '--query-gpu=name' '--format=csv,noheader' 2>$null
            if ($LASTEXITCODE -eq 0 -and ($detectedName -join '') -match 'RTX 5090') { $GpuArchitecture = 'Blackwell' }
        }
    }
    if ($GpuArchitecture -eq 'Blackwell') { $profile = $manifest.ninferBlackwell }
}
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hasher = [Security.Cryptography.SHA256]::Create()
try { $installId = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant())))).Replace('-','').Substring(0,16).ToLowerInvariant() } finally { $hasher.Dispose() }
$dataDir = Join-Path $root ('backends\' + $Backend + '-' + $Model)
$stateFile = Join-Path $dataDir 'ownership.json'
$composeFile = Join-Path $dataDir 'compose.json'
$sourceDir = Join-Path $dataDir 'source'
$projectName = "qwenstatus-$installId-$Backend-$Model"
$imageName = "qwenstatus/$Backend`:$($profile.commit.Substring(0,12))-$installId"
$supported = $Model -eq 'standard' -or $profile.uncensoredSupported

if ($Action -eq 'Plan') {
    [ordered]@{schemaVersion=1;action='Plan';readOnly=$true;installRoot=$root;backend=$Backend;model=$Model;supported=$supported;unsupportedReason=$(if ($supported) {$null} else {$profile.uncensoredReason});contextTokens=$profile.contextTokens;endpoint='http://127.0.0.1:18021/v1';sourceRepository=$profile.repository;sourceCommit=$profile.commit;minimumFreeGiB=$profile.minimumFreeGiB;dockerStorageFreeGiB=$profile.dockerStorageFreeGiB;minimumRamGiB=$profile.minimumRamGiB;supportedGpuPattern=$profile.gpuNamePattern;gpuIndex=$GpuIndex;installStartsServer=$false;phases=@('prerequisite-check','fetch-pinned-source','build-pinned-base-image','download-and-prepare-model','record-image-id');profile=$profile;uncensored=$manifest.uncensored;compose=(Get-Compose);reproducibility=$manifest.reproducibility} | ConvertTo-Json -Depth 16
    exit 0
}
if (-not $supported) { throw "$Backend + $Model is unsupported in this pinned profile. $($profile.uncensoredReason) Gated access must be approved personally at $($manifest.uncensored.accessUrl). No model will be substituted or downloaded." }
Assert-Docker
Assert-NoReparse $dataDir

if ($Action -eq 'Verify') {
    $state = Assert-Prepared
    $report = [ordered]@{installed=$true;prepared=$true;backend=$Backend;model=$Model;imageId=$state.imageId;contextTokens=$profile.contextTokens;running=$false;healthy=$false;endpoint='http://127.0.0.1:18021/v1'}
    if ($state.containerId) {
        $container = Assert-ContainerOwner $state.containerId
        $report.running = [bool]$container.State.Running
        if ($report.running) {
            try { $health = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:18021/health' -TimeoutSec 10; $report.healthy = $health.StatusCode -eq 200 } catch { $report.healthy = $false }
        }
    }
    $report | ConvertTo-Json -Depth 4
    if ($report.running -and -not $report.healthy) { exit 2 }
    exit 0
}

$mutex = New-Object Threading.Mutex($false,'Local\QwenStatus-Backend-18021')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another backend installer/controller is active. Retry after it finishes.' }
    if ($Action -eq 'Stop') {
        $state = Read-OwnedState
        if (-not $state.containerId) { Write-Host 'This installation has no recorded started container. Nothing was stopped.'; exit 0 }
        $container = Assert-ContainerOwner $state.containerId
        if ($container.State.Running) { Invoke-Checked docker @('stop','--time','30',$state.containerId) }
        Write-Host 'The recorded, owned model container is stopped. Its image and model files remain installed.'
        exit 0
    }
    Assert-Hardware
    if ($Action -eq 'Start') {
        $state = Assert-Prepared
        Assert-NoRunningBackend
        Write-Utf8 $composeFile ((Get-Compose) | ConvertTo-Json -Depth 12)
        Invoke-Checked docker @('compose','--project-name',$projectName,'-f',$composeFile,'up','-d','--no-build','--no-deps','--pull','never','backend')
        $id = (Invoke-Checked docker @('compose','--project-name',$projectName,'-f',$composeFile,'ps','-a','-q','backend') -Capture).Trim()
        $null = Assert-ContainerOwner $id
        $state.containerId = $id
        Write-Utf8 $stateFile ($state | ConvertTo-Json -Depth 6)
        Write-Host 'Model container started. Compilation/loading can take several minutes. Run -Action Verify to check readiness; startup is not a health guarantee.'
        exit 0
    }

    Assert-FreeSpace
    if ($Model -eq 'uncensored') {
        if (-not $HfTokenFile -or -not [IO.Path]::IsPathRooted($HfTokenFile) -or -not (Test-Path -LiteralPath $HfTokenFile -PathType Leaf)) {
            throw 'Gated model access requires a user-owned Hugging Face token file. Personally accept access at https://huggingface.co/orcarouter/Qwen3.8-27B-Uncensored and authenticate, then supply its absolute path with -HfTokenFile. Credentials are never written into the install plan or image.'
        }
        $HfTokenFile = [IO.Path]::GetFullPath($HfTokenFile)
    }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git for Windows is required to fetch the pinned source. Install Git, then retry.' }
    if (Test-Path -LiteralPath $dataDir) {
        $state = Read-OwnedState
        if ($state.sourceCommit -ne $profile.commit -or $state.manifestHash -ne $manifestHash) { throw 'Existing profile differs. Use a new installation root; no existing model or image is replaced automatically.' }
        $running = Invoke-Checked docker @('ps','--filter',"label=io.qwenstatus.install-id=$installId",'--filter',"label=io.qwenstatus.backend=$Backend",'--format','{{.ID}}') -Capture
        if ($running.Trim()) { throw 'This backend is running. Stop it explicitly before reinstalling; the installer will not stop it.' }
    } else {
        $null = New-Item -ItemType Directory -Path $dataDir -Force
        $state = [pscustomobject]@{schemaVersion=1;installId=$installId;root=$root;backend=$Backend;model=$Model;sourceCommit=$profile.commit;manifestHash=$manifestHash;prepared=$false;imageId='';containerId=''}
        Write-Utf8 $stateFile ($state | ConvertTo-Json -Depth 6)
    }
    foreach ($folder in @('models','cache','setup')) { $null = New-Item -ItemType Directory -Path (Join-Path $dataDir $folder) -Force }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $dataDir 'setup\backends.json')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'backend-prepare.py') -Destination (Join-Path $dataDir 'setup\backend-prepare.py')
    if (-not (Test-Path -LiteralPath $sourceDir)) {
        Invoke-Checked git @('init',$sourceDir)
        Invoke-Checked git @('-C',$sourceDir,'remote','add','origin',$profile.repository)
    }
    $remote = (Invoke-Checked git @('-C',$sourceDir,'remote','get-url','origin') -Capture).Trim()
    if ($remote -ne $profile.repository) { throw 'Source remote does not match the pinned upstream; refusing to overwrite it.' }
    Invoke-Checked git @('-C',$sourceDir,'fetch','--depth','1','origin',$profile.commit)
    Invoke-Checked git @('-C',$sourceDir,'checkout','--detach',$profile.commit)
    $head = (Invoke-Checked git @('-C',$sourceDir,'rev-parse','HEAD') -Capture).Trim()
    if ($head -ne $profile.commit) { throw 'Fetched source does not match the pinned commit.' }
    $dirty = Invoke-Checked git @('-C',$sourceDir,'status','--porcelain','--untracked-files=no') -Capture
    if ($dirty.Trim()) { throw 'Source tree has local tracked changes. Refusing to build modified upstream source.' }
    $dockerfile = Get-Content -LiteralPath (Join-Path $sourceDir 'Dockerfile') -Raw
    foreach ($entry in $profile.baseImages.PSObject.Properties) {
        if (-not $dockerfile.Contains('FROM ' + $entry.Name)) { throw "Pinned Docker base not found in upstream Dockerfile: $($entry.Name)" }
        $dockerfile = $dockerfile.Replace(('FROM ' + $entry.Name),('FROM ' + $entry.Name + '@' + $entry.Value))
    }
    if ($Backend -eq 'ninfer' -and $profile.requiresConversion) {
        # Same GeForce compatibility fix documented by the ninfer-4090 fork.
        $anchor = 'COPY --from=build /build/apps/ninfer /usr/local/bin/ninfer'
        if (-not $dockerfile.Contains($anchor)) { throw 'NInfer Dockerfile layout changed; refusing an unreviewed compatibility edit.' }
        $dockerfile = $dockerfile.Replace($anchor, "RUN rm -rf /usr/local/cuda-13.1/compat /usr/local/cuda-13/compat /usr/local/cuda/compat`n`n$anchor")
    }
    $pinnedDockerfile = Join-Path $dataDir 'Dockerfile.pinned'
    Write-Utf8 $pinnedDockerfile $dockerfile
    Invoke-Checked docker @('build','--tag',$imageName,'--file',$pinnedDockerfile,$sourceDir)
    $state.imageId = (Invoke-Checked docker @('image','inspect','--format','{{.Id}}',$imageName) -Capture).Trim()
    $state.prepared = $false
    Write-Utf8 $stateFile ($state | ConvertTo-Json -Depth 6)
    $setupMount = (Join-Path $dataDir 'setup') + ':/setup:ro'
    $modelsMount = (Join-Path $dataDir 'models') + $(if ($Backend -eq 'vllm') {':/app/models'} else {':/models'})
    # Preparation has no GPU device request and publishes no port.
    if ($Backend -eq 'vllm') {
        Invoke-Checked docker @('run','--rm','--restart','no','--label','io.qwenstatus.role=prepare','--volume',$setupMount,'--volume',$modelsMount,'--volume',((Join-Path $dataDir 'cache') + ':/cache'),'--env','HOME=/cache','--env','FAST_VARIANT=1','--env','DFLASH2=1',$imageName,'/app/venv/bin/python','/setup/backend-prepare.py','vllm')
    } elseif ($profile.requiresConversion) {
        $converterImage = "qwenstatus/ninfer-converter:$($profile.commit.Substring(0,12))-$installId"
        $converterDockerfile = Join-Path $PSScriptRoot 'backend-convert.Dockerfile'
        Invoke-Checked docker @('build','--tag',$converterImage,'--file',$converterDockerfile,$PSScriptRoot)
        $convertArgs = @('run','--rm','--restart','no','--label','io.qwenstatus.role=prepare','--volume',$setupMount,'--volume',$modelsMount,'--volume',($sourceDir + ':/source:ro'))
        if ($Model -eq 'uncensored') { $convertArgs += @('--volume',($HfTokenFile + ':/run/secrets/hf_token:ro')) }
        $convertArgs += @($converterImage, $(if ($Model -eq 'uncensored') {'ninfer-uncensored'} else {'ninfer-blackwell'}))
        Invoke-Checked docker $convertArgs
    } else {
        Invoke-Checked docker @('pull',$profile.prepareImage)
        Invoke-Checked docker @('run','--rm','--restart','no','--label','io.qwenstatus.role=prepare','--volume',$setupMount,'--volume',$modelsMount,$profile.prepareImage,'python','/setup/backend-prepare.py','ninfer')
    }
    $state.prepared = $true
    Write-Utf8 $composeFile ((Get-Compose) | ConvertTo-Json -Depth 12)
    Write-Utf8 $stateFile ($state | ConvertTo-Json -Depth 6)
    Write-Host "Installed $Backend ($Model). Model preparation is complete. No model server was started. Use -Action Start explicitly when ready."
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
