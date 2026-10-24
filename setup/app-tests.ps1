param([string]$Executable=(Join-Path (Split-Path -Parent $PSScriptRoot) 'QwenStatus.new.exe'),[string]$ResultsDirectory)
$ErrorActionPreference='Stop'
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Build the Windows app first (build.cmd).' }
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$base=if($env:RUNNER_TEMP){$env:RUNNER_TEMP}else{[IO.Path]::GetTempPath()}
$temp=if($ResultsDirectory){$ResultsDirectory}else{Join-Path $base ('qwen-app-tests-'+[guid]::NewGuid().ToString('N'))}
if (-not [IO.Path]::IsPathRooted($temp) -or (Test-Path -LiteralPath $temp)) { throw 'Offline results require a new absolute directory.' }
$settings=Join-Path $env:LOCALAPPDATA 'QwenStatus\settings.json'
$before=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings).Hash}else{''}
$prior=$env:QWEN_SELF_TEST_DIR
try {
    $env:QWEN_SELF_TEST_DIR=$temp
    foreach ($case in @('core-test','insights-test','operations-test','experience-test','chat-test','delegation-test','install-test','strata-test','status-test','token-test-ui')) {
        $process=Start-Process -FilePath $Executable -ArgumentList ('--'+$case) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(60000)) { throw ('Offline app test timed out (inspect its exact PID '+$process.Id+'): '+$case) }
        $name=if($case -eq 'core-test'){'core-test-result.txt'}elseif($case -eq 'token-test-ui'){'token-test-ui.txt'}else{$case+'.txt'}
        if ($process.ExitCode -ne 0) {
            $failure=Get-Content -LiteralPath (Join-Path $temp $name) -Raw -ErrorAction SilentlyContinue
            throw ('Offline app test failed: '+$case+' · exit '+$process.ExitCode+' · '+$temp+[Environment]::NewLine+$failure)
        }
        $result=Get-Content -LiteralPath (Join-Path $temp $name) -Raw
        if (-not $result.StartsWith('PASS')) { throw ($case+': '+$result) }
        Write-Output ($case+': '+$result.Trim())
    }
    $after=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings).Hash}else{''}
    if($before -ne $after){throw 'Real application settings changed during offline checks.'}
    Write-Output ('Offline previews/results: '+$temp)
} finally {
    $env:QWEN_SELF_TEST_DIR=$prior
    $after=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings).Hash}else{''}
    if($before -ne $after){throw 'Real application settings changed during offline checks.'}
}
