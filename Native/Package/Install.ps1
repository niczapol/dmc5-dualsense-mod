param(
    [string]$GameDir,
    [switch]$ReplaceExistingFramework,
    [switch]$AllowExistingFramework,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
$packageVersion = '1.7.1-native'
$releaseManifestSource = Join-Path $PSScriptRoot 'release-manifest.json'
if (Test-Path -LiteralPath $releaseManifestSource -PathType Leaf) {
    try {
        $releaseMetadata = Get-Content -LiteralPath $releaseManifestSource -Raw | ConvertFrom-Json
        if ($releaseMetadata.Version) { $packageVersion = [string]$releaseMetadata.Version }
    } catch {
        throw "release-manifest.json is invalid: $($_.Exception.Message)"
    }
}

function Find-Dmc5Directory {
    if ($GameDir) {
        return [IO.Path]::GetFullPath($GameDir)
    }

    $steamRoots = [Collections.Generic.List[string]]::new()
    $registryPaths = @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'
    )

    foreach ($registryPath in $registryPaths) {
        try {
            $item = Get-ItemProperty -LiteralPath $registryPath
            if ($item.SteamPath) { $steamRoots.Add([string]$item.SteamPath) }
            if ($item.InstallPath) { $steamRoots.Add([string]$item.InstallPath) }
        } catch { }
    }

    $steamRoots.Add('C:\Program Files (x86)\Steam')
    $libraryRoots = [Collections.Generic.List[string]]::new()

    foreach ($steamRoot in $steamRoots | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $steamRoot -PathType Container)) { continue }
        $libraryRoots.Add($steamRoot)
        $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf -PathType Leaf)) { continue }

        foreach ($line in Get-Content -LiteralPath $vdf) {
            if ($line -match '^\s*"path"\s+"(.+)"') {
                $libraryRoots.Add(($Matches[1] -replace '\\\\', '\'))
            }
        }
    }

    foreach ($root in $libraryRoots | Select-Object -Unique) {
        $candidate = Join-Path $root 'steamapps\common\Devil May Cry 5'
        if (Test-Path -LiteralPath (Join-Path $candidate 'DevilMayCry5.exe') -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    throw 'Devil May Cry 5 was not found. Pass its directory with -GameDir.'
}

function Get-RelativePath([string]$Base, [string]$Path) {
    $baseFull = [IO.Path]::GetFullPath($Base).TrimEnd('\') + '\'
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($baseFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the game directory: $pathFull"
    }
    return $pathFull.Substring($baseFull.Length)
}

function Invalidate-PakEntries([string]$PakPath, [object[]]$Targets) {
    if (-not (Test-Path -LiteralPath $PakPath -PathType Leaf)) {
        throw "The main PAK was not found: $PakPath"
    }

    $stream = [IO.File]::Open(
        $PakPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
    $records = [Collections.Generic.List[object]]::new()
    $written = [Collections.Generic.List[object]]::new()

    try {
        $magic = $reader.ReadUInt32()
        $version = $reader.ReadUInt16()
        $flags = $reader.ReadUInt16()
        $entryCount = $reader.ReadUInt32()
        [void]$reader.ReadUInt32()

        if ($magic -ne 0x414B504B -or $version -ne 4) {
            throw "Unsupported PAK format: magic=0x$($magic.ToString('X8')), version=$version"
        }
        if (($flags -band 8) -ne 0) {
            throw 'The safe installer does not support an encrypted PAK table.'
        }

        foreach ($target in $Targets) {
            $matches = [Collections.Generic.List[object]]::new()
            for ($index = 0; $index -lt $entryCount; $index++) {
                $entryOffset = 16L + 48L * $index
                $stream.Position = $entryOffset
                $lower = $reader.ReadUInt32()
                $upper = $reader.ReadUInt32()
                if ($lower -eq [uint32]$target.Lower -and $upper -eq [uint32]$target.Upper) {
                    $matches.Add([pscustomobject]@{
                        PakRelativePath = Get-RelativePath $resolvedGameDir $PakPath
                        ResourcePath = [string]$target.Path
                        EntryIndex = $index
                        EntryOffset = $entryOffset
                        OriginalHashLower = [uint32]$lower
                        OriginalHashUpper = [uint32]$upper
                    })
                }
            }

            if ($matches.Count -ne 1) {
                throw "Expected one PAK entry for $($target.Path), found $($matches.Count)."
            }
            $records.Add($matches[0])
        }

        try {
            foreach ($record in $records) {
                $stream.Position = [int64]$record.EntryOffset
                $writer.Write([uint32]0)
                $writer.Write([uint32]0)
                $written.Add($record)
            }
            $writer.Flush()
            $stream.Flush($true)
        }
        catch {
            foreach ($record in $written) {
                $stream.Position = [int64]$record.EntryOffset
                $writer.Write([uint32]$record.OriginalHashLower)
                $writer.Write([uint32]$record.OriginalHashUpper)
            }
            $writer.Flush()
            $stream.Flush($true)
            throw
        }

        return @($records)
    }
    finally {
        $writer.Dispose()
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Restore-PakInvalidations([string]$BaseDirectory, [object[]]$Invalidations) {
    foreach ($record in @($Invalidations)) {
        $pakPath = Join-Path $BaseDirectory $record.PakRelativePath
        if (-not (Test-Path -LiteralPath $pakPath -PathType Leaf)) { continue }

        $stream = [IO.File]::Open(
            $pakPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read)
        $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
        $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
        try {
            $stream.Position = [int64]$record.EntryOffset
            $currentLower = $reader.ReadUInt32()
            $currentUpper = $reader.ReadUInt32()
            if ($currentLower -eq 0 -and $currentUpper -eq 0) {
                $stream.Position = [int64]$record.EntryOffset
                $writer.Write([uint32]$record.OriginalHashLower)
                $writer.Write([uint32]$record.OriginalHashUpper)
                $writer.Flush()
                $stream.Flush($true)
            }
        }
        finally {
            $writer.Dispose()
            $reader.Dispose()
            $stream.Dispose()
        }
    }
}

function Test-IsREFramework([string]$Path) {
    try {
        $item = Get-Item -LiteralPath $Path -ErrorAction Stop
        if ($item.Length -lt 64 -or $item.Length -gt 128MB) { return $false }
        $stream = [IO.File]::OpenRead($item.FullName)
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) { return $false }
            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) { return $false }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
                return $false
            }
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
        $binaryText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($item.FullName))
        return $binaryText.IndexOf('REFramework', [StringComparison]::OrdinalIgnoreCase) -ge 0
    }
    catch {
        return $false
    }
}

function Confirm-UnknownFrameworkReplacement([string]$Path) {
    if ($NonInteractive) {
        throw 'An existing dinput8.dll was found, but it could not be identified as REFramework. Interactive confirmation is disabled, so the installer stopped without changing it.'
    }

    Write-Warning "An unknown dinput8.dll is already present: $Path"
    Write-Host 'It may belong to another mod loader. DMC5DualSense can keep an exact backup, replace it with the bundled official REFramework loader, and restore the backup when this mod is uninstalled.' -ForegroundColor Yellow
    $answer = Read-Host 'Replace it safely and keep a backup? [Y/N]'
    return $answer -match '^(?i:y|yes)$'
}

$resolvedGameDir = Find-Dmc5Directory
$gameExe = Join-Path $resolvedGameDir 'DevilMayCry5.exe'
if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) {
    throw "DevilMayCry5.exe was not found in the selected directory: $resolvedGameDir"
}

if (Get-Process -Name 'DevilMayCry5' -ErrorAction SilentlyContinue) {
    throw 'Close Devil May Cry 5 before installing the mod.'
}

if ($AllowExistingFramework) {
    Write-Warning '-AllowExistingFramework is deprecated. It now behaves like -ReplaceExistingFramework.'
    $ReplaceExistingFramework = $true
}

$dependencies = Join-Path $PSScriptRoot 'Dependencies'

$modDir = Join-Path $resolvedGameDir 'DMC5DualSense'
$manifestPath = Join-Path $modDir 'install-manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    $installedUninstaller = Join-Path $modDir 'Uninstall.ps1'
    if (-not (Test-Path -LiteralPath $installedUninstaller -PathType Leaf)) {
        throw 'An existing installation has no Uninstall.ps1. Repair or remove it manually before updating.'
    }

    Write-Host 'A previous version was found. Performing a safe update while preserving settings and logs...' -ForegroundColor Cyan
    & $installedUninstaller -GameDir $resolvedGameDir
    if (Test-Path -LiteralPath $manifestPath) {
        throw 'The previous version was not fully removed; the update has stopped.'
    }
}
if (Test-Path -LiteralPath $modDir -PathType Container) {
    # The uninstaller deliberately retains user configuration and runtime
    # diagnostics when they differ from installed files. Accept only those known
    # files here; anything else may belong to another tool and must not be claimed.
    $retainedNames = @(
        'config.json',
        'bridge.log',
        'bridge.ready.json',
        'launcher.log',
        'plugin.log',
        'calibration.csv',
        'nero-input.csv',
        'motor.csv',
        'player-type-dump.txt'
    )
    $unexpected = @(Get-ChildItem -LiteralPath $modDir -Force | Where-Object {
        $_.PSIsContainer -or $_.Name -notin $retainedNames
    })
    if ($unexpected.Count -gt 0) {
        $unexpectedNames = ($unexpected | ForEach-Object Name) -join ', '
        throw "The DMC5DualSense directory contains unknown files without an installation manifest: $unexpectedNames. Rename the directory or move those files manually."
    }
}

$frameworkZip = Join-Path $dependencies 'REFramework.zip'
$uiRoot = Join-Path $PSScriptRoot 'UI'
$hapticsRoot = Join-Path $PSScriptRoot 'Haptics'
foreach ($required in @(
    $frameworkZip,
    (Join-Path $PSScriptRoot 'DMC5DualSense.Bridge.exe'),
    (Join-Path $PSScriptRoot 'DMC5DualSense.Launcher.exe'),
    (Join-Path $PSScriptRoot 'DMC5DualSense.dll'),
    (Join-Path $PSScriptRoot 'Test-DualSense.ps1'),
    (Join-Path $PSScriptRoot 'TEST-DualSense.cmd'),
    (Join-Path $PSScriptRoot 'Uninstall.ps1'),
    (Join-Path $PSScriptRoot 'UNINSTALL-DMC5-DualSense.cmd'),
    (Join-Path $PSScriptRoot 'README_RU.md'),
    (Join-Path $PSScriptRoot 'README_EN.md'),
    (Join-Path $PSScriptRoot 'NOTICE.txt'),
    (Join-Path $PSScriptRoot 'BUILD_INFO.txt'),
    (Join-Path $hapticsRoot '1040252522.wav'),
    (Join-Path $hapticsRoot '193630586.wav'),
    (Join-Path $hapticsRoot '297926011.wav'),
    (Join-Path $hapticsRoot '310261087.wav'),
    (Join-Path $hapticsRoot '317387691.wav'),
    (Join-Path $hapticsRoot '511441928.wav'),
    (Join-Path $hapticsRoot '564764444.wav'),
    (Join-Path $hapticsRoot '683314104.wav'),
    (Join-Path $hapticsRoot '726668428.wav'),
    (Join-Path $hapticsRoot '748704802.wav'),
    (Join-Path $hapticsRoot '752139616.wav'),
    (Join-Path $hapticsRoot '87828053.wav'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui0000\tex\ui0010_iam.tex.11.x64'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui3100\gui\ui3109.gui.270020'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui4000\gui\ui4002.gui.270020'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui4000\tex\ui4002_00_iam.tex.11'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui8000\gui\ui8013.gui.270020'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui8000\tex\ui8013_iam.tex.11')
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "A required package file is missing: $required"
    }
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('DMC5DualSense-' + [Guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $modDir 'backup'
$records = [Collections.Generic.List[object]]::new()
$removedRecords = [Collections.Generic.List[object]]::new()
$createdDirectories = [Collections.Generic.List[string]]::new()
$pakInvalidations = [Collections.Generic.List[object]]::new()

try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $frameworkExtract = Join-Path $temporary 'framework'
    Expand-Archive -LiteralPath $frameworkZip -DestinationPath $frameworkExtract

    $incomingDinput = Get-ChildItem -LiteralPath $frameworkExtract -Filter 'dinput8.dll' -Recurse | Select-Object -First 1
    if (-not $incomingDinput) { throw 'REFramework.zip does not contain dinput8.dll.' }

    $targetDinput = Join-Path $resolvedGameDir 'dinput8.dll'
    $incomingHash = (Get-FileHash -LiteralPath $incomingDinput.FullName -Algorithm SHA256).Hash
    $existingDinput = Test-Path -LiteralPath $targetDinput -PathType Leaf
    $currentHash = if ($existingDinput) {
        (Get-FileHash -LiteralPath $targetDinput -Algorithm SHA256).Hash
    } else { $null }
    $existingIsREFramework = $existingDinput -and (Test-IsREFramework $targetDinput)
    $frameworkAction = 'InstallBundled'

    if ($existingDinput -and $currentHash -eq $incomingHash) {
        $frameworkAction = 'PreserveMatching'
        Write-Host 'The bundled REFramework is already installed; the existing dinput8.dll will be preserved.' -ForegroundColor Cyan
    }
    elseif ($existingDinput -and $ReplaceExistingFramework) {
        $frameworkAction = 'ReplaceExisting'
        Write-Host 'The existing dinput8.dll will be backed up and replaced as explicitly requested.' -ForegroundColor Yellow
    }
    elseif ($existingIsREFramework) {
        $frameworkAction = 'PreserveExisting'
        Write-Host 'An existing REFramework installation was detected. Its dinput8.dll will be preserved so other REFramework mods remain intact.' -ForegroundColor Cyan
        Write-Host 'The native plugin supports REFramework Plugin API 1.10 or newer. If an older build rejects it, rerun Install.ps1 with -ReplaceExistingFramework; the original DLL will be backed up and restored on uninstall.' -ForegroundColor Yellow
    }
    elseif ($existingDinput) {
        if (-not (Confirm-UnknownFrameworkReplacement $targetDinput)) {
            Write-Host 'Installation cancelled. The existing dinput8.dll was not changed.' -ForegroundColor Yellow
            return
        }
        $frameworkAction = 'ReplaceExisting'
        Write-Host 'The existing dinput8.dll will be backed up and replaced.' -ForegroundColor Yellow
    }

    New-Item -ItemType Directory -Path $modDir -Force | Out-Null
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

    function Install-OneFile([string]$Source, [string]$Destination) {
        $relative = Get-RelativePath $resolvedGameDir $Destination
        $existed = Test-Path -LiteralPath $Destination -PathType Leaf
        $backupRelative = $null

        if ($existed) {
            $backupRelative = Join-Path 'backup' $relative
            $backupPath = Join-Path $modDir $backupRelative
            New-Item -ItemType Directory -Path (Split-Path $backupPath) -Force | Out-Null
            Copy-Item -LiteralPath $Destination -Destination $backupPath -Force
        }

        $destinationDirectory = Split-Path $Destination
        if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            $createdDirectories.Add((Get-RelativePath $resolvedGameDir $destinationDirectory))
        }

        Copy-Item -LiteralPath $Source -Destination $Destination -Force
        $records.Add([pscustomobject]@{
            RelativePath = $relative
            Existed = $existed
            BackupRelativePath = $backupRelative
            OriginalSha256 = if ($existed) { (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash } else { $null }
            InstalledSha256 = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        })
    }

    function Remove-LegacyFile([string]$Target) {
        if (-not (Test-Path -LiteralPath $Target -PathType Leaf)) { return }

        $relative = Get-RelativePath $resolvedGameDir $Target
        $backupRelative = Join-Path 'backup\removed' $relative
        $backupPath = Join-Path $modDir $backupRelative
        New-Item -ItemType Directory -Path (Split-Path $backupPath) -Force | Out-Null
        Copy-Item -LiteralPath $Target -Destination $backupPath -Force
        $removedRecords.Add([pscustomobject]@{
            RelativePath = $relative
            BackupRelativePath = $backupRelative
            OriginalSha256 = (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash
        })
        Remove-Item -LiteralPath $Target -Force
    }

    if ($frameworkAction -in @('InstallBundled', 'ReplaceExisting')) {
        Install-OneFile $incomingDinput.FullName $targetDinput
    }

    # REFramework defaults to an open overlay. MenuOpen is applied only when
    # RememberMenuState is enabled, so setting MenuOpen=false by itself does not
    # suppress the panel. Preserve the user's complete file through the normal
    # installer backup, enable the two loaders required by the texture and GUI
    # overrides, and change only the headless-startup keys.
    $refConfigTarget = Join-Path $resolvedGameDir 're2_fw_config.txt'
    $refConfigSource = Join-Path $temporary 're2_fw_config.txt'
    $refConfigLines = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $refConfigTarget -PathType Leaf) {
        foreach ($line in Get-Content -LiteralPath $refConfigTarget) {
            $refConfigLines.Add([string]$line)
        }
    }
    foreach ($setting in ([ordered]@{
        LooseFileLoader_Enabled = 'true'
        LooseTextureLoader_Enabled = 'true'
        REFrameworkConfig_MenuOpen = 'false'
        REFrameworkConfig_RememberMenuState = 'true'
        ScriptRunner_OpenDebugConsoleAtStartup = 'false'
    }).GetEnumerator()) {
        $replacement = $setting.Key + '=' + $setting.Value
        $replaced = $false
        for ($lineIndex = 0; $lineIndex -lt $refConfigLines.Count; $lineIndex++) {
            if ($refConfigLines[$lineIndex] -match ('^' + [regex]::Escape($setting.Key) + '=')) {
                $refConfigLines[$lineIndex] = $replacement
                $replaced = $true
                break
            }
        }
        if (-not $replaced) { $refConfigLines.Add($replacement) }
    }
    [IO.File]::WriteAllLines($refConfigSource, $refConfigLines, [Text.UTF8Encoding]::new($false))
    Install-OneFile $refConfigSource $refConfigTarget

    Install-OneFile (Join-Path $PSScriptRoot 'DMC5DualSense.Bridge.exe') (Join-Path $modDir 'DMC5DualSense.Bridge.exe')
    Install-OneFile (Join-Path $PSScriptRoot 'DMC5DualSense.Launcher.exe') (Join-Path $modDir 'DMC5DualSense.Launcher.exe')
    # The old managed prototype used this source plugin. Leaving it beside the
    # native plugin can load two implementations of the same telemetry bridge.
    # Back it up and disable it for the lifetime of this installation.
    Remove-LegacyFile (Join-Path $resolvedGameDir 'reframework\plugins\source\DMC5DualSense.cs')
    Install-OneFile (Join-Path $PSScriptRoot 'DMC5DualSense.dll') (Join-Path $resolvedGameDir 'reframework\plugins\DMC5DualSense.dll')
    Install-OneFile (Join-Path $PSScriptRoot 'Test-DualSense.ps1') (Join-Path $modDir 'Test-DualSense.ps1')
    Install-OneFile (Join-Path $PSScriptRoot 'TEST-DualSense.cmd') (Join-Path $modDir 'TEST-DualSense.cmd')
    Install-OneFile (Join-Path $PSScriptRoot 'Uninstall.ps1') (Join-Path $modDir 'Uninstall.ps1')
    Install-OneFile (Join-Path $PSScriptRoot 'UNINSTALL-DMC5-DualSense.cmd') (Join-Path $modDir 'UNINSTALL-DMC5-DualSense.cmd')
    Install-OneFile (Join-Path $PSScriptRoot 'README_RU.md') (Join-Path $modDir 'README_RU.md')
    Install-OneFile (Join-Path $PSScriptRoot 'README_EN.md') (Join-Path $modDir 'README_EN.md')
    Install-OneFile (Join-Path $PSScriptRoot 'NOTICE.txt') (Join-Path $modDir 'NOTICE.txt')
    Install-OneFile (Join-Path $PSScriptRoot 'BUILD_INFO.txt') (Join-Path $modDir 'BUILD_INFO.txt')
    if (Test-Path -LiteralPath $releaseManifestSource -PathType Leaf) {
        Install-OneFile $releaseManifestSource (Join-Path $modDir 'release-manifest.json')
    }

    $configTarget = Join-Path $modDir 'config.json'
    if (-not (Test-Path -LiteralPath $configTarget -PathType Leaf)) {
        Install-OneFile (Join-Path $PSScriptRoot 'config.json') $configTarget
    }

    foreach ($source in Get-ChildItem -LiteralPath $hapticsRoot -Filter '*.wav' -File) {
        Install-OneFile $source.FullName (Join-Path (Join-Path $modDir 'Haptics') $source.Name)
    }

    foreach ($source in Get-ChildItem -LiteralPath $uiRoot -File -Recurse) {
        $relative = Get-RelativePath $uiRoot $source.FullName
        Install-OneFile $source.FullName (Join-Path $resolvedGameDir $relative)
    }

    # RE Engine prefers a matching entry in re_chunk_000.pak over a loose file.
    # The older prompt mod had invalidated only its three TEX files, so the three
    # GUI layouts must be invalidated as well. Only their 8-byte hash pairs
    # in the TOC are changed; payload data remains untouched and is restored by
    # the uninstaller from the exact values stored in the manifest.
    $pakPath = Join-Path $resolvedGameDir 're_chunk_000.pak'
    $pakTargets = @(
        [pscustomobject]@{
            Path = 'natives/x64/ui/gui/ui3100/gui/ui3109.gui.270020'
            Lower = [uint32]3412546084
            Upper = [uint32]3766842389
        },
        [pscustomobject]@{
            Path = 'natives/x64/ui/gui/ui4000/gui/ui4002.gui.270020'
            Lower = [uint32]1604417987
            Upper = [uint32]163320100
        },
        [pscustomobject]@{
            Path = 'natives/x64/ui/gui/ui8000/gui/ui8013.gui.270020'
            Lower = [uint32]1327091247
            Upper = [uint32]808910760
        }
    )
    foreach ($invalidation in @(Invalidate-PakEntries $pakPath $pakTargets)) {
        $pakInvalidations.Add($invalidation)
    }

    $manifest = [pscustomobject]@{
        Version = $packageVersion
        InstalledUtc = [DateTime]::UtcNow.ToString('O')
        GameDirectory = $resolvedGameDir
        Framework = [pscustomobject]@{
            Action = $frameworkAction
            ExistingSha256 = $currentHash
            BundledSha256 = $incomingHash
            ExistingRecognizedAsREFramework = $existingIsREFramework
        }
        Files = $records
        RemovedFiles = $removedRecords
        CreatedDirectories = $createdDirectories
        PakInvalidations = $pakInvalidations
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    Write-Host ''
    Write-Host 'DMC5 DualSense Layer Native C++ was installed successfully.' -ForegroundColor Green
    Write-Host "Game directory: $resolvedGameDir"
    Write-Host 'The Bridge runs only while DMC5 is running and exits with the game.'
    Write-Host 'Steam Input continues to own gameplay input and the touchpad; the Bridge sends DualSense feedback only.'
    Write-Host 'This build does not require .NET, ViGEm, a virtual controller, or a separate driver.'
    Write-Host 'Keep Steam Input enabled for DMC5 or select Use default settings.' -ForegroundColor Yellow
    Write-Host 'Connect the DualSense over USB and start the game normally with Steam Play.'
    Write-Host 'No Steam Launch Options are required for this mod.' -ForegroundColor Green
    Write-Host 'If you upgraded from an old release, remove its DMC5DualSense.Launcher entry from Steam Launch Options.' -ForegroundColor Yellow
}
catch {
    Restore-PakInvalidations $resolvedGameDir @($pakInvalidations)
    for ($recordIndex = $records.Count - 1; $recordIndex -ge 0; $recordIndex--) {
        $record = $records[$recordIndex]
        $target = Join-Path $resolvedGameDir $record.RelativePath
        if ($record.Existed -and $record.BackupRelativePath) {
            $backup = Join-Path $modDir $record.BackupRelativePath
            if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination $target -Force }
        } elseif (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Force
        }
    }
    foreach ($record in @($removedRecords)) {
        $target = Join-Path $resolvedGameDir $record.RelativePath
        $backup = Join-Path $modDir $record.BackupRelativePath
        if (-not (Test-Path -LiteralPath $target) -and
            (Test-Path -LiteralPath $backup -PathType Leaf)) {
            New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item -LiteralPath $backup -Destination $target -Force
        }
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
