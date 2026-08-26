[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageZip,
    [switch]$KeepWorkingDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Hash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Read-PakPair([string]$Path, [int]$Index) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 16L + 48L * $Index
        [pscustomobject]@{ Lower = $reader.ReadUInt32(); Upper = $reader.ReadUInt32() }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function New-MockPak([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint32]0x414B504B)
        $writer.Write([uint16]4)
        $writer.Write([uint16]0)
        $writer.Write([uint32]3)
        $writer.Write([uint32]0)
        foreach ($pair in @(
            @([uint32]3412546084, [uint32]3766842389),
            @([uint32]1604417987, [uint32]163320100),
            @([uint32]1327091247, [uint32]808910760)
        )) {
            $writer.Write($pair[0])
            $writer.Write($pair[1])
            $writer.Write([byte[]]::new(40))
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$zipPath = (Resolve-Path -LiteralPath $PackageZip).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$workRoot = Join-Path $tempRoot ('DMC5DualSense-Smoke-' + [Guid]::NewGuid().ToString('N'))
$workRoot = [IO.Path]::GetFullPath($workRoot)
if (-not $workRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create a smoke-test directory outside the system temp root: $workRoot"
}

try {
    $extractRoot = Join-Path $workRoot 'package'
    $gameRoot = Join-Path $workRoot 'SteamLibrary\steamapps\common\Devil May Cry 5'
    New-Item -ItemType Directory -Path $extractRoot, $gameRoot | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot

    $installers = @(Get-ChildItem -LiteralPath $extractRoot -Filter 'Install.ps1' -File -Recurse)
    if ($installers.Count -ne 1) {
        throw "Expected one Install.ps1 in the release ZIP, found $($installers.Count)."
    }

    New-Item -ItemType File -Path (Join-Path $gameRoot 'DevilMayCry5.exe') | Out-Null
    $pakPath = Join-Path $gameRoot 're_chunk_000.pak'
    New-MockPak $pakPath

    & $installers[0].FullName -GameDir $gameRoot -AllowExistingFramework -NoClipboard

    $manifestPath = Join-Path $gameRoot 'DMC5DualSense\install-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The installer did not create install-manifest.json.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in @($manifest.Files)) {
        $installed = Join-Path $gameRoot ([string]$entry.RelativePath)
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) {
            throw "Installed manifest file is missing: $($entry.RelativePath)"
        }
        if ((Get-Hash $installed) -ne ([string]$entry.InstalledSha256).ToUpperInvariant()) {
            throw "Installed manifest hash mismatch: $($entry.RelativePath)"
        }
    }

    $firstInvalidated = Read-PakPair $pakPath 0
    $secondInvalidated = Read-PakPair $pakPath 1
    $thirdInvalidated = Read-PakPair $pakPath 2
    if ($firstInvalidated.Lower -ne 0 -or $firstInvalidated.Upper -ne 0 -or
        $secondInvalidated.Lower -ne 0 -or $secondInvalidated.Upper -ne 0 -or
        $thirdInvalidated.Lower -ne 0 -or $thirdInvalidated.Upper -ne 0) {
        throw 'The installer did not invalidate all three expected GUI PAK entries.'
    }

    $installedUninstaller = Join-Path $gameRoot 'DMC5DualSense\Uninstall.ps1'
    & $installedUninstaller -GameDir $gameRoot

    $firstRestored = Read-PakPair $pakPath 0
    $secondRestored = Read-PakPair $pakPath 1
    $thirdRestored = Read-PakPair $pakPath 2
    if ($firstRestored.Lower -ne 3412546084 -or $firstRestored.Upper -ne 3766842389 -or
        $secondRestored.Lower -ne 1604417987 -or $secondRestored.Upper -ne 163320100 -or
        $thirdRestored.Lower -ne 1327091247 -or $thirdRestored.Upper -ne 808910760) {
        throw 'The uninstaller did not restore all three original GUI PAK entries.'
    }
    if (Test-Path -LiteralPath $manifestPath) {
        throw 'install-manifest.json remained after uninstall.'
    }

    [pscustomobject]@{
        Package = $zipPath
        PackageSha256 = Get-Hash $zipPath
        InstalledFilesVerified = @($manifest.Files).Count
        PakEntriesRestored = 3
        GameLaunched = $false
        Result = 'PASS'
    } | ConvertTo-Json
}
finally {
    if ($KeepWorkingDirectory) {
        Write-Host "Smoke-test directory retained: $workRoot"
    }
    elseif (Test-Path -LiteralPath $workRoot -PathType Container) {
        $resolvedWorkRoot = [IO.Path]::GetFullPath($workRoot)
        if (-not $resolvedWorkRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a smoke-test directory outside the system temp root: $resolvedWorkRoot"
        }
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}
