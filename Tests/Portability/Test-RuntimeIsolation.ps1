[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativePackageDirectory,
    [Parameter(Mandatory)][string]$ManagedPackageDirectory,
    [string]$WorkingRoot = [IO.Path]::GetTempPath()
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if (Get-Process -Name 'DMC5DualSense.Bridge','DevilMayCry5' -ErrorAction SilentlyContinue) { throw 'Close game/runtime before isolated audit' }
$root=Join-Path ([IO.Path]::GetFullPath($WorkingRoot)) ('DMC5DS-runtime-audit-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Start-AuditProcess([string]$Exe, [string]$Arguments, [string]$Directory) {
    $info=[Diagnostics.ProcessStartInfo]::new($Exe, $Arguments)
    $info.WorkingDirectory=$Directory
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.RedirectStandardError=$true
    $info.RedirectStandardOutput=$true
    $info.Environment['DOTNET_DISABLE_GUI_ERRORS']='1'
    return [Diagnostics.Process]::Start($info)
}
function Observe-Exit($Process) {
    if (-not $Process.WaitForExit(10000)) { $Process.Kill(); $Process.WaitForExit(); return 'TIMEOUT (audit process stopped)' }
    return $Process.ExitCode
}
foreach ($variant in @('native','managed')) {
    $pkg=if ($variant -eq 'native') {$NativePackageDirectory} else {$ManagedPackageDirectory}
    $dir=Join-Path $root $variant
    New-Item -ItemType Directory -Path $dir | Out-Null
    $exe=Join-Path $dir 'DMC5DualSense.Bridge.exe'
    Copy-Item -LiteralPath (Join-Path $pkg 'DMC5DualSense.Bridge.exe') -Destination $exe
    $config=Join-Path $dir 'config.json'
    [IO.File]::WriteAllText($config,'{"EnableAdvancedHaptics":false,"Port":28753}')
    # No steam_api64.dll anywhere beside this fixture: no connection to real Steam/hardware.
    $process=Start-AuditProcess $exe '--probe' $dir
    $exit=Observe-Exit $process
    $results.Add([pscustomobject]@{Variant=$variant;Case='Missing Steam API reports probe failure';Pass=$exit -eq 2;Exit=$exit})

    $mutex=[Threading.Mutex]::new($false,'Local\DMC5DualSense.Bridge')
    try {
        $process=Start-AuditProcess $exe '--self-test-all' $dir
        $exit=Observe-Exit $process
        $results.Add([pscustomobject]@{Variant=$variant;Case='Self-test does not claim success while another instance exists';Pass=$exit -ne 0;Exit=$exit})
    } finally { $mutex.Dispose() }

    $parent=Start-AuditProcess "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -Command "Start-Sleep -Seconds 30"' $dir
    try {
        $process=Start-AuditProcess $exe ("--parent " + $parent.Id) $dir
        Start-Sleep -Milliseconds 1700
        $running=-not $process.HasExited
        $parent.Kill(); $parent.WaitForExit()
        $exit=Observe-Exit $process
        $results.Add([pscustomobject]@{Variant=$variant;Case='No-hardware session exits when its parent ends';Pass=$running -and $exit -eq 0;Exit=$exit;ReadyFileRemoved=-not (Test-Path -LiteralPath (Join-Path $dir 'bridge.ready.json'))})
    } finally { if (-not $parent.HasExited) {$parent.Kill()} }

    [IO.File]::WriteAllText($config,'{"EnableAdvancedHaptics":false,"Port":28753, INVALID')
    # Do not exercise old native default-on-error behavior against actual audio.
    $packageVersion=(Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json).Version
    if ($variant -eq 'managed' -or $packageVersion -notlike '1.7.3*') {
        $process=Start-AuditProcess $exe '--probe' $dir
        $exit=Observe-Exit $process
        $stderr=$process.StandardError.ReadToEnd()
        $stderr | Set-Content -LiteralPath (Join-Path $dir 'malformed-config.stderr.txt')
        $results.Add([pscustomobject]@{Variant=$variant;Case='Malformed configuration produces actionable error without unhandled crash';Pass=($exit -eq 4 -and $stderr -notmatch 'Unhandled');Exit=$exit;Error=$stderr.Substring(0,[Math]::Min(500,$stderr.Length))})
    }
}
$report=[pscustomobject]@{Root=$root;NoGameOrRealSteamApi=$true;Results=$results}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 6


if (@($results | Where-Object { -not $_.Pass }).Count -gt 0) { exit 1 }
