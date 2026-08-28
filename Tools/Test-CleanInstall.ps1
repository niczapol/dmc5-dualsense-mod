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

    $frameworkZips = @(Get-ChildItem -LiteralPath $extractRoot -Filter 'REFramework.zip' -File -Recurse)
    if ($frameworkZips.Count -ne 1) {
        throw "Expected one REFramework.zip in the release ZIP, found $($frameworkZips.Count)."
    }
    $frameworkRoot = Join-Path $workRoot 'framework'
    Expand-Archive -LiteralPath $frameworkZips[0].FullName -DestinationPath $frameworkRoot
    $bundledDinputs = @(Get-ChildItem -LiteralPath $frameworkRoot -Filter 'dinput8.dll' -File -Recurse)
    if ($bundledDinputs.Count -ne 1) {
        throw "Expected one bundled dinput8.dll, found $($bundledDinputs.Count)."
    }
    $bundledDinput = $bundledDinputs[0].FullName
    $bundledDinputHash = Get-Hash $bundledDinput

    New-Item -ItemType File -Path (Join-Path $gameRoot 'DevilMayCry5.exe') | Out-Null
    $pakPath = Join-Path $gameRoot 're_chunk_000.pak'
    New-MockPak $pakPath

    & $installers[0].FullName -GameDir $gameRoot

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

    $targetDinput = Join-Path $gameRoot 'dinput8.dll'
    if (Test-Path -LiteralPath $targetDinput) {
        throw 'Clean uninstall left the bundled dinput8.dll behind.'
    }

    # A byte-distinct REFramework build must be kept in place. Mutating the
    # final byte gives this installer-only fixture a different hash while
    # retaining a valid x64 PE header and the embedded REFramework identity.
    Copy-Item -LiteralPath $bundledDinput -Destination $targetDinput
    $fixtureStream = [IO.File]::Open($targetDinput, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
    try {
        $fixtureStream.Position = $fixtureStream.Length - 1
        $lastByte = $fixtureStream.ReadByte()
        $fixtureStream.Position = $fixtureStream.Length - 1
        $fixtureStream.WriteByte([byte](($lastByte + 1) -band 0xFF))
    }
    finally {
        $fixtureStream.Dispose()
    }
    $existingFrameworkHash = Get-Hash $targetDinput
    if ($existingFrameworkHash -eq $bundledDinputHash) {
        throw 'The alternate REFramework fixture did not get a distinct hash.'
    }

    & $installers[0].FullName -GameDir $gameRoot
    $preserveManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$preserveManifest.Framework.Action -ne 'PreserveExisting') {
        throw "Expected PreserveExisting, got $($preserveManifest.Framework.Action)."
    }
    if ((Get-Hash $targetDinput) -ne $existingFrameworkHash) {
        throw 'The installer changed an existing REFramework dinput8.dll.'
    }
    & (Join-Path $gameRoot 'DMC5DualSense\Uninstall.ps1') -GameDir $gameRoot
    if ((Get-Hash $targetDinput) -ne $existingFrameworkHash) {
        throw 'The uninstaller changed a preserved REFramework dinput8.dll.'
    }

    # An unknown proxy must not be overwritten implicitly.
    [IO.File]::WriteAllBytes($targetDinput, [Text.Encoding]::ASCII.GetBytes('MZ unknown dinput8 proxy fixture'))
    $unknownHash = Get-Hash $targetDinput
    $unknownRejected = $false
    try {
        & $installers[0].FullName -GameDir $gameRoot -NonInteractive
    }
    catch {
        $unknownRejected = $_.Exception.Message -like '*could not be identified as REFramework*'
    }
    if (-not $unknownRejected) {
        throw 'The non-interactive installer did not safely reject an unknown dinput8.dll.'
    }
    if ((Get-Hash $targetDinput) -ne $unknownHash -or (Test-Path -LiteralPath $manifestPath)) {
        throw 'The rejected installation changed the unknown dinput8.dll or created a manifest.'
    }

    # Declining the normal prompt must cancel cleanly and leave the unknown DLL
    # untouched.
    $global:DMC5DualSensePromptCount = 0
    function Read-Host {
        param([string]$Prompt)
        $global:DMC5DualSensePromptCount++
        return 'N'
    }
    try {
        & $installers[0].FullName -GameDir $gameRoot
    }
    finally {
        Remove-Item Function:\Read-Host -ErrorAction SilentlyContinue
    }
    if ($global:DMC5DualSensePromptCount -ne 1) {
        throw "Expected one cancellation prompt, got $global:DMC5DualSensePromptCount."
    }
    Remove-Variable DMC5DualSensePromptCount -Scope Global -ErrorAction SilentlyContinue
    if ((Get-Hash $targetDinput) -ne $unknownHash -or (Test-Path -LiteralPath $manifestPath)) {
        throw 'Declining replacement changed the unknown dinput8.dll or created a manifest.'
    }

    # The normal interactive prompt must make replacement reversible without
    # requiring the user to discover a PowerShell command-line switch.
    $global:DMC5DualSensePromptCount = 0
    function Read-Host {
        param([string]$Prompt)
        $global:DMC5DualSensePromptCount++
        return 'Y'
    }
    try {
        & $installers[0].FullName -GameDir $gameRoot
    }
    finally {
        Remove-Item Function:\Read-Host -ErrorAction SilentlyContinue
    }
    if ($global:DMC5DualSensePromptCount -ne 1) {
        throw "Expected one replacement prompt, got $global:DMC5DualSensePromptCount."
    }
    Remove-Variable DMC5DualSensePromptCount -Scope Global -ErrorAction SilentlyContinue
    $replaceManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$replaceManifest.Framework.Action -ne 'ReplaceExisting') {
        throw "Expected ReplaceExisting, got $($replaceManifest.Framework.Action)."
    }
    if ((Get-Hash $targetDinput) -ne $bundledDinputHash) {
        throw 'Interactive framework replacement did not install the bundled dinput8.dll.'
    }
    & (Join-Path $gameRoot 'DMC5DualSense\Uninstall.ps1') -GameDir $gameRoot
    if ((Get-Hash $targetDinput) -ne $unknownHash) {
        throw 'Uninstall did not restore the exact explicitly replaced dinput8.dll.'
    }

    # If another tool changes a replaced loader later, uninstall must preserve
    # the newer file and retain our exact backup instead of overwriting either.
    [IO.File]::WriteAllBytes($targetDinput, [Text.Encoding]::ASCII.GetBytes('MZ original proxy before explicit replacement'))
    $originalBeforeReplacementHash = Get-Hash $targetDinput
    & $installers[0].FullName -GameDir $gameRoot -ReplaceExistingFramework
    [IO.File]::WriteAllBytes($targetDinput, [Text.Encoding]::ASCII.GetBytes('MZ proxy updated by another tool after installation'))
    $changedAfterInstallHash = Get-Hash $targetDinput
    & (Join-Path $gameRoot 'DMC5DualSense\Uninstall.ps1') -GameDir $gameRoot
    if ((Get-Hash $targetDinput) -ne $changedAfterInstallHash) {
        throw 'Uninstall overwrote a dinput8.dll changed by another tool.'
    }
    $retainedBackup = Join-Path $gameRoot 'DMC5DualSense\backup\dinput8.dll'
    if (-not (Test-Path -LiteralPath $retainedBackup -PathType Leaf) -or
        (Get-Hash $retainedBackup) -ne $originalBeforeReplacementHash) {
        throw 'Uninstall did not retain the exact original backup after a third-party change.'
    }

    [pscustomobject]@{
        Package = $zipPath
        PackageSha256 = Get-Hash $zipPath
        InstalledFilesVerified = @($manifest.Files).Count
        PakEntriesRestored = 3
        FrameworkScenarios = @(
            'CleanInstall',
            'PreserveExisting',
            'RejectUnknownNonInteractive',
            'DeclinePromptWithoutChanges',
            'PromptReplaceAndRestore',
            'ChangedAfterInstallPreserved'
        )
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
