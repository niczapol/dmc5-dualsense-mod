$ErrorActionPreference = 'Stop'

$modDir = [IO.Path]::GetFullPath($PSScriptRoot)
$gameDir = Split-Path $modDir
$gameExe = Join-Path $gameDir 'DevilMayCry5.exe'
$bridgeExe = Join-Path $modDir 'DMC5DualSense.Bridge.exe'
$bridgeLog = Join-Path $modDir 'bridge.log'
$configPath = Join-Path $modDir 'config.json'

if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) {
    throw "DevilMayCry5.exe не найден рядом с папкой мода: $gameDir"
}
if (-not (Test-Path -LiteralPath $bridgeExe -PathType Leaf)) {
    throw "Мост DualSense не найден: $bridgeExe"
}

if (Get-Process -Name 'DevilMayCry5' -ErrorAction SilentlyContinue) {
    Write-Host 'Devil May Cry 5 уже запущена.'
    exit 0
}

# A bridge started from inside DMC5 is too late on systems where Steam Input
# takes the physical HID handle. Always start a fresh bridge and wait until it
# has both the writable HID interface and the DualSense audio endpoint.
$existingBridge = Get-Process -Name 'DMC5DualSense.Bridge' -ErrorAction SilentlyContinue
if ($existingBridge) {
    $existingBridge | Stop-Process -Force
    $existingBridge | Wait-Process -Timeout 3 -ErrorAction SilentlyContinue
}

$initialLogLines = if (Test-Path -LiteralPath $bridgeLog -PathType Leaf) {
    @(Get-Content -LiteralPath $bridgeLog).Count
} else { 0 }

$advancedHapticsRequired = $true
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    try {
        $advancedHapticsRequired = [bool]((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).EnableAdvancedHaptics)
    } catch { }
}

$bridgeProcess = Start-Process -FilePath $bridgeExe -WorkingDirectory $modDir -WindowStyle Hidden -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(8)
$hidReady = $false
$audioReady = -not $advancedHapticsRequired
$diagnostic = 'мост не успел сообщить состояние контроллера'

while ([DateTime]::UtcNow -lt $deadline -and -not ($hidReady -and $audioReady)) {
    Start-Sleep -Milliseconds 200
    if ($bridgeProcess.HasExited) {
        throw "Мост DualSense завершился до запуска игры (код $($bridgeProcess.ExitCode))."
    }
    if (-not (Test-Path -LiteralPath $bridgeLog -PathType Leaf)) { continue }

    $newLines = @(Get-Content -LiteralPath $bridgeLog | Select-Object -Skip $initialLogLines)
    foreach ($line in $newLines) {
        if ($line -match 'DualSense (connected|reconnected):') { $hidReady = $true }
        if ($line -match 'DualSense unavailable:|DualSense disconnected:') { $diagnostic = $line }
        if ($line -match 'Advanced haptics audio:') { $audioReady = $true }
        if ($line -match 'Advanced haptics unavailable:') { $diagnostic = $line }
    }
}

if (-not $hidReady) {
    Stop-Process -Id $bridgeProcess.Id -Force -ErrorAction SilentlyContinue
    throw "DualSense не захвачен до запуска DMC5. Переподключите USB-кабель и закройте PlayStation Accessories/DS4Windows/DualSenseX. Последнее состояние: $diagnostic"
}
if (-not $audioReady) {
    Stop-Process -Id $bridgeProcess.Id -Force -ErrorAction SilentlyContinue
    throw "Не найден аудиовыход DualSense для HD-вибрации. Последнее состояние: $diagnostic"
}

Write-Host 'DualSense захвачен: курки, свет и HD-вибрация готовы.' -ForegroundColor Green
Start-Process 'steam://rungameid/601150'
