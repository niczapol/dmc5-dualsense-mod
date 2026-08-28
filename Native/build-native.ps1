[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$ToolsRoot,
    [string]$HapticsDirectory,
    [switch]$SkipRuntimeAssets
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ToolsRoot) { $ToolsRoot = Join-Path $repoRoot '.tools\native' }
$resolvedToolsRoot = [IO.Path]::GetFullPath($ToolsRoot)
$toolchainRoot = Join-Path $resolvedToolsRoot 'llvm-mingw'
$compiler = Join-Path $toolchainRoot 'bin\clang++.exe'
$strip = Join-Path $toolchainRoot 'bin\llvm-strip.exe'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "Pinned LLVM-MinGW toolchain is missing. Run Native\Prepare-Dependencies.ps1 -ToolsRoot '$resolvedToolsRoot'."
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot 'bin'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$common = @(
    '-std=c++20',
    '-DUNICODE',
    '-D_UNICODE',
    '-DWIN32_LEAN_AND_MEAN',
    '-D_WIN32_WINNT=0x0A00',
    '-ffunction-sections',
    '-fdata-sections',
    '-fno-exceptions',
    '-fno-rtti',
    '-static',
    '-static-libgcc',
    '-static-libstdc++',
    '-municode',
    '-mwindows',
    '-Wl,--gc-sections',
    '-Wl,--no-insert-timestamp'
)

if ($Configuration -eq 'Release') {
    $common += @('-O2', '-DNDEBUG')
} else {
    $common += @('-O0', '-g')
}

$launcherOutput = Join-Path $outputRoot 'DMC5DualSense.Launcher.Native.exe'
& $compiler @common `
    (Join-Path $PSScriptRoot 'Launcher\launcher.cpp') `
    '-o' $launcherOutput `
    '-lws2_32' '-lshell32'
if ($LASTEXITCODE -ne 0) { throw "Native launcher build failed with code $LASTEXITCODE." }

if ($Configuration -eq 'Release') {
    & $strip '--strip-all' $launcherOutput
    if ($LASTEXITCODE -ne 0) { throw "Native launcher strip failed with code $LASTEXITCODE." }
}

$bridgeCommon = @($common | Where-Object { $_ -notin @('-fno-exceptions', '-fno-rtti') })
$bridgeOutput = Join-Path $outputRoot 'DMC5DualSense.Bridge.Native.exe'
& $compiler @bridgeCommon `
    '-include' 'thread' `
    ('-I' + (Join-Path $PSScriptRoot 'third_party')) `
    ('-I' + (Join-Path $PSScriptRoot 'Bridge')) `
    (Join-Path $PSScriptRoot 'Bridge\core.cpp') `
    (Join-Path $PSScriptRoot 'Bridge\platform.cpp') `
    (Join-Path $PSScriptRoot 'Bridge\haptics.cpp') `
    (Join-Path $PSScriptRoot 'Bridge\bridge.cpp') `
    '-o' $bridgeOutput `
    '-lxinput1_4' '-lws2_32' '-lshell32' '-lole32' '-luuid'
if ($LASTEXITCODE -ne 0) { throw "Native bridge build failed with code $LASTEXITCODE." }

if ($Configuration -eq 'Release') {
    & $strip '--strip-all' $bridgeOutput
    if ($LASTEXITCODE -ne 0) { throw "Native bridge strip failed with code $LASTEXITCODE." }
}

if (-not $SkipRuntimeAssets) {
    if (-not $HapticsDirectory) {
        $HapticsDirectory = Join-Path $repoRoot 'Bridge\Assets\Haptics'
    }
    $resolvedHaptics = [IO.Path]::GetFullPath($HapticsDirectory)
    $hapticFiles = @(Get-ChildItem -LiteralPath $resolvedHaptics -Filter '*.wav' -File)
    if ($hapticFiles.Count -ne 12) {
        throw "Expected 12 pinned haptic WAV files in $resolvedHaptics; found $($hapticFiles.Count)."
    }
    $hapticsOutput = Join-Path $outputRoot 'Haptics'
    New-Item -ItemType Directory -Path $hapticsOutput -Force | Out-Null
    Copy-Item -LiteralPath $hapticFiles.FullName -Destination $hapticsOutput -Force
}

$reframeworkRoot = Join-Path $resolvedToolsRoot 'vendor\REFramework'
$pluginOutput = Join-Path $outputRoot 'DMC5DualSense.dll'
& $compiler `
    '-std=c++20' '-DUNICODE' '-D_UNICODE' '-DWIN32_LEAN_AND_MEAN' `
    '-D_WIN32_WINNT=0x0A00' '-O2' '-DNDEBUG' '-static-libgcc' '-static-libstdc++' `
    '-shared' '-ffunction-sections' '-fdata-sections' '-Wl,--gc-sections' `
    '-Wl,--no-insert-timestamp' `
    ('-I' + (Join-Path $reframeworkRoot 'include')) `
    (Join-Path $PSScriptRoot 'Plugin\plugin.cpp') `
    '-o' $pluginOutput '-lws2_32' '-lshell32'
if ($LASTEXITCODE -ne 0) { throw "Native REFramework plugin build failed with code $LASTEXITCODE." }

if ($Configuration -eq 'Release') {
    & $strip '--strip-all' $pluginOutput
    if ($LASTEXITCODE -ne 0) { throw "Native plugin strip failed with code $LASTEXITCODE." }
}

$pluginVersionTestOutput = Join-Path $outputRoot 'DMC5DualSense.PluginVersionTests.exe'
& $compiler `
    '-std=c++20' '-DUNICODE' '-D_UNICODE' '-DWIN32_LEAN_AND_MEAN' `
    '-D_WIN32_WINNT=0x0A00' '-O2' '-DNDEBUG' '-static' `
    '-static-libgcc' '-static-libstdc++' '-municode' '-Wl,--no-insert-timestamp' `
    (Join-Path $PSScriptRoot 'Tests\plugin_version_test.cpp') `
    '-o' $pluginVersionTestOutput
if ($LASTEXITCODE -ne 0) { throw "Native plugin version test build failed with code $LASTEXITCODE." }
& $pluginVersionTestOutput $pluginOutput
if ($LASTEXITCODE -ne 0) { throw "Native plugin version test failed with code $LASTEXITCODE." }

$testOutput = Join-Path $outputRoot 'DMC5DualSense.NativeTests.exe'
$testArguments = @(
    '-std=c++20', '-DUNICODE', '-D_UNICODE', '-DWIN32_LEAN_AND_MEAN',
    '-D_WIN32_WINNT=0x0A00', '-O2', '-DNDEBUG', '-static',
    '-static-libgcc', '-static-libstdc++', '-Wl,--no-insert-timestamp',
    (Join-Path $PSScriptRoot 'Bridge\core.cpp'),
    (Join-Path $PSScriptRoot 'Tests\tests.cpp'),
    '-o', $testOutput
)
& $compiler @testArguments
if ($LASTEXITCODE -ne 0) { throw "Native logic test build failed with code $LASTEXITCODE." }
& $testOutput
if ($LASTEXITCODE -ne 0) { throw "Native logic tests failed with code $LASTEXITCODE." }

foreach ($component in @(
    @{ Name = 'Launcher'; Path = $launcherOutput },
    @{ Name = 'Bridge'; Path = $bridgeOutput },
    @{ Name = 'Plugin'; Path = $pluginOutput }
)) {
    $item = Get-Item -LiteralPath $component.Path
    [pscustomobject]@{
        Component = $component.Name
        Path = $item.FullName
        Bytes = $item.Length
        Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}
