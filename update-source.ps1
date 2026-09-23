param(
    [ValidateSet('Check','Install','Rollback')][string]$Mode = 'Check',
    [string]$Target = (Split-Path -Parent $MyInvocation.MyCommand.Path),
    [string]$Repository
)
$ErrorActionPreference = 'Stop'
$Target = (Resolve-Path -LiteralPath $Target).Path
if (-not $Repository) {
    $Repository = if (Test-Path -LiteralPath (Join-Path $Target '.git')) { $Target } else { Join-Path (Split-Path -Parent $Target) 'QwenStatus-public' }
}
$Repository = (Resolve-Path -LiteralPath $Repository).Path
if (-not (Test-Path -LiteralPath (Join-Path $Repository '.git'))) { throw 'GitHub 원본 저장소가 없습니다. QwenStatus-public 체크아웃을 확인하세요.' }
$remote = (& git -C $Repository remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $remote -notmatch '^https://github\.com/kea9997/qwen-status-tray(?:\.git)?$') { throw '예상한 GitHub 원본이 아닙니다: ' + $remote }
$files = @('QwenStatus.cs','TokenTestWindow.cs','QwenInsights.cs','QwenOperations.cs','QwenTelemetry.cs','QwenExperience.cs','run-source.ps1','update-source.ps1','build.cmd','README.md','settings.example.json')
$backupRoot = Join-Path $env:LOCALAPPDATA 'QwenStatus\update-backups'
$versionFile = Join-Path $Target 'source-version.txt'
if ($Mode -eq 'Rollback') {
    $backup = Get-ChildItem -LiteralPath $backupRoot -Directory -ErrorAction SilentlyContinue | Where-Object Name -Match '^\d{8}-\d{6}-\d{3}$' | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $backup) { throw '복구할 이전 버전이 없습니다.' }
    foreach ($name in $files + 'source-version.txt') {
        $old = Join-Path $backup.FullName $name
        $destination = Join-Path $Target $name
        if (Test-Path -LiteralPath $old) { Copy-Item -LiteralPath $old -Destination $destination -Force }
        elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
    }
    Rename-Item -LiteralPath $backup.FullName -NewName ($backup.Name + '.restored')
    Write-Output ('복구 완료: ' + $backup.Name)
    return
}
& git -C $Repository fetch --quiet origin main
if ($LASTEXITCODE -ne 0) { throw 'GitHub 버전 확인에 실패했습니다.' }
$revision = (& git -C $Repository rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') { throw 'GitHub 버전을 확인하지 못했습니다.' }
$current = if (Test-Path -LiteralPath $versionFile) { (Get-Content -LiteralPath $versionFile -Raw).Trim() } elseif ($Target -eq $Repository) { (& git -C $Repository rev-parse HEAD).Trim() } else { '설치 버전 미기록' }
Write-Output ('설치: ' + $current)
Write-Output ('GitHub: ' + $revision)
if ($Mode -eq 'Check') { return }
if ($current -eq $revision) { Write-Output '이미 최신 버전입니다.'; return }
$temp = Join-Path $env:TEMP ('qwen-update-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $zip = Join-Path $temp 'source.zip'
    & git -C $Repository archive --format=zip --output=$zip $revision -- $files
    if ($LASTEXITCODE -ne 0) { throw '소스 압축을 만들지 못했습니다.' }
    $stage = Join-Path $temp 'source'
    Expand-Archive -LiteralPath $zip -DestinationPath $stage
    foreach ($name in $files) { if (-not (Test-Path -LiteralPath (Join-Path $stage $name))) { throw ('소스 파일 누락: ' + $name) } }
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (-not (Test-Path -LiteralPath $csc)) { throw '.NET Framework C# 컴파일러를 찾지 못했습니다.' }
    & $csc /nologo /target:winexe ('/out:' + (Join-Path $temp 'check.exe')) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll (Join-Path $stage 'QwenStatus.cs') (Join-Path $stage 'TokenTestWindow.cs') (Join-Path $stage 'QwenInsights.cs') (Join-Path $stage 'QwenOperations.cs') (Join-Path $stage 'QwenTelemetry.cs') (Join-Path $stage 'QwenExperience.cs')
    if ($LASTEXITCODE -ne 0) { throw '새 소스 컴파일 검사에 실패했습니다. 기존 앱을 유지합니다.' }
    foreach ($case in @('core-test','insights-test','operations-test','experience-test')) {
        $arguments = '-NoProfile -STA -ExecutionPolicy RemoteSigned -File "' + (Join-Path $stage 'run-source.ps1') + '" --' + $case
        $started = Get-Date
        $testProcess = Start-Process -FilePath powershell.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if (-not $testProcess.WaitForExit(30000)) { Stop-Process -Id $testProcess.Id -Force; throw ('새 소스 자체 검사가 시간 초과: ' + $case) }
        if ($testProcess.ExitCode -ne 0) { throw ('새 소스 자체 검사 실패: ' + $case) }
        $resultPath = Join-Path $env:LOCALAPPDATA ('QwenStatus\' + $case + '-result.txt')
        if ($case -eq 'insights-test') { $resultPath = Join-Path $env:LOCALAPPDATA 'QwenStatus\insights-test.txt' }
        if ($case -eq 'operations-test') { $resultPath = Join-Path $env:LOCALAPPDATA 'QwenStatus\operations-test.txt' }
        if ($case -eq 'experience-test') { $resultPath = Join-Path $env:LOCALAPPDATA 'QwenStatus\experience-test.txt' }
        $result = Get-Item -LiteralPath $resultPath -ErrorAction SilentlyContinue
        if (-not $result -or $result.LastWriteTime -lt $started.AddSeconds(-2) -or -not ((Get-Content -LiteralPath $resultPath -Raw).Trim().StartsWith('PASS'))) { throw ('새 소스 자체 검사 결과 확인 실패: ' + $case) }
    }
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $backup = Join-Path $backupRoot (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    New-Item -ItemType Directory -Path $backup | Out-Null
    foreach ($name in $files + 'source-version.txt') {
        $old = Join-Path $Target $name
        if (Test-Path -LiteralPath $old) { Copy-Item -LiteralPath $old -Destination (Join-Path $backup $name) }
    }
    try {
        foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $Target $name) -Force }
        Set-Content -LiteralPath $versionFile -Value $revision -Encoding ASCII
    } catch {
        foreach ($name in $files + 'source-version.txt') {
            $old = Join-Path $backup $name
            $destination = Join-Path $Target $name
            if (Test-Path -LiteralPath $old) { Copy-Item -LiteralPath $old -Destination $destination -Force }
            elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
        }
        throw
    }
    Write-Output ('설치 완료: ' + $revision)
    Write-Output ('복구 파일: ' + $backup)
} finally {
    $resolvedTemp = (Resolve-Path -LiteralPath $temp -ErrorAction SilentlyContinue).Path
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if ($resolvedTemp -and $resolvedTemp.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolvedTemp) -match '^qwen-update-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
