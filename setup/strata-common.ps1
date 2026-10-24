# Pure parsing/profile/identity helpers, also used by offline tests. No downloads or process starts.
function Strata-Origin([string]$Value) {
    $uri=$null
    if (-not [Uri]::TryCreate($Value,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -ne 'http' -or $uri.Host -notin @('127.0.0.1','localhost','[::1]') -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') { throw 'Strata URL은 인증·경로 없는 loopback HTTP 주소여야 합니다.' }
    return $uri.GetLeftPart([UriPartial]::Authority)
}
function Strata-Identity($Health,$Models,[string]$Expected='') {
    if ($Health.service -ne 'strata' -or $Health.loaded -isnot [bool] -or [string]::IsNullOrWhiteSpace($Health.model) -or $Health.model -match '[\r\n]') { throw 'Strata health 형식이 잘못됐습니다.' }
    if ($Expected -and $Expected -cne $Health.model) { throw '선택한 모델과 실제 Strata 모델이 다릅니다.' }
    $entry=@($Models.data | Where-Object { $_.id -ceq $Health.model })
    if ($entry.Count -ne 1) { throw '실제 모델이 /v1/models에 없거나 중복됐습니다.' }
    $limits=@();foreach ($n in @($Health.max_context,$entry[0].meta.n_ctx)) { if ($null -ne $n) { if ($n -is [string] -or $n -is [bool] -or $n -le 4096 -or [math]::Floor($n) -ne $n) { throw 'Strata 문맥 용량이 잘못됐습니다.' };$limits+= [long]$n } }
    if (-not $limits.Count) { throw '실제 Strata 문맥 용량을 확인할 수 없습니다.' }
    return [ordered]@{backend='strata';model=$Health.model;contextTokens=($limits|Measure-Object -Minimum).Minimum;loaded=$Health.loaded;vision=$false}
}
function Strata-Get([string]$Origin,[string]$Endpoint) {
    # HttpWebRequest with redirects disabled also works in Windows PowerShell 5.1.
    $request=[Net.HttpWebRequest]::Create((Strata-Origin $Origin)+$Endpoint)
    $request.AllowAutoRedirect=$false;$request.Timeout=3000;$request.ReadWriteTimeout=3000;$request.Proxy=$null
    $response=$null;$reader=$null
    try { $response=$request.GetResponse();if ([int]$response.StatusCode -ne 200) { throw 'Strata returned a non-200 response.' };$reader=New-Object IO.StreamReader($response.GetResponseStream());return $reader.ReadToEnd()|ConvertFrom-Json }
    finally { if ($reader) {$reader.Dispose()};if ($response) {$response.Dispose()} }
}
function Strata-Quote([string]$Text) {
    if ($Text -match '["\r\n]' -or $Text.EndsWith('\')) { throw '명령 경로의 따옴표·줄바꿈·마지막 역슬래시를 확인하세요.' }
    return '"'+$Text+'"'
}
function Strata-LaunchArguments([string]$Entry,[string]$Config,[int]$Gpu) {
    if ($Gpu -lt 0) { throw '명시적 GPU index가 필요합니다.' }
    return '-I '+(Strata-Quote (Strata-FullPath $Entry))+' --engine strata --host 127.0.0.1 --port 18021 --config '+(Strata-Quote (Strata-FullPath $Config))+' --gpu '+$Gpu
}
function Strata-FullPath([string]$Value) {
    if (-not $Value -or -not [IO.Path]::IsPathRooted($Value) -or $Value -match '["\r\n]') { throw '절대 경로를 지정하세요.' }
    return [IO.Path]::GetFullPath($Value).TrimEnd('\')
}
function Strata-Hash([string]$Path,[string]$Expected,[long]$Bytes=-1) {
    $item=Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer -or ($Bytes -ge 0 -and $item.Length -ne $Bytes)) { throw ('파일 크기가 다릅니다: '+$item.Name) }
    $hash=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Expected -and $hash -ne $Expected.ToLowerInvariant()) { throw ('SHA256 불일치: '+$item.Name) }
    return $hash
}
function Strata-Arguments($Config) {
    $result=@{};$flags=@('--pack','--native','--ple-gguf','--expert-profile','--expert-cache','--prefill','--spec','--spec-min-p','--mtp','--max-context','--kv','--kv-resident','--vram-reserve-mib','--pool-workers','--resident-budget-gib')
    for ($i=0;$i -lt $Config.args.Count;$i++) {
        $key=[string]$Config.args[$i]
        if ($key -eq '--vision') { continue }
        if ($key -notin $flags -or $result.ContainsKey($key) -or $i+1 -ge $Config.args.Count) { throw ('지원하지 않거나 중복된 Strata 설정 인자: '+$key) }
        $i++;$result[$key]=[string]$Config.args[$i]
    }
    foreach ($key in @('--pack','--native','--ple-gguf','--mtp')) { if (-not $result.ContainsKey($key)) { throw ('준비된 SC117 설정에 필요한 인자 없음: '+$key) } }
    return $result
}
function Strata-Profile($Pins,[string]$Profile,[int]$ContextTokens=262144,[int]$PoolWorkers=10,[double]$ResidentBudgetGiB=26,[string]$Prefill='') {
    if ($Profile -notin @('IQ3_XXS','IQ2_XS') -or $ContextTokens -lt 8192 -or $ContextTokens -gt 262144 -or $PoolWorkers -lt 1 -or $PoolWorkers -gt 64 -or $ResidentBudgetGiB -lt 1 -or $ResidentBudgetGiB -gt 512) { throw 'Strata 프로필 범위를 확인하세요.' }
    if (-not $Prefill) { $Prefill=$Pins.profiles.$Profile.prefill }
    if ($Prefill -ne 'auto' -and ($Prefill -notmatch '^\d+$' -or [long]$Prefill -lt 512 -or [long]$Prefill -gt 262144)) { throw 'prefill은 auto 또는 512~262144입니다.' }
    return [ordered]@{quant=$Profile;contextTokens=$ContextTokens;poolWorkers=$PoolWorkers;residentBudgetGiB=$ResidentBudgetGiB;prefill=$Prefill;kv='int8';kvResident=[math]::Min(32768,$ContextTokens);spec=4;specMinP='0.5';vramReserveMiB=1280;draftVocab='cjk'}
}
function Strata-SameInstant($Left,$Right) {
    $ticks=@()
    foreach ($value in @($Left,$Right)) {
        if ($value -is [DateTimeOffset]) { $ticks+=$value.UtcDateTime.Ticks }
        elseif ($value -is [DateTime]) {
            if ($value.Kind -eq [DateTimeKind]::Unspecified) { return $false }
            $ticks+=$value.ToUniversalTime().Ticks
        } elseif ($value -is [string]) {
            $parsed=[DateTimeOffset]::MinValue
            # Require an explicit zone and all seven fractional digits from the saved roundtrip format.
            $formats=[string[]]@("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'","yyyy-MM-dd'T'HH:mm:ss.fffffffzzz")
            if (-not [DateTimeOffset]::TryParseExact($value,$formats,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal,[ref]$parsed)) { return $false }
            $ticks+=$parsed.UtcDateTime.Ticks
        } else { return $false }
    }
    return $ticks[0] -eq $ticks[1]
}
function Strata-Owned($Saved,$Process,[string]$Root,[string]$Config) {
    if (-not $Saved -or -not $Process -or -not $Process.CommandLine -or -not $Process.ExecutablePath) { return $false }
    return $Saved.root -eq $Root -and $Saved.config -eq $Config -and $Saved.backend -eq 'strata' -and [int]$Process.ProcessId -eq [int]$Saved.pid -and $Process.ExecutablePath -eq $Saved.executable -and $Process.CreationDate.ToUniversalTime().ToString('o') -eq $Saved.created -and $Process.CommandLine.Contains((Strata-Quote $Config)) -and $Process.CommandLine.Contains((Strata-Quote $Saved.entry))
}
function Strata-WriteJson([string]$Path,$Data) {
    $temp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::WriteAllText($temp,($Data|ConvertTo-Json -Depth 14),(New-Object Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp,$Path,[System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temp,$Path) }
}
