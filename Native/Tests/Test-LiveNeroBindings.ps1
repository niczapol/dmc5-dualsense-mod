[CmdletBinding()]
param(
    [string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Devil May Cry 5',
    [string]$ExpectedNeroAttackLarge = '800',
    [string]$ExpectedNeroExceed = '200',
    [int]$TimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pluginLog = Join-Path $GameDirectory 'DMC5DualSense\plugin.log'
if (-not (Test-Path -LiteralPath $pluginLog -PathType Leaf)) {
    throw "Plugin log not found: $pluginLog"
}

function Get-LatestPluginSession {
    $lines = @(Get-Content -LiteralPath $pluginLog)
    $start = -1
    for ($index = $lines.Count - 1; $index -ge 0; --$index) {
        if ($lines[$index] -like '*=== native plugin session*') {
            $start = $index
            break
        }
    }
    if ($start -lt 0) { throw 'No native plugin session marker was found.' }
    return @($lines[$start..($lines.Count - 1)])
}

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$pattern = "Control bindings \(character SaveDataManager KeyAssign\): AttackL=0x$ExpectedNeroAttackLarge, Exceed=0x$ExpectedNeroExceed"
do {
    $binding = Get-LatestPluginSession |
        Select-String -Pattern $pattern |
        Select-Object -Last 1
    if ($null -ne $binding) {
        Write-Host "PASS live Nero saved-control contract: $($binding.Line)"
        exit 0
    }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $deadline)

throw "Live Nero binding contract did not report AttackL=0x$ExpectedNeroAttackLarge and Exceed=0x$ExpectedNeroExceed."
