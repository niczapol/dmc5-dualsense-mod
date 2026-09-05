[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageZip,
    [string]$WorkingRoot = [IO.Path]::GetTempPath()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Get-Process -Name 'DMC5DualSense.Bridge','DevilMayCry5' -ErrorAction SilentlyContinue) {
    throw 'Close game/runtime before isolated installer audit'
}
$zip = (Resolve-Path -LiteralPath $PackageZip).Path
$Variant = if ($zip -match 'Native') {'native'} else {'managed'}
$work = Join-Path ([IO.Path]::GetFullPath($WorkingRoot)) ('DMC5DS-audit-' + $Variant + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'package')
$pkg = (Get-ChildItem -LiteralPath (Join-Path $work 'package') -Filter Install.ps1 -Recurse).DirectoryName
$results = [Collections.Generic.List[object]]::new()
function New-Game([string]$Name) {
    $game = Join-Path $work $Name
    New-Item -ItemType Directory -Path $game | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $game 'DevilMayCry5.exe'), [byte[]]::new(0))
    $writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $game 're_chunk_000.pak')))
    try {
        $writer.Write([uint32]0x414B504B); $writer.Write([uint16]4); $writer.Write([uint16]0)
        $writer.Write([uint32]3); $writer.Write([uint32]0)
        foreach ($pair in @(@([uint32]3412546084,[uint32]3766842389),@([uint32]1604417987,[uint32]163320100),@([uint32]1327091247,[uint32]808910760))) {
            $writer.Write($pair[0]); $writer.Write($pair[1]); $writer.Write([byte[]]::new(40))
        }
    } finally { $writer.Dispose() }
    return $game
}
function Install-Package([string]$Game, [string]$Package = $pkg) {
    & (Join-Path $Package 'Install.ps1') -GameDir $Game -NonInteractive *>> (Join-Path $work 'installer.log')
}
function Record([string]$Case, [bool]$Pass, [object]$Detail) {
    $results.Add([pscustomobject]@{Case=$Case; Pass=$Pass; Detail=$Detail})
}
function Has-Bridge([string]$Game) { Test-Path -LiteralPath (Join-Path $Game 'DMC5DualSense\DMC5DualSense.Bridge.exe') }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

# Upgrade with a required executable absent (e.g. a quarantined/extraction-missed file).
$game = New-Game 'incomplete-upgrade'
Install-Package $game
$missing = Join-Path $pkg 'DMC5DualSense.Bridge.exe'
Move-Item -LiteralPath $missing -Destination ($missing + '.held')
$errorText = ''
try { Install-Package $game } catch { $errorText = $_.Exception.Message }
finally { Move-Item -LiteralPath ($missing + '.held') -Destination $missing }
Record 'Incomplete update keeps previous working installation' (Has-Bridge $game) $errorText

# Release manifest should catch byte corruption before installing the payload.
$game = New-Game 'corrupted-package'
$oldBytes = [IO.File]::ReadAllBytes($missing)
try {
    [IO.File]::WriteAllBytes($missing, [Text.Encoding]::ASCII.GetBytes('AUDIT: not an executable'))
    $errorText = ''
    try { Install-Package $game } catch { $errorText = $_.Exception.Message }
    Record 'Corrupted payload is rejected before installation' (-not (Has-Bridge $game)) $errorText
} finally { [IO.File]::WriteAllBytes($missing, $oldBytes) }

# Late failure must be reversible and retryable without manual directory removal.
$game = New-Game 'pak-conflict'
$pak = Join-Path $game 're_chunk_000.pak'
$oldPak = [IO.File]::ReadAllBytes($pak)
$badPak = [byte[]]$oldPak.Clone()
[Array]::Clear($badPak, 16, 8)
[IO.File]::WriteAllBytes($pak, $badPak)
$errorText = ''
try { Install-Package $game } catch { $errorText = $_.Exception.Message }
Record 'Conflicting PAK rejected and installed executable rolled back' ((-not (Has-Bridge $game)) -and $errorText.Length -gt 0) $errorText
[IO.File]::WriteAllBytes($pak, $oldPak)
$errorText = ''
try { Install-Package $game } catch { $errorText = $_.Exception.Message }
Record 'Retry succeeds after transient PAK failure is removed' (Has-Bridge $game) $errorText

# Same-version reinstall retains edited config; Steam verification repair is reversible.
$game = New-Game 'settings-upgrade'
$pak = Join-Path $game 're_chunk_000.pak'
$originalPak = [IO.File]::ReadAllBytes($pak)
Install-Package $game
$config = Join-Path $game 'DMC5DualSense\config.json'
$json = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
$json.TriggerStrength = 0.37
$json.EnableLightbar = $false
$json | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding UTF8
$before = Hash $config
Install-Package $game
Record 'Edited configuration survives reinstall byte-for-byte' ((Hash $config) -eq $before) $before
[IO.File]::WriteAllBytes($pak, $originalPak)
Install-Package $game
& (Join-Path $game 'DMC5DualSense\Uninstall.ps1') -GameDir $game *>> (Join-Path $work 'installer.log')
Record 'Repair after Steam PAK verification can be uninstalled' ((Hash $pak) -eq ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($originalPak)))) 'Original mock PAK hash restored'

# The distributed entrypoint uses Windows PowerShell 5.1, not development pwsh.
# A handled failure AFTER uninstall must restore the previous mod and PAK state.
$game = New-Game 'late-update-rejection'
$loader = Join-Path $game 'dinput8.dll'
[IO.File]::WriteAllText($loader, 'MZ original unknown loader for rollback fixture')
& (Join-Path $pkg 'Install.ps1') -GameDir $game -NonInteractive -ReplaceExistingFramework *>> (Join-Path $work 'installer.log')
$beforeFiles = @{}
foreach ($relative in @('dinput8.dll','re_chunk_000.pak','DMC5DualSense\DMC5DualSense.Bridge.exe','DMC5DualSense\install-manifest.json')) {
    $beforeFiles[$relative] = Hash (Join-Path $game $relative)
}
$errorText = ''
try { Install-Package $game } catch { $errorText = $_.Exception.Message }
$restored = $errorText.Length -gt 0
foreach ($relative in $beforeFiles.Keys) {
    $path = Join-Path $game $relative
    $restored = $restored -and (Test-Path -LiteralPath $path) -and (Hash $path) -eq $beforeFiles[$relative]
}
Record 'Late update rejection restores previous executable, loader, manifest and PAK' $restored $errorText

# The distributed entrypoint uses Windows PowerShell 5.1, not development pwsh.
$game = New-Game ('Steam Library [audit] ' + [char]0x6E38 + [char]0x620F)
$parseScript = Join-Path $pkg 'Install.ps1'
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $parseScript -GameDir $game -NonInteractive *>> (Join-Path $work 'winps51.log')
Record 'Windows PowerShell 5.1 install in spaces/brackets/Unicode game path' ($LASTEXITCODE -eq 0 -and (Has-Bridge $game)) $LASTEXITCODE

$report = [pscustomobject]@{Variant=$Variant; Root=$work; Results=$results}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $work 'results.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 8


# Findings intentionally make the audit red until their corresponding fixes exist.
if (@($results | Where-Object { -not $_.Pass }).Count -gt 0) { exit 1 }
