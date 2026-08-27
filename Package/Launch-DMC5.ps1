$ErrorActionPreference = 'Stop'

$modDir = [IO.Path]::GetFullPath($PSScriptRoot)
$gameDir = Split-Path $modDir
$gameExe = Join-Path $gameDir 'DevilMayCry5.exe'

if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) {
    throw "DevilMayCry5.exe не найден рядом с папкой мода: $gameDir"
}
if (Get-Process -Name 'DevilMayCry5' -ErrorAction SilentlyContinue) {
    Write-Host 'Devil May Cry 5 уже запущена.'
    exit 0
}

# REFramework loads the in-game plugin, which starts the hidden session Bridge
# and ties it to DMC5. This helper never creates a resident task.
Start-Process 'steam://rungameid/601150'
