param([Parameter(ValueFromRemainingArguments=$true)][string[]]$AppArguments)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dataRoot = Join-Path $env:LOCALAPPDATA 'QwenStatus'

try {
    [AppDomain]::CurrentDomain.SetData('QwenStatusRoot', $sourceRoot)
    $types = Add-Type -Path @(
        (Join-Path $sourceRoot 'QwenStatus.cs'),
        (Join-Path $sourceRoot 'TokenTestWindow.cs'),
        (Join-Path $sourceRoot 'QwenInsights.cs'),
        (Join-Path $sourceRoot 'QwenOperations.cs'),
        (Join-Path $sourceRoot 'QwenTelemetry.cs'),
        (Join-Path $sourceRoot 'QwenExperience.cs'),
        (Join-Path $sourceRoot 'QwenChatWindow.cs'),
        (Join-Path $sourceRoot 'QwenDelegation.cs')
    ) -ReferencedAssemblies @(
        'System.Windows.Forms', 'System.Drawing', 'System.Web.Extensions'
    ) -PassThru -WarningAction SilentlyContinue
    $appType = $types | Where-Object Name -eq 'QwenStatus' | Select-Object -First 1
    if ($null -eq $appType) { throw 'QwenStatus type was not compiled.' }
    $main = $appType.GetMethod('Main', [Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $main) { throw 'QwenStatus.Main was not found.' }
    $parameters = New-Object object[] 1
    $parameters[0] = [string[]]$AppArguments
    [void]$main.Invoke($null, $parameters)
} catch {
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    ($_ | Out-String) | Set-Content -LiteralPath (Join-Path $dataRoot 'source-launch-error.log') -Encoding UTF8
    throw
}
