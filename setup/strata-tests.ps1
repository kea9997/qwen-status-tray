# Offline parser/profile/ownership tests. No model downloads, process starts, live ports or settings writes.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'strata-common.ps1')
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backends.json') -Raw|ConvertFrom-Json
$pins=$manifest.strata
if (@($manifest.PSObject.Properties.Name | Where-Object { $_ -notin @('schemaVersion','strata') }).Count) { throw 'Non-Strata profiles remain in the public manifest.' }
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock]$Operation,[string]$Message) { $rejected=$false;try{& $Operation|Out-Null}catch{$rejected=$true};Assert $rejected $Message }
Assert ((Strata-Origin 'http://127.0.0.1:8088') -eq 'http://127.0.0.1:8088') 'Explicit loopback attach failed'
foreach ($url in @('https://127.0.0.1:8088','http://example.com','http://user:pass@localhost','http://localhost/v1','http://localhost/?x=1','http://localhost/#x')) { Reject {Strata-Origin $url} ('Unsafe origin accepted: '+$url) }
$health='{"service":"strata","loaded":false,"model":"fixture-fn","max_context":32768}'|ConvertFrom-Json
$models='{"data":[{"id":"fixture-fn","meta":{"n_ctx":16384}}]}'|ConvertFrom-Json
$identity=Strata-Identity $health $models
Assert (-not $identity.loaded -and $identity.model -eq 'fixture-fn' -and $identity.contextTokens -eq 16384) 'Unloaded/model/actual capacity mapping failed'
Reject { Strata-Identity $health $models 'wrong-model' } 'Wrong model accepted'
Reject { Strata-Identity $health ('{"data":[]}'|ConvertFrom-Json) } 'Missing served model accepted'
foreach ($quant in @('IQ3_XXS','IQ2_XS')) {
    $profile=Strata-Profile $pins $quant
    Assert ($profile.contextTokens -eq 262144 -and $profile.kv -eq 'int8' -and $profile.kvResident -eq 32768 -and $profile.poolWorkers -eq 10 -and $profile.residentBudgetGiB -eq 26 -and $profile.spec -eq 4 -and $profile.specMinP -eq '0.5' -and $profile.vramReserveMiB -eq 1280) 'Measured-example defaults changed'
    Assert ($profile.prefill -eq $(if($quant -eq 'IQ3_XXS'){'auto'}else{'16384'})) 'Quant-specific prefill changed'
    Assert ($pins.profiles.$quant.sha256 -match '^[0-9a-f]{64}$') 'Missing weight digest'
}
$small=Strata-Profile $pins 'IQ2_XS' 16384 8 22 '8192'
Assert ($small.kvResident -eq 16384 -and $small.poolWorkers -eq 8 -and $small.residentBudgetGiB -eq 22) 'Explicit tuning ignored'
Reject {Strata-Profile $pins 'IQ3_XXS' 524288} 'Unvalidated long context accepted'
Reject {Strata-Profile $pins 'IQ3_XXS' 262144 10 26 'auto;unsafe'} 'Invalid prefill accepted'
$config='{"args":["--pack","C:\\Models\\pack","--native","C:\\Models\\first.gguf","--ple-gguf","C:\\Models\\second.gguf","--mtp","C:\\Models\\mtp","--vision"]}'|ConvertFrom-Json
Assert ((Strata-Arguments $config)['--native'] -eq 'C:\Models\first.gguf') 'Path with native shard was lost'
$config.args+=@('--approx-route-skip','on');Reject {Strata-Arguments $config} 'Approximate route skipping accepted'
$root='C:\Apps\한글 Qwen';$path=$root+'\strata\server.json';$entry='C:\Sources\Strata\serve\server.py';$created=[DateTime]::UtcNow
$saved=[pscustomobject]@{root=$root;config=$path;backend='strata';pid=123;executable='C:\Python\Scripts\python.exe';entry=$entry;created=$created.ToUniversalTime().ToString('o')}
$launch=Strata-LaunchArguments $entry $path 2
Assert ($launch -ceq ('-I "'+$entry+'" --engine strata --host 127.0.0.1 --port 18021 --config "'+$path+'" --gpu 2')) 'Launch fell back to upstream mock/8095 or lost explicit GPU/config'
Reject {Strata-LaunchArguments $entry $path -1} 'Launch accepted an implicit GPU'
$process=[pscustomobject]@{ProcessId=123;ExecutablePath=$saved.executable;CreationDate=$created;CommandLine='python '+$launch}
Assert (Strata-Owned $saved $process $root $path) 'Exact owned identity rejected'
$process.ProcessId=124;Assert (-not (Strata-Owned $saved $process $root $path)) 'PID mismatch accepted';$process.ProcessId=123
$process.CreationDate=$created.AddSeconds(1);Assert (-not (Strata-Owned $saved $process $root $path)) 'Reused PID accepted';$process.CreationDate=$created
$process.ExecutablePath='C:\Other\python.exe';Assert (-not (Strata-Owned $saved $process $root $path)) 'Executable mismatch accepted';$process.ExecutablePath=$saved.executable
Assert (-not (Strata-Owned $saved $process 'C:\Other' $path)) 'Other install root accepted'
Assert (-not (Strata-Owned $saved $process $root 'C:\Other\config.json')) 'Other config accepted'
Reject {Strata-Quote 'C:\bad"path'} 'Quote injection accepted'
$temp=Join-Path ([IO.Path]::GetTempPath()) ('qwen-strata-parser-'+[guid]::NewGuid().ToString('N'))
# Plan does not inspect hardware or connect to a live server, and must not create its target.
$shell=(Get-Process -Id $PID).Path
$plan=& $shell -NoProfile -File (Join-Path $PSScriptRoot 'strata-install.ps1') -Action Plan -InstallRoot $temp -Profile IQ2_XS -Json
Assert ($LASTEXITCODE -eq 0 -and -not (Test-Path -LiteralPath $temp)) 'Strata Plan mutated target or failed'
$plan=$plan|ConvertFrom-Json
Assert (-not $plan.startsModelServer -and -not $plan.downloads -and -not $plan.dockerRequired -and -not $plan.automaticEngineAcquisition -and $plan.profile.quant -eq 'IQ2_XS') 'Plan falsely promised acquisition or started a model'
Write-Output 'PASS: offline Strata origin/identity/context/profile/config/ownership parsing and read-only native plan; no downloads/GPU starts/live ports/settings'
$global:LASTEXITCODE=0
