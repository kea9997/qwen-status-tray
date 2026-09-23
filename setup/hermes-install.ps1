#requires -Version 5.1
<#
.SYNOPSIS
Install the official Hermes Python source into a new, isolated user directory.
.DESCRIPTION
Plan is offline/read-only. Install downloads pinned tools and source, creates a
private Python environment, and writes only InstallRoot/hermes. Verify performs
read-only file/metadata checks; -CheckEndpoint optionally GETs the local queue.
No existing Hermes installation is discovered, imported, repaired, or started.
#>
[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Install', 'Verify')][string]$Action = 'Plan',
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [switch]$CheckEndpoint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runningWindows = $env:OS -eq 'Windows_NT'
$hermesCommit = '62ac203abe6373a3e54cb78a851326b099a88f14'
$uvVersion = '0.12.18'
$gitVersion = '2.55.0.5'
$queueUrl = 'http://127.0.0.1:18022/v1'
$modelName = 'qwen3.8-27b'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Get-AbsoluteRoot([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -match '[\r\n\x00]') {
        throw 'InstallRoot must be a nonempty absolute local path.'
    }
    if ($runningWindows) {
        if ($Value -notmatch '^[A-Za-z]:[\\/]') { throw 'Use a fully qualified local drive path, for example D:\QwenSuite.' }
    } elseif (-not $Value.StartsWith('/')) {
        throw 'Use an absolute Linux path, for example /home/alice/qwen-suite.'
    }
    $resolved = [IO.Path]::GetFullPath($Value).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($resolved -eq [IO.Path]::GetPathRoot($resolved).TrimEnd('\', '/') -or $resolved.Length -lt 4) {
        throw 'A filesystem root cannot be used as InstallRoot.'
    }
    # Reject junctions/symlinks in every existing ancestor before any write.
    $cursor = $resolved
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "InstallRoot must not contain files, symbolic links, or junctions in its ancestors: $cursor"
            }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName
    }
    return $resolved
}

$installBase = Get-AbsoluteRoot $InstallRoot
$managedRoot = Join-Path $installBase 'hermes'
$sourceRoot = Join-Path $managedRoot 'source'
$venvRoot = Join-Path $managedRoot 'venv'
$hermesHome = Join-Path (Join-Path $managedRoot 'profiles') 'qwen'
$gitRoot = Join-Path $managedRoot 'git'
$pythonExe = if ($runningWindows) { Join-Path $venvRoot 'Scripts\python.exe' } else { Join-Path $venvRoot 'bin/python' }
$bashExe = if ($runningWindows) { Join-Path $gitRoot 'bin\bash.exe' } else { '/bin/bash' }
$resultPath = Join-Path $managedRoot 'install-result.json'
$logPath = Join-Path $managedRoot 'install.log'
$environmentContract = [ordered]@{
    QWEN_HERMES_PYTHON = $pythonExe
    QWEN_HERMES_ROOT = $sourceRoot
    QWEN_HERMES_HOME = $hermesHome
    QWEN_HERMES_GIT_BASH = $bashExe
}

function Assert-ManagedPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $managedRoot + [IO.Path]::DirectorySeparatorChar
    $comparison = if ($runningWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ($full -ne $managedRoot -and -not $full.StartsWith($prefix, $comparison)) { throw "Path escapes the managed directory: $full" }
    $null = Get-AbsoluteRoot ([IO.Path]::GetDirectoryName($full))
}

function Write-JsonFile([string]$Path, $Value) {
    Assert-ManagedPath $Path
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), $utf8)
}

function Write-Phase([string]$Message) {
    $line = '[Hermes] ' + $Message
    Write-Host $line
    [IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $utf8)
}

function Invoke-InstallCommand([string]$File, [string[]]$ArgumentList) {
    $previousPreference = $ErrorActionPreference
    try {
        # Native tools normally write progress to stderr even on success.
        $ErrorActionPreference = 'Continue'
        & $File @ArgumentList 2>&1 | ForEach-Object {
            $line = [string]$_
            Write-Host $line
            [IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $utf8)
        }
        $commandExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($commandExitCode -ne 0) { throw "Installation command failed (exit $commandExitCode): $File. See $logPath" }
}

function Save-Download([string]$Url, [string]$Destination, [string]$Sha256) {
    Assert-ManagedPath $Destination
    Write-Phase ('Downloading ' + ([Uri]$Url).AbsolutePath)
    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -TimeoutSec 1800
    $actualHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Sha256 -and $actualHash -ne $Sha256) { throw "SHA256 mismatch: $Destination" }
    return $actualHash
}

function Get-Verification {
    # Do not import Hermes, execute its CLI, or read config/credential contents.
    $checks = [ordered]@{}
    $checks.source = Test-Path -LiteralPath (Join-Path $sourceRoot 'hermes_cli\main.py') -PathType Leaf
    $checks.python = Test-Path -LiteralPath $pythonExe -PathType Leaf
    $checks.bash = Test-Path -LiteralPath $bashExe -PathType Leaf
    $checks.home = Test-Path -LiteralPath $hermesHome -PathType Container
    $checks.config = Test-Path -LiteralPath (Join-Path $hermesHome 'config.yaml') -PathType Leaf
    $checks.lockfile = Test-Path -LiteralPath (Join-Path $sourceRoot 'uv.lock') -PathType Leaf
    $checks.installRecord = Test-Path -LiteralPath $resultPath -PathType Leaf
    $checks.distributionMetadata = $false
    $packageVersion = $null
    if ($checks.python) {
        # -I ignores PYTHON* and user site packages; -B prevents bytecode writes.
        # importlib.metadata reads installed package metadata without loading it.
        try {
            $metadata = & $pythonExe -I -B -c 'import importlib.metadata as m,json; print(json.dumps({"hermes":m.version("hermes-agent"),"telegram":m.version("python-telegram-bot")}))' 2>$null
            if ($LASTEXITCODE -eq 0) {
                $packageVersion = ($metadata | ConvertFrom-Json).hermes
                $checks.distributionMetadata = -not [string]::IsNullOrWhiteSpace($packageVersion)
            }
        } catch { $checks.distributionMetadata = $false }
    }
    $endpoint = [ordered]@{ checked = $false; reachable = $null; modelAdvertised = $null }
    if ($CheckEndpoint) {
        $endpoint.checked = $true
        try {
            $models = Invoke-RestMethod -Uri ($queueUrl + '/models') -Method Get -TimeoutSec 10
            $endpoint.reachable = $true
            $endpoint.modelAdvertised = @($models.data | Where-Object { $_.id -eq $modelName }).Count -gt 0
        } catch {
            $endpoint.reachable = $false
            $endpoint.modelAdvertised = $false
        }
    }
    $ready = @($checks.Values | Where-Object { $_ -ne $true }).Count -eq 0
    return [ordered]@{
        schemaVersion = 1; action = 'Verify'; installed = $ready
        status = $(if ($ready) { 'installed' } else { 'incomplete' })
        runtimeStarted = $false; inferenceTested = $false
        packageVersion = $packageVersion; checks = $checks; endpoint = $endpoint
        environment = $environmentContract; resultPath = $resultPath
        windowsGatewayCompatible = $runningWindows
    }
}

if ($Action -eq 'Plan') {
    [ordered]@{
        schemaVersion = 1; action = 'Plan'; readOnly = $true
        platform = $(if ($runningWindows) { 'Windows' } else { 'Linux/WSL (PowerShell 7 required)' })
        installRoot = $installBase; managedRoot = $managedRoot
        canCreate = -not (Test-Path -LiteralPath $managedRoot)
        sourceCommit = $hermesCommit; uvVersion = $uvVersion; gitVersion = $gitVersion
        endpoint = $queueUrl; model = $modelName; environment = $environmentContract
        resultPath = $resultPath; windowsGatewayCompatible = $runningWindows
        steps = @('Download checksum-pinned uv and Windows PortableGit', 'Download official Hermes source at the pinned commit', 'Install private Python 3.11; no PATH/registry registration', 'Install core + messaging from the upstream frozen uv.lock', 'Create a dedicated local-Qwen profile and installation record')
        manualSteps = @('Start the Qwen queue separately when ready', 'Review first-run Hermes prompts yourself', 'Enter personal account/API/Telegram settings only in your private installation', 'Start CLI or messaging gateway only when requested')
        excluded = @('Existing profiles/credentials', 'Automatic remote-agent or Telegram startup', 'Node/browser/desktop/voice optional tooling', 'User PATH, shell profiles, registry, services, scheduled tasks')
    } | ConvertTo-Json -Depth 10
    return
}

if ($Action -eq 'Verify') {
    $verification = Get-Verification
    $verification | ConvertTo-Json -Depth 10
    if (-not $verification.installed -or ($CheckEndpoint -and -not $verification.endpoint.modelAdvertised)) { exit 2 }
    return
}

# Never adopt or overwrite an existing directory, including partial attempts.
if (Test-Path -LiteralPath $managedRoot) {
    throw "Refusing to overwrite $managedRoot. Use Verify or choose a fresh InstallRoot; inspect partial installations manually."
}
if (-not $runningWindows) {
    if ($PSVersionTable.PSVersion.Major -lt 7 -or -not (Test-Path -LiteralPath '/proc/sys/kernel/osrelease')) {
        throw 'Non-Windows installation requires PowerShell 7 in Linux/WSL.'
    }
    if ((& id -u) -eq '0') { throw 'Run as your ordinary WSL/Linux user, without sudo/root.' }
    if (-not (Test-Path -LiteralPath '/bin/bash')) { throw '/bin/bash is required.' }
    $null = Get-Command tar -CommandType Application -ErrorAction Stop
}
$architecture = if ($runningWindows) {
    if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
} else { (& uname -m).Trim() }
$arm64 = $architecture -match '^(ARM64|aarch64)$'
if (-not $arm64 -and $architecture -notmatch '^(AMD64|x86_64)$') { throw "Only x64 and ARM64 are supported: $architecture" }

$uvAssets = @{
    'windows-x64' = @('uv-x86_64-pc-windows-msvc.zip', 'cae6a3bc25239f83dffb467a4b180508d9da23986c04639ebfa44e43e6a84bff')
    'windows-arm64' = @('uv-aarch64-pc-windows-msvc.zip', '17f27b1c64eacc757ae603579f116a014881e486c5e79ae81877980d4699e943')
    'linux-x64' = @('uv-x86_64-unknown-linux-gnu.tar.gz', '89eadd7c76fc063887959510d5ba0ab1264dfd5f1143b925ddb73021a40acf16')
    'linux-arm64' = @('uv-aarch64-unknown-linux-gnu.tar.gz', 'afb6291f3f0a6b4521fc67b947822506c41dde5b60d2189dd8f3695b2ac8c9e7')
}
$assetKey = $(if ($runningWindows) { 'windows-' } else { 'linux-' }) + $(if ($arm64) { 'arm64' } else { 'x64' })
$uvAsset = $uvAssets[$assetKey]
$oldEnvironment = @{}
$managedCreated = $false
$state = [ordered]@{
    schemaVersion = 1; action = 'Install'; status = 'installing'; installed = $false
    sourceCommit = $hermesCommit; uvVersion = $uvVersion; pythonRequest = '3.11'
    endpoint = $queueUrl; model = $modelName; environment = $environmentContract
    runtimeStarted = $false; inferenceTested = $false; windowsGatewayCompatible = $runningWindows
    sourceUrl = "https://codeload.github.com/NousResearch/hermes-agent/zip/$hermesCommit"
    sourceArchiveSha256 = $null; resultPath = $resultPath
}

try {
    $null = New-Item -ItemType Directory -Path $managedRoot
    $managedCreated = $true
    $downloadRoot = Join-Path $managedRoot 'downloads'
    $toolsRoot = Join-Path $managedRoot 'tools'
    $temporaryRoot = Join-Path $managedRoot 'tmp'
    foreach ($directory in @($downloadRoot, $toolsRoot, $temporaryRoot, $hermesHome)) {
        $null = New-Item -ItemType Directory -Path $directory -Force
    }
    Write-JsonFile $resultPath $state
    $processSettings = @{
        UV_CACHE_DIR = (Join-Path $managedRoot 'cache')
        UV_PYTHON_INSTALL_DIR = (Join-Path $managedRoot 'python')
        UV_PYTHON_BIN_DIR = (Join-Path $managedRoot 'bin')
        UV_PROJECT_ENVIRONMENT = $venvRoot
        UV_TOOL_DIR = (Join-Path $toolsRoot 'uv-tools')
        UV_TOOL_BIN_DIR = (Join-Path $managedRoot 'bin')
        UV_PYTHON_INSTALL_REGISTRY = '0'; UV_PYTHON_INSTALL_BIN = '0'; UV_NO_CONFIG = '1'
        UV_PYTHON = ''; UV_INDEX = ''; UV_DEFAULT_INDEX = ''; UV_INDEX_URL = ''; UV_EXTRA_INDEX_URL = ''
        UV_CONFIG_FILE = ''; UV_PYTHON_INSTALL_MIRROR = ''; UV_PYTHON_DOWNLOADS = 'automatic'
        PYTHONPATH = ''; PYTHONHOME = ''; PYTHONNOUSERSITE = '1'; PYTHONDONTWRITEBYTECODE = '1'; VIRTUAL_ENV = ''
        HERMES_HOME = $hermesHome; HERMES_CONFIG = ''; HERMES_ENV = ''; HERMES_PROFILE = ''
        TMPDIR = $temporaryRoot; TEMP = $temporaryRoot; TMP = $temporaryRoot
    }
    foreach ($name in $processSettings.Keys) {
        $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $processSettings[$name], 'Process')
    }
    $uvArchive = Join-Path $downloadRoot $uvAsset[0]
    $null = Save-Download "https://github.com/astral-sh/uv/releases/download/$uvVersion/$($uvAsset[0])" $uvArchive $uvAsset[1]
    $uvRoot = Join-Path $toolsRoot 'uv'
    if ($runningWindows) {
        Expand-Archive -LiteralPath $uvArchive -DestinationPath $uvRoot
    } else {
        $null = New-Item -ItemType Directory -Path $uvRoot
        Invoke-InstallCommand 'tar' @('-xzf', $uvArchive, '-C', $uvRoot)
    }
    $uvName = if ($runningWindows) { 'uv.exe' } else { 'uv' }
    $uvExecutables = @(Get-ChildItem -LiteralPath $uvRoot -Filter $uvName -File -Recurse)
    if ($uvExecutables.Count -ne 1) { throw 'The pinned uv archive must contain exactly one uv executable.' }
    $uvExe = $uvExecutables[0].FullName

    if ($runningWindows) {
        $gitAsset = if ($arm64) { 'PortableGit-2.55.0.5-arm64.7z.exe' } else { 'PortableGit-2.55.0.5-64-bit.7z.exe' }
        $gitHash = if ($arm64) { '49d1dd3158017fa9805d07268433dbab7021b2ec1c1cc3fbabaf8b8255764dd0' } else { '5aa8a20f6e9abb2c755f0e73c91c687701a46b309ad84a0ca6509380fa4ae290' }
        $gitArchive = Join-Path $downloadRoot $gitAsset
        $null = Save-Download "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.5/$gitAsset" $gitArchive $gitHash
        Write-Phase 'Extracting official PortableGit inside the installation root.'
        Assert-ManagedPath $gitRoot
        $gitProcess = Start-Process -FilePath $gitArchive -ArgumentList @('-y', ('-o"' + $gitRoot + '"')) -WindowStyle Hidden -Wait -PassThru
        if ($gitProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $bashExe)) { throw 'PortableGit extraction failed or bash.exe is missing.' }
        $oldEnvironment['PATH'] = [Environment]::GetEnvironmentVariable('PATH', 'Process')
        $env:PATH = (Join-Path $gitRoot 'cmd') + [IO.Path]::PathSeparator + $env:PATH
    }
    $sourceArchive = Join-Path $downloadRoot ('hermes-' + $hermesCommit + '.zip')
    $state.sourceArchiveSha256 = Save-Download $state.sourceUrl $sourceArchive ''
    $unpackRoot = Join-Path $managedRoot 'source-unpack'
    Expand-Archive -LiteralPath $sourceArchive -DestinationPath $unpackRoot
    $unpackedSource = Join-Path $unpackRoot ('hermes-agent-' + $hermesCommit)
    # Validate both absolute targets before moving a directory, never across shells.
    Assert-ManagedPath $unpackedSource
    Assert-ManagedPath $sourceRoot
    if (-not (Test-Path -LiteralPath (Join-Path $unpackedSource 'uv.lock'))) { throw 'Pinned Hermes source is missing uv.lock.' }
    Move-Item -LiteralPath $unpackedSource -Destination $sourceRoot
    Write-Phase 'Installing private Python 3.11 (no global executable or registry entry).'
    Invoke-InstallCommand $uvExe @('python', 'install', '3.11', '--no-bin', '--no-registry', '--no-config')
    Write-Phase 'Installing Hermes core and messaging dependencies from the upstream lockfile.'
    Invoke-InstallCommand $uvExe @('sync', '--project', $sourceRoot, '--directory', $sourceRoot, '--python', '3.11', '--managed-python', '--frozen', '--no-dev', '--extra', 'messaging')
    if (-not (Test-Path -LiteralPath $pythonExe)) { throw 'The requested virtual environment was not created.' }

    # Deliberately contains no real credential or consent acknowledgement.
    $configText = @'
model:
  provider: custom
  default: qwen3.8-27b
  base_url: http://127.0.0.1:18022/v1
  api_key: local-qwen-no-secret
  api_mode: chat_completions
auxiliary:
'@
    # Independent blocks (not YAML aliases) allow later per-task editing.
    $auxiliaryTasks = @('compression', 'vision', 'skills_hub', 'approval', 'review', 'mcp',
        'title_generation', 'memory_query_rewrite', 'tts_audio_tags', 'triage_specifier',
        'kanban_decomposer', 'profile_describer', 'goal_judge', 'curator', 'monitor',
        'background_review', 'moa_reference', 'moa_aggregator')
    foreach ($auxiliaryTask in $auxiliaryTasks) {
        $configText += "`n  ${auxiliaryTask}:`n    provider: main`n    model: $modelName`n    base_url: $queueUrl`n    api_key: local-qwen-no-secret"
    }
    $configText += "`n" + @'
delegation:
  provider: custom
  model: qwen3.8-27b
  base_url: http://127.0.0.1:18022/v1
  api_key: local-qwen-no-secret
  api_mode: chat_completions
terminal:
  backend: local
'@
    [IO.File]::WriteAllText((Join-Path $hermesHome 'config.yaml'), $configText + "`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $hermesHome '.env'), "# Local queue placeholder; this is not a secret.`nOPENAI_BASE_URL=$queueUrl`nOPENAI_API_KEY=local-qwen-no-secret`nHERMES_MODEL=$modelName`n", $utf8)
    $state.status = 'installed'
    $state.installed = $true
    Write-JsonFile $resultPath $state
    $verification = Get-Verification
    if (-not $verification.installed) { throw 'Post-install metadata verification failed.' }
    $state['packageVersion'] = $verification.packageVersion
    Write-JsonFile $resultPath $state
    Write-Phase 'Installed. Hermes, Telegram, and Qwen servers have not been started.'
    $state | ConvertTo-Json -Depth 12
} catch {
    if ($managedCreated) {
        $state.status = 'failed'; $state.installed = $false
        $state['error'] = $_.Exception.Message
        Write-JsonFile $resultPath $state
    }
    throw
} finally {
    foreach ($name in $oldEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process')
    }
}
