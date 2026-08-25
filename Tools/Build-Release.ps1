[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$HapticsDirectory,
    [string]$UiDirectory,
    [string]$DependencyCache,
    [switch]$FrameworkDependent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n"), $utf8NoBom)
}

function Get-ExactHash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-ExactFile([string]$Path, [long]$Size, [string]$Sha256, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing $Label input: $Path"
    }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $Size) {
        throw "$Label size mismatch for $Path. Expected $Size, got $($item.Length)."
    }
    $actual = Get-ExactHash $Path
    if ($actual -ne $Sha256.ToUpperInvariant()) {
        throw "$Label SHA-256 mismatch for $Path. Expected $Sha256, got $actual."
    }
}

function Copy-ExactFile([string]$Source, [string]$Destination) {
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
        throw "Path is outside the release staging directory: $pathFull"
    }
    return $pathFull.Substring($baseFull.Length).Replace('\', '/')
}

function New-DeterministicZip([string]$SourceDirectory, [string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $stream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
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

$versionFile = Join-Path $repoRoot 'version.json'
if (-not $Version) {
    $Version = [string]((Get-Content -LiteralPath $versionFile -Raw | ConvertFrom-Json).Version)
}
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    throw "Invalid release version: $Version"
}

if (-not $HapticsDirectory) {
    $HapticsDirectory = Join-Path $repoRoot 'Bridge\Assets\Haptics'
}
if (-not $UiDirectory) {
    $UiDirectory = Join-Path $repoRoot 'Package\UI'
}
if (-not $DependencyCache) {
    $DependencyCache = Join-Path $repoRoot '.tools\release-cache'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ("artifacts\v" + $Version)
}

$hapticsRoot = [IO.Path]::GetFullPath($HapticsDirectory)
$uiRoot = [IO.Path]::GetFullPath($UiDirectory)
$dependencyRoot = [IO.Path]::GetFullPath($DependencyCache)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)

if (Test-Path -LiteralPath $outputRoot) {
    throw "Output directory already exists. Choose a new -OutputDirectory: $outputRoot"
}
New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
New-Item -ItemType Directory -Path $outputRoot | Out-Null

$assetManifestPath = Join-Path $repoRoot 'release-assets.json'
$assetManifest = Get-Content -LiteralPath $assetManifestPath -Raw | ConvertFrom-Json
if ([int]$assetManifest.Schema -ne 1) { throw 'Unsupported release-assets.json schema.' }

foreach ($asset in $assetManifest.Haptics) {
    Assert-ExactFile (Join-Path $hapticsRoot $asset.Name) ([long]$asset.Size) ([string]$asset.Sha256) 'haptics'
}
foreach ($asset in $assetManifest.Ui) {
    $relative = ([string]$asset.Path).Replace('/', [IO.Path]::DirectorySeparatorChar)
    Assert-ExactFile (Join-Path $uiRoot $relative) ([long]$asset.Size) ([string]$asset.Sha256) 'UI'
}

$dependencyFiles = [ordered]@{}
foreach ($dependency in $assetManifest.Dependencies) {
    $target = Join-Path $dependencyRoot ([string]$dependency.Name)
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        Write-Host "Downloading pinned dependency $($dependency.Name)..."
        Invoke-WebRequest -Uri ([string]$dependency.Url) -OutFile $target
    }
    Assert-ExactFile $target ([long]$dependency.Size) ([string]$dependency.Sha256) 'dependency'
    $dependencyFiles[[string]$dependency.Name] = $target
}

$vigemPath = [string]$dependencyFiles['ViGEmBus_1.22.0_x64_x86_arm64.exe']
$signature = Get-AuthenticodeSignature -LiteralPath $vigemPath
if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $signature.SignerCertificate.Subject -notlike 'CN=Nefarius Software Solutions*') {
    throw "ViGEmBus Authenticode validation failed: $($signature.Status), $($signature.SignerCertificate.Subject)"
}

$localDotnet = Join-Path $repoRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$dotnetVersion = (& $dotnet --version).Trim()
if ($dotnetVersion -notmatch '^10\.') {
    throw ".NET SDK 10.x is required, found $dotnetVersion."
}

Write-Host 'Running deterministic logic tests...'
& $dotnet run --project (Join-Path $repoRoot 'Tests\DMC5DualSense.LogicTests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Logic tests failed with code $LASTEXITCODE." }
& $dotnet build (Join-Path $repoRoot 'Tools\UiAssetBuilder\DMC5DualSense.UiAssetBuilder.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "UI tool build failed with code $LASTEXITCODE." }

$buildRoot = Join-Path $outputRoot 'build'
$bridgePublish = Join-Path $buildRoot 'bridge'
$launcherPublish = Join-Path $buildRoot 'launcher'
New-Item -ItemType Directory -Path $bridgePublish, $launcherPublish | Out-Null
$selfContained = -not $FrameworkDependent

Write-Host "Publishing win-x64 runtime (self-contained=$selfContained)..."
& $dotnet publish (Join-Path $repoRoot 'Bridge\DMC5DualSense.Bridge.csproj') `
    -c Release -r win-x64 --self-contained ($selfContained.ToString().ToLowerInvariant()) `
    -p:PublishSingleFile=true -p:DebugType=embedded "-p:HapticsRoot=$hapticsRoot" `
    -o $bridgePublish
if ($LASTEXITCODE -ne 0) { throw "Bridge publish failed with code $LASTEXITCODE." }

& $dotnet publish (Join-Path $repoRoot 'Launcher\DMC5DualSense.Launcher.csproj') `
    -c Release -r win-x64 --self-contained ($selfContained.ToString().ToLowerInvariant()) `
    -p:PublishSingleFile=true -p:DebugType=embedded -o $launcherPublish
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed with code $LASTEXITCODE." }

$stageRoot = Join-Path $outputRoot ("DMC5DualSense-v" + $Version)
New-Item -ItemType Directory -Path $stageRoot | Out-Null

$trackedPackageFiles = & git -C $repoRoot ls-files -- 'Package'
if ($LASTEXITCODE -ne 0 -or -not $trackedPackageFiles) {
    throw 'Unable to enumerate tracked Package files with git.'
}
foreach ($relativePath in $trackedPackageFiles) {
    if ($relativePath -eq 'Package/BUILD_INFO.txt') { continue }
    $source = Join-Path $repoRoot $relativePath
    $destinationRelative = $relativePath.Substring('Package/'.Length).Replace('/', '\')
    Copy-ExactFile $source (Join-Path $stageRoot $destinationRelative)
}

Copy-ExactFile (Join-Path $bridgePublish 'DMC5DualSense.Bridge.exe') `
    (Join-Path $stageRoot 'DMC5DualSense.Bridge.exe')
Copy-ExactFile (Join-Path $launcherPublish 'DMC5DualSense.Launcher.exe') `
    (Join-Path $stageRoot 'DMC5DualSense.Launcher.exe')
Copy-ExactFile (Join-Path $repoRoot 'Plugin\DMC5DualSense.cs') `
    (Join-Path $stageRoot 'DMC5DualSense.cs')

foreach ($asset in $assetManifest.Ui) {
    $relative = ([string]$asset.Path).Replace('/', [IO.Path]::DirectorySeparatorChar)
    Copy-ExactFile (Join-Path $uiRoot $relative) (Join-Path (Join-Path $stageRoot 'UI') $relative)
}
foreach ($dependency in $assetManifest.Dependencies) {
    $name = [string]$dependency.Name
    Copy-ExactFile ([string]$dependencyFiles[$name]) (Join-Path (Join-Path $stageRoot 'Dependencies') $name)
}

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$sourceDate = (& git -C $repoRoot show -s --format=%cI HEAD).Trim()
$assetManifestHash = Get-ExactHash $assetManifestPath
$buildInfo = @(
    "DMC5 DualSense Layer $Version",
    "Source commit: $sourceCommit",
    "Source date: $sourceDate",
    "Target: Devil May Cry 5 Steam / Windows x64",
    "Runtime: .NET $dotnetVersion, self-contained=$selfContained",
    "Controller: Sony DualSense USB VID_054C/PID_0CE6",
    "Input: direct HID -> ViGEm Xbox 360",
    "Asset manifest SHA256: $assetManifestHash",
    '',
    'This package passed deterministic logic tests and exact input hash validation.'
) -join "`n"
Write-Utf8NoBom (Join-Path $stageRoot 'BUILD_INFO.txt') ($buildInfo + "`n")

$payloadFiles = Get-ChildItem -LiteralPath $stageRoot -File -Recurse |
    Sort-Object { Get-RelativeForwardPath $stageRoot $_.FullName } |
    ForEach-Object {
        [ordered]@{
            Path = Get-RelativeForwardPath $stageRoot $_.FullName
            Size = $_.Length
            Sha256 = Get-ExactHash $_.FullName
        }
    }

$releaseManifest = [ordered]@{
    Schema = 1
    Version = $Version
    SourceCommit = $sourceCommit
    SourceDate = $sourceDate
    RuntimeIdentifier = 'win-x64'
    SelfContained = $selfContained
    DotnetSdk = $dotnetVersion
    AssetManifestSha256 = $assetManifestHash
    Files = @($payloadFiles)
}
$releaseJson = $releaseManifest | ConvertTo-Json -Depth 6
Write-Utf8NoBom (Join-Path $stageRoot 'release-manifest.json') ($releaseJson + "`n")

$zipName = "DMC5DualSense-v$Version-win-x64.zip"
$zipPath = Join-Path $outputRoot $zipName
New-DeterministicZip $stageRoot $zipPath
$zipHash = Get-ExactHash $zipPath
$checksumPath = Join-Path $outputRoot 'CHECKSUMS.txt'
Write-Utf8NoBom $checksumPath ("$zipHash  $zipName`n")

$result = [ordered]@{
    Version = $Version
    SourceCommit = $sourceCommit
    SelfContained = $selfContained
    Zip = $zipPath
    ZipSize = (Get-Item -LiteralPath $zipPath).Length
    ZipSha256 = $zipHash
    Checksums = $checksumPath
    Stage = $stageRoot
}
$result | ConvertTo-Json
