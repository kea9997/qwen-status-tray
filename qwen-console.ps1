param([ValidateSet('chat','agent')][string]$Mode='chat')
$ErrorActionPreference='Stop'; [Console]::InputEncoding=[Text.UTF8Encoding]::new(); [Console]::OutputEncoding=[Text.UTF8Encoding]::new()
$settingsPath=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'QwenStatus\settings.json'
$settings=if(Test-Path -LiteralPath $settingsPath){Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json}else{$null}
$base=if($settings -and $settings.uiUrl){$settings.uiUrl.TrimEnd('/')}else{'http://127.0.0.1:18022/ui'}
if(([uri]$base).Scheme -ne 'http' -or -not ([uri]$base).IsLoopback){throw 'UI endpoint must be local HTTP; the UI token is never sent to a remote host.'}
$gatewayRoot=if($settings -and $settings.gatewayDirectory){[Environment]::ExpandEnvironmentVariables($settings.gatewayDirectory)}else{Join-Path (Split-Path -Parent $PSScriptRoot) 'qwen-gateway'}
if(-not [IO.Path]::IsPathRooted($gatewayRoot)){$gatewayRoot=Join-Path $PSScriptRoot $gatewayRoot}
$token=(Get-Content -LiteralPath (Join-Path $gatewayRoot 'ui-token.txt') -Raw).Trim(); $session=[guid]::NewGuid().ToString(); $cwd=Join-Path (Split-Path -Parent $PSScriptRoot) 'QwenWork'; if(-not (Test-Path -LiteralPath $cwd)){$cwd=[Environment]::GetFolderPath('MyDocuments')}
function Invoke-QwenJson([string]$uri,[string]$method='GET',[byte[]]$body=$null){
 $args=@{Uri=$uri;Method=$method;Headers=@{'X-UI-Token'=$token};UseBasicParsing=$true}
 if($null -ne $body){$args.ContentType='application/json; charset=utf-8';$args.Body=$body}
 $response=Invoke-WebRequest @args
 [Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray()) | ConvertFrom-Json
}
Write-Host "Qwen $Mode console - type exit to quit" -ForegroundColor Cyan
while($true){ $prompt=Read-Host 'Input'; if($prompt -eq 'exit'){break}; if([string]::IsNullOrWhiteSpace($prompt)){continue}; $body=@{mode=$Mode;prompt=$prompt;cwd=$cwd;session=$session}|ConvertTo-Json; try{$job=Invoke-QwenJson "$base/jobs" 'POST' ([Text.Encoding]::UTF8.GetBytes($body)); do{Start-Sleep -Milliseconds 500;$r=Invoke-QwenJson "$base/jobs/$($job.id)"; Write-Host -NoNewline "`rStatus: $($r.status)   "}while($r.status -eq 'running' -or $r.status -eq 'queued'); Write-Host "`n`n$($r.output)`n"}catch{Write-Host "`nError: $($_.Exception.Message)" -ForegroundColor Red}}
