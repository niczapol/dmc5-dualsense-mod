param([switch]$Quick)

$ErrorActionPreference = 'Stop'
$bridge = Join-Path $PSScriptRoot 'DMC5DualSense.Bridge.exe'
if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) { throw "Не найден $bridge" }
$testArgument = if ($Quick) { '--probe' } else { '--self-test-all' }
$testProcess = Start-Process -FilePath $bridge -ArgumentList @($testArgument) `
    -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -Wait -PassThru
if ($testProcess.ExitCode -ne 0) {
    throw "Самотест завершился с кодом $($testProcess.ExitCode)"
}
