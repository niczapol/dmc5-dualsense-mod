[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Compiler,
    [string]$WorkingRoot = [IO.Path]::GetTempPath()
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root = Join-Path ([IO.Path]::GetFullPath($WorkingRoot)) ('DMC5DS-SteamMock-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $root 'fixture'
New-Item -ItemType Directory -Path (Join-Path $fixture 'DMC5DualSense') -Force | Out-Null
& $Compiler -std=c++20 -O2 -static -shared (Join-Path $PSScriptRoot 'mock_steam.cpp') -o (Join-Path $fixture 'steam_api64.dll')
if ($LASTEXITCODE) { throw 'Mock compilation failed' }
& $Compiler -std=c++20 -O2 -static -municode -I (Join-Path $repo 'Native\Bridge') (Join-Path $PSScriptRoot 'test_platform.cpp') (Join-Path $repo 'Native\Bridge\platform.cpp') (Join-Path $repo 'Native\Bridge\core.cpp') -lxinput1_4 -o (Join-Path $root 'test_platform.exe')
if ($LASTEXITCODE) { throw 'Test compilation failed' }
& (Join-Path $root 'test_platform.exe') $fixture | Tee-Object -FilePath (Join-Path $root 'results.txt')
$testExit = $LASTEXITCODE
Write-Host "Audit-only Steam mock retained at $root. NEVER install it into a game."
exit $testExit
