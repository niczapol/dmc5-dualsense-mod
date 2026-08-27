[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$BuildDirectory,
    [string]$ToolsRoot,
    [string]$UiDirectory,
    [string]$HapticsDirectory,
    [string]$DependencyCache
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$nativeRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $nativeRoot '..'))
if (-not $Version) {
    $Version = [string]((Get-Content -LiteralPath (Join-Path $repoRoot 'version.json') -Raw |
        ConvertFrom-Json).Version)
}
if (-not $ToolsRoot) { $ToolsRoot = Join-Path $repoRoot '.tools\native' }
$resolvedToolsRoot = [IO.Path]::GetFullPath($ToolsRoot)
if (-not $UiDirectory) { $UiDirectory = Join-Path $repoRoot 'Package\UI' }
$uiSourceRoot = [IO.Path]::GetFullPath($UiDirectory)
if (-not $HapticsDirectory) {
    $HapticsDirectory = Join-Path $repoRoot 'Bridge\Assets\Haptics'
}
$hapticSourceRoot = [IO.Path]::GetFullPath($HapticsDirectory)
if (-not $DependencyCache) { $DependencyCache = Join-Path $repoRoot '.tools\release-cache' }
$cache = [IO.Path]::GetFullPath($DependencyCache)
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Write-Utf8NoBom([string]$Path, [string]$Value) {
    [IO.File]::WriteAllText($Path, ($Value -replace "`r`n", "`n"), $utf8NoBom)
}

function Get-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Copy-File([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Missing package input: $Source"
    }
    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Get-RelativeForwardPath([string]$Base, [string]$Path) {
    $baseFull = [IO.Path]::GetFullPath($Base).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($baseFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside staging directory: $pathFull"
    }
    return $pathFull.Substring($baseFull.Length).Replace('\', '/')
}

function New-DeterministicZip([string]$SourceDirectory, [string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new(
        $stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $files = Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse |
            Sort-Object { Get-RelativeForwardPath $SourceDirectory $_.FullName }
        foreach ($file in $files) {
            $relative = Get-RelativeForwardPath $SourceDirectory $file.FullName
            $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try { $input.CopyTo($output) }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    throw "Invalid package version: $Version"
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $nativeRoot ("artifacts\" + $Version)
}
if (-not $BuildDirectory) {
    $BuildDirectory = Join-Path $nativeRoot ("package-build\" + $Version)
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
if (Test-Path -LiteralPath $outputRoot) {
    throw "Output already exists; select a fresh directory: $outputRoot"
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

& (Join-Path $nativeRoot 'build-native.ps1') -Configuration Release `
    -OutputDirectory $buildRoot -ToolsRoot $resolvedToolsRoot `
    -HapticsDirectory $hapticSourceRoot | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Native build failed with code $LASTEXITCODE" }

$assets = Get-Content -LiteralPath (Join-Path $repoRoot 'release-assets.json') -Raw |
    ConvertFrom-Json
foreach ($asset in $assets.Haptics) {
    $path = Join-Path $hapticSourceRoot ([string]$asset.Name)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -ne [long]$asset.Size -or
        (Get-Hash $path) -ne ([string]$asset.Sha256).ToUpperInvariant()) {
        throw "Pinned haptic validation failed: $path"
    }
}
foreach ($asset in $assets.Ui) {
    $path = Join-Path $uiSourceRoot (([string]$asset.Path).Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -ne [long]$asset.Size -or
        (Get-Hash $path) -ne ([string]$asset.Sha256).ToUpperInvariant()) {
        throw "Pinned UI validation failed: $path"
    }
}
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$dependencyByName = @{}
foreach ($dependency in $assets.Dependencies) {
    if ([string]$dependency.Name -eq 'csharp-api.zip') { continue }
    $path = Join-Path $cache ([string]$dependency.Name)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Invoke-WebRequest -Uri ([string]$dependency.Url) -OutFile $path
    }
    if ((Get-Item -LiteralPath $path).Length -ne [long]$dependency.Size -or
        (Get-Hash $path) -ne ([string]$dependency.Sha256).ToUpperInvariant()) {
        throw "Pinned dependency validation failed: $path"
    }
    $dependencyByName[[string]$dependency.Name] = $path
}

$stageRoot = Join-Path $outputRoot ("DMC5DualSense-Native-" + $Version)
New-Item -ItemType Directory -Path $stageRoot | Out-Null

foreach ($name in @(
    'Install.ps1', 'Uninstall.ps1', 'INSTALL-DMC5-DualSense.cmd',
    'UNINSTALL-DMC5-DualSense.cmd', 'Test-DualSense.ps1', 'TEST-DualSense.cmd',
    'README_RU.md', 'README_EN.md'
)) {
    Copy-File (Join-Path (Join-Path $nativeRoot 'Package') $name) (Join-Path $stageRoot $name)
}
Copy-File (Join-Path $nativeRoot 'Package\config.json') (Join-Path $stageRoot 'config.json')
foreach ($name in @('NOTICE.txt', 'LICENSE.txt')) {
    Copy-File (Join-Path (Join-Path $repoRoot 'Package') $name) (Join-Path $stageRoot $name)
}

Copy-File (Join-Path $buildRoot 'DMC5DualSense.Bridge.Native.exe') `
    (Join-Path $stageRoot 'DMC5DualSense.Bridge.exe')
Copy-File (Join-Path $buildRoot 'DMC5DualSense.Launcher.Native.exe') `
    (Join-Path $stageRoot 'DMC5DualSense.Launcher.exe')
Copy-File (Join-Path $buildRoot 'DMC5DualSense.dll') `
    (Join-Path $stageRoot 'DMC5DualSense.dll')

foreach ($file in Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Haptics') -Filter '*.wav' -File) {
    Copy-File $file.FullName (Join-Path (Join-Path $stageRoot 'Haptics') $file.Name)
}
$uiRoot = $uiSourceRoot
foreach ($file in Get-ChildItem -LiteralPath $uiRoot -File -Recurse) {
    $relative = Get-RelativeForwardPath $uiRoot $file.FullName
    Copy-File $file.FullName (Join-Path (Join-Path $stageRoot 'UI') $relative)
}
Copy-File ([string]$dependencyByName['REFramework.zip']) `
    (Join-Path $stageRoot 'Dependencies\REFramework.zip')
Copy-File (Join-Path $repoRoot 'Package\Licenses\REFramework-LICENSE.txt') `
    (Join-Path $stageRoot 'Licenses\REFramework-LICENSE.txt')
Copy-File (Join-Path $nativeRoot 'third_party\nlohmann\LICENSE.MIT') `
    (Join-Path $stageRoot 'Licenses\nlohmann-json-LICENSE.txt')

$toolchainVersion = (& (Join-Path $resolvedToolsRoot 'llvm-mingw\bin\clang++.exe') --version |
    Select-Object -First 1).Trim()
$buildInfo = @(
    "DMC5 DualSense Layer Native C++ $Version",
    'Target: Devil May Cry 5 Steam / Windows x64',
    'Runtime: native Win32 C++; no .NET runtime',
    "Compiler: $toolchainVersion",
    'REFramework nightly: 01397 / 684ca77369ec1050e844e8651a9b1d5b7c5aa370',
    'Gameplay input and touchpad owner: Steam Input',
    'Controller output: SteamInput006 adaptive triggers, LED and rumble',
    'Advanced haptics: bundled samples through four-channel Windows WASAPI',
    'External driver/runtime dependencies: none',
    'Build mode: deterministic PE timestamps and statically linked C++ runtimes'
) -join "`n"
Write-Utf8NoBom (Join-Path $stageRoot 'BUILD_INFO.txt') ($buildInfo + "`n")

$files = Get-ChildItem -LiteralPath $stageRoot -File -Recurse |
    Sort-Object { Get-RelativeForwardPath $stageRoot $_.FullName } |
    ForEach-Object {
        [ordered]@{
            Path = Get-RelativeForwardPath $stageRoot $_.FullName
            Size = $_.Length
            Sha256 = Get-Hash $_.FullName
        }
    }
$manifest = [ordered]@{
    Schema = 1
    Version = $Version
    Runtime = 'native-cpp-win-x64'
    SelfContained = $true
    DotnetRequired = $false
    Files = @($files)
}
Write-Utf8NoBom (Join-Path $stageRoot 'release-manifest.json') `
    (($manifest | ConvertTo-Json -Depth 6) + "`n")

$zipName = "DMC5DualSense-Native-$Version-win-x64.zip"
$zipPath = Join-Path $outputRoot $zipName
New-DeterministicZip $stageRoot $zipPath
$zipHash = Get-Hash $zipPath
Write-Utf8NoBom (Join-Path $outputRoot 'CHECKSUMS.txt') "$zipHash  $zipName`n"

[pscustomobject]@{
    Version = $Version
    Stage = $stageRoot
    Zip = $zipPath
    ZipBytes = (Get-Item -LiteralPath $zipPath).Length
    ZipSha256 = $zipHash
    PayloadBytes = (Get-ChildItem -LiteralPath $stageRoot -File -Recurse |
        Measure-Object Length -Sum).Sum
}
