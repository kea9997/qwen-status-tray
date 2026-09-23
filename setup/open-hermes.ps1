param([Parameter(Mandatory=$true)][string]$InstallRoot)
$ErrorActionPreference='Stop'
[Console]::InputEncoding=New-Object Text.UTF8Encoding $false
[Console]::OutputEncoding=New-Object Text.UTF8Encoding $false
$env:PYTHONUTF8='1'
$record=Get-Content -LiteralPath (Join-Path $InstallRoot 'hermes\install-result.json') -Raw | ConvertFrom-Json
if ($record.status -ne 'installed') { throw 'Hermes 설치가 완료되지 않았습니다. 설치 도우미에서 확인하세요.' }
foreach ($name in @('QWEN_HERMES_PYTHON','QWEN_HERMES_ROOT','QWEN_HERMES_HOME','QWEN_HERMES_GIT_BASH')) {
    $path=$record.environment.$name
    if (-not $path -or -not [IO.Path]::IsPathRooted($path) -or -not (Test-Path -LiteralPath $path)) { throw ('Hermes 경로를 확인하세요: '+$name) }
}
$env:HERMES_HOME=$record.environment.QWEN_HERMES_HOME
$env:HERMES_GIT_BASH_PATH=$record.environment.QWEN_HERMES_GIT_BASH
$env:HERMES_PROFILE=$null;$env:HERMES_CONFIG=$null;$env:HERMES_ENV=$null
Set-Location -LiteralPath $record.environment.QWEN_HERMES_ROOT
& $record.environment.QWEN_HERMES_PYTHON -m hermes_cli.main
if ($LASTEXITCODE -ne 0) { throw ('Hermes 종료 코드: '+$LASTEXITCODE) }
