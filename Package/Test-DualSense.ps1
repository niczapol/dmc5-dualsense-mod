param([switch]$Quick)
$ErrorActionPreference='Stop'
$bridge=Join-Path $PSScriptRoot 'DMC5DualSense.Bridge.exe'
if(-not(Test-Path -LiteralPath $bridge)){throw 'Bridge executable is missing. Reinstall the complete package.'}
$argument=if($Quick){'--probe'}else{'--self-test-all'}
$process=Start-Process -FilePath $bridge -ArgumentList $argument -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -Wait -PassThru
switch($process.ExitCode){
    0 {Write-Host 'Test commands completed. Physical feedback must still be confirmed by you.'}
    2 {Write-Host 'Controller/audio not ready. Connect one DualSense by USB; keep Steam Input enabled. Check bridge.log for details.'; exit 2}
    4 {Write-Host 'Invalid config.json. Check bridge.log; feedback was not started.'; exit 4}
    5 {Write-Host 'Test NOT run: the Bridge is already running. Close DMC5 before this standalone test.'; exit 5}
    default {Write-Host "Test failed (exit $($process.ExitCode)). See bridge.log."; exit $process.ExitCode}
}
