param([string]$GameDir)

$ErrorActionPreference = 'Stop'

if (-not $GameDir) {
    $steamRoots = [Collections.Generic.List[string]]::new()
    foreach ($registryPath in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
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
        if (Test-Path -LiteralPath $vdf -PathType Leaf) {
            foreach ($line in Get-Content -LiteralPath $vdf) {
                if ($line -match '^\s*"path"\s+"(.+)"') {
                    $libraryRoots.Add(($Matches[1] -replace '\\\\', '\'))
                }
            }
        }
    }

    foreach ($root in $libraryRoots | Select-Object -Unique) {
        $candidate = Join-Path $root 'steamapps\common\Devil May Cry 5'
        if (Test-Path -LiteralPath (Join-Path $candidate 'DMC5DualSense\install-manifest.json') -PathType Leaf) {
            $GameDir = $candidate
            break
        }
    }
}

if (-not $GameDir) { throw 'The installed mod was not found. Pass the game directory with -GameDir.' }
$resolvedGameDir = [IO.Path]::GetFullPath($GameDir)
$modDir = Join-Path $resolvedGameDir 'DMC5DualSense'
$manifestPath = Join-Path $modDir 'install-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The installation manifest was not found: $manifestPath"
}

if (Get-Process -Name 'DevilMayCry5' -ErrorAction SilentlyContinue) {
    throw 'Close Devil May Cry 5 before uninstalling the mod.'
}

Get-Process -Name 'DMC5DualSense.Bridge' -ErrorAction SilentlyContinue | Stop-Process -Force
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ($manifest.PSObject.Properties['Autostart'] -and $manifest.Autostart) {
    $autostart = $manifest.Autostart
    $currentValue = $null
    try {
        $currentValue = [string](Get-ItemPropertyValue -LiteralPath $autostart.RegistryPath -Name $autostart.ValueName -ErrorAction Stop)
    } catch { }

    if ($currentValue -eq [string]$autostart.InstalledValue) {
        if ($autostart.PreviousValueExisted) {
            New-ItemProperty -LiteralPath $autostart.RegistryPath -Name $autostart.ValueName -PropertyType String -Value ([string]$autostart.PreviousValue) -Force | Out-Null
        } else {
            Remove-ItemProperty -LiteralPath $autostart.RegistryPath -Name $autostart.ValueName -ErrorAction SilentlyContinue
        }
    } elseif ($null -ne $currentValue) {
        Write-Warning 'The startup entry was changed after installation and was left unchanged.'
    }
}

foreach ($record in @($manifest.PakInvalidations)) {
    $pakPath = Join-Path $resolvedGameDir $record.PakRelativePath
    if (-not (Test-Path -LiteralPath $pakPath -PathType Leaf)) {
        Write-Warning "The PAK required for rollback was not found: $pakPath"
        continue
    }

    $stream = [IO.File]::Open(
        $pakPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        $magic = $reader.ReadUInt32()
        $version = $reader.ReadUInt16()
        $flags = $reader.ReadUInt16()
        $entryCount = $reader.ReadUInt32()
        [void]$reader.ReadUInt32()
        $expectedOffset = 16L + 48L * [int64]$record.EntryIndex
        if ($magic -ne 0x414B504B -or $version -ne 4 -or ($flags -band 8) -ne 0 -or
            [int64]$record.EntryOffset -ne $expectedOffset -or
            [int64]$record.EntryIndex -ge [int64]$entryCount) {
            throw "The PAK structure changed; safe rollback stopped for $($record.ResourcePath)."
        }

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
        elseif ($currentLower -ne [uint32]$record.OriginalHashLower -or
                $currentUpper -ne [uint32]$record.OriginalHashUpper) {
            Write-Warning "The PAK entry was changed by another program and was left unchanged: $($record.ResourcePath)"
        }
    }
    finally {
        $writer.Dispose()
        $reader.Dispose()
        $stream.Dispose()
    }
}

$manifestFiles = @($manifest.Files)
$retainedBackup = $false
for ($recordIndex = $manifestFiles.Count - 1; $recordIndex -ge 0; $recordIndex--) {
    $record = $manifestFiles[$recordIndex]
    $target = Join-Path $resolvedGameDir $record.RelativePath
    if ($record.Existed -and $record.BackupRelativePath) {
        $backup = Join-Path $modDir $record.BackupRelativePath
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            if (Test-Path -LiteralPath $target -PathType Leaf) {
                $currentHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
                if ($currentHash -ne [string]$record.InstalledSha256) {
                    Write-Warning "A pre-existing file was changed after installation and was left unchanged: $target"
                    $retainedBackup = $true
                    continue
                }
            }
            New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item -LiteralPath $backup -Destination $target -Force
        }
    } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
        $currentHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($currentHash -eq $record.InstalledSha256) {
            Remove-Item -LiteralPath $target -Force
        } else {
            Write-Warning "A file was changed after installation and was left in place: $target"
        }
    }
}

foreach ($relative in @($manifest.CreatedDirectories) | Sort-Object Length -Descending) {
    $directory = Join-Path $resolvedGameDir $relative
    if ((Test-Path -LiteralPath $directory -PathType Container) -and -not (Get-ChildItem -LiteralPath $directory -Force)) {
        Remove-Item -LiteralPath $directory -Force
    }
}

$ownedBackup = Join-Path $modDir 'backup'
if (-not $retainedBackup -and (Test-Path -LiteralPath $ownedBackup -PathType Container)) {
    Remove-Item -LiteralPath $ownedBackup -Recurse -Force
}
Remove-Item -LiteralPath $manifestPath -Force

if ($retainedBackup) {
    Write-Warning "At least one pre-existing file changed after installation. Its original backup was retained in $ownedBackup. Move that backup somewhere safe before installing another DMC5DualSense version."
}

if ((Test-Path -LiteralPath $modDir -PathType Container) -and -not (Get-ChildItem -LiteralPath $modDir -Force)) {
    Remove-Item -LiteralPath $modDir -Force
    Write-Host 'DMC5 DualSense Layer was removed and the previous file state was restored.' -ForegroundColor Green
} else {
    Write-Host 'The mod files were removed and the previous file state was restored.' -ForegroundColor Green
    Write-Warning "Changed settings or logs not owned by the installer remain in $modDir."
}
