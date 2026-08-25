param(
    [string]$GameDir,
    [switch]$AllowExistingFramework
)

$ErrorActionPreference = 'Stop'

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

    throw 'Devil May Cry 5 не найден. Передайте путь параметром -GameDir.'
}

function Get-RelativePath([string]$Base, [string]$Path) {
    $baseFull = [IO.Path]::GetFullPath($Base).TrimEnd('\') + '\'
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($baseFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Путь находится вне каталога игры: $pathFull"
    }
    return $pathFull.Substring($baseFull.Length)
}

function Invalidate-PakEntries([string]$PakPath, [object[]]$Targets) {
    if (-not (Test-Path -LiteralPath $PakPath -PathType Leaf)) {
        throw "Главный PAK не найден: $PakPath"
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
            throw "Неподдерживаемый формат PAK: magic=0x$($magic.ToString('X8')), version=$version"
        }
        if (($flags -band 8) -ne 0) {
            throw 'Зашифрованная таблица PAK не поддерживается безопасным установщиком.'
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
                throw "Ожидалась одна PAK-запись для $($target.Path), найдено: $($matches.Count)."
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

$resolvedGameDir = Find-Dmc5Directory
$gameExe = Join-Path $resolvedGameDir 'DevilMayCry5.exe'
if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) {
    throw "В выбранной папке нет DevilMayCry5.exe: $resolvedGameDir"
}

if (Get-Process -Name 'DevilMayCry5' -ErrorAction SilentlyContinue) {
    throw 'Сначала закройте Devil May Cry 5.'
}

$modDir = Join-Path $resolvedGameDir 'DMC5DualSense'
$manifestPath = Join-Path $modDir 'install-manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    throw 'DMC5 DualSense Layer уже установлен. Для чистого обновления сначала запустите Uninstall.ps1.'
}
if ((Test-Path -LiteralPath $modDir -PathType Container) -and (Get-ChildItem -LiteralPath $modDir -Force)) {
    throw 'Папка DMC5DualSense уже существует, но в ней нет журнала этого установщика. Переименуйте её или укажите другую установку игры.'
}

$dependencies = Join-Path $PSScriptRoot 'Dependencies'
$frameworkZip = Join-Path $dependencies 'REFramework.zip'
$csharpZip = Join-Path $dependencies 'csharp-api.zip'
$uiRoot = Join-Path $PSScriptRoot 'UI'
foreach ($required in @(
    $frameworkZip,
    $csharpZip,
    (Join-Path $PSScriptRoot 'DMC5DualSense.Bridge.exe'),
    (Join-Path $PSScriptRoot 'DMC5DualSense.Launcher.exe'),
    (Join-Path $PSScriptRoot 'DMC5DualSense.cs'),
    (Join-Path $PSScriptRoot 'Test-DualSense.ps1'),
    (Join-Path $PSScriptRoot 'TEST-DualSense.cmd'),
    (Join-Path $PSScriptRoot 'Uninstall.ps1'),
    (Join-Path $PSScriptRoot 'UNINSTALL-DMC5-DualSense.cmd'),
    (Join-Path $PSScriptRoot 'README_RU.md'),
    (Join-Path $PSScriptRoot 'BUILD_INFO.txt'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui0000\tex\ui0010_iam.tex.11.x64'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui4000\gui\ui4002.gui.270020'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui4000\tex\ui4002_00_iam.tex.11'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui8000\gui\ui8013.gui.270020'),
    (Join-Path $uiRoot 'natives\x64\ui\gui\ui8000\tex\ui8013_iam.tex.11')
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "В пакете отсутствует обязательный файл: $required"
    }
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('DMC5DualSense-' + [Guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $modDir 'backup'
$records = [Collections.Generic.List[object]]::new()
$createdDirectories = [Collections.Generic.List[string]]::new()
$pakInvalidations = [Collections.Generic.List[object]]::new()
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'DMC5DualSense'
$runValueExisted = $false
$runValueBefore = $null
$autostartInstalled = $false

try {
    $runValueBefore = [string](Get-ItemPropertyValue -LiteralPath $runKey -Name $runValueName -ErrorAction Stop)
    $runValueExisted = $true
} catch { }

try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $frameworkExtract = Join-Path $temporary 'framework'
    $csharpExtract = Join-Path $temporary 'csharp'
    Expand-Archive -LiteralPath $frameworkZip -DestinationPath $frameworkExtract
    Expand-Archive -LiteralPath $csharpZip -DestinationPath $csharpExtract

    $incomingDinput = Get-ChildItem -LiteralPath $frameworkExtract -Filter 'dinput8.dll' -Recurse | Select-Object -First 1
    if (-not $incomingDinput) { throw 'В REFramework.zip не найден dinput8.dll.' }

    $targetDinput = Join-Path $resolvedGameDir 'dinput8.dll'
    if ((Test-Path -LiteralPath $targetDinput) -and -not $AllowExistingFramework) {
        $currentHash = (Get-FileHash -LiteralPath $targetDinput -Algorithm SHA256).Hash
        $incomingHash = (Get-FileHash -LiteralPath $incomingDinput.FullName -Algorithm SHA256).Hash
        if ($currentHash -ne $incomingHash) {
            throw 'В игре уже есть другой dinput8.dll. Чтобы разрешить его резервное копирование и замену, запустите Install.ps1 с -AllowExistingFramework.'
        }
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
            InstalledSha256 = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        })
    }

    Install-OneFile $incomingDinput.FullName $targetDinput

    $csharpRoot = Join-Path $csharpExtract 'reframework'
    foreach ($source in Get-ChildItem -LiteralPath $csharpRoot -File -Recurse) {
        $relative = Get-RelativePath $csharpRoot $source.FullName
        Install-OneFile $source.FullName (Join-Path (Join-Path $resolvedGameDir 'reframework') $relative)
    }

    Install-OneFile (Join-Path $PSScriptRoot 'DMC5DualSense.Bridge.exe') (Join-Path $modDir 'DMC5DualSense.Bridge.exe')
    Install-OneFile (Join-Path $PSScriptRoot 'DMC5DualSense.Launcher.exe') (Join-Path $modDir 'DMC5DualSense.Launcher.exe')
    Install-OneFile (Join-Path $PSScriptRoot 'Test-DualSense.ps1') (Join-Path $modDir 'Test-DualSense.ps1')
    Install-OneFile (Join-Path $PSScriptRoot 'TEST-DualSense.cmd') (Join-Path $modDir 'TEST-DualSense.cmd')
    Install-OneFile (Join-Path $PSScriptRoot 'Uninstall.ps1') (Join-Path $modDir 'Uninstall.ps1')
    Install-OneFile (Join-Path $PSScriptRoot 'UNINSTALL-DMC5-DualSense.cmd') (Join-Path $modDir 'UNINSTALL-DMC5-DualSense.cmd')
    Install-OneFile (Join-Path $PSScriptRoot 'README_RU.md') (Join-Path $modDir 'README_RU.md')
    Install-OneFile (Join-Path $PSScriptRoot 'BUILD_INFO.txt') (Join-Path $modDir 'BUILD_INFO.txt')

    $configTarget = Join-Path $modDir 'config.json'
    if (-not (Test-Path -LiteralPath $configTarget -PathType Leaf)) {
        Install-OneFile (Join-Path $PSScriptRoot 'config.json') $configTarget
    }

    Install-OneFile (Join-Path $PSScriptRoot 'DMC5DualSense.cs') (Join-Path $resolvedGameDir 'reframework\plugins\source\DMC5DualSense.cs')

    foreach ($source in Get-ChildItem -LiteralPath $uiRoot -File -Recurse) {
        $relative = Get-RelativePath $uiRoot $source.FullName
        Install-OneFile $source.FullName (Join-Path $resolvedGameDir $relative)
    }

    # RE Engine prefers a matching entry in re_chunk_000.pak over a loose file.
    # The older prompt mod had invalidated only its three TEX files, so the two
    # new GUI layouts must be invalidated as well. Only their 8-byte hash pairs
    # in the TOC are changed; payload data remains untouched and is restored by
    # the uninstaller from the exact values stored in the manifest.
    $pakPath = Join-Path $resolvedGameDir 're_chunk_000.pak'
    $pakTargets = @(
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

    $launcherTarget = Join-Path $modDir 'DMC5DualSense.Launcher.exe'
    $autostartCommand = '"' + $launcherTarget + '" --background'
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -LiteralPath $runKey -Name $runValueName -PropertyType String -Value $autostartCommand -Force | Out-Null
    $autostartInstalled = $true

    $manifest = [pscustomobject]@{
        Version = '1.2.0-resident-bridge'
        InstalledUtc = [DateTime]::UtcNow.ToString('O')
        GameDirectory = $resolvedGameDir
        Files = $records
        CreatedDirectories = $createdDirectories
        PakInvalidations = $pakInvalidations
        Autostart = [pscustomobject]@{
            RegistryPath = $runKey
            ValueName = $runValueName
            InstalledValue = $autostartCommand
            PreviousValueExisted = $runValueExisted
            PreviousValue = $runValueBefore
        }
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    Start-Process -FilePath $launcherTarget -ArgumentList '--background' -WindowStyle Hidden

    Write-Host ''
    Write-Host 'DMC5 DualSense Layer установлен.' -ForegroundColor Green
    Write-Host "Игра: $resolvedGameDir"
    Write-Host 'Фоновый bridge зарегистрирован в автозапуске и захватывает DualSense до Steam Input.'
    Write-Host 'Подключите DualSense по USB и запускайте игру обычной кнопкой «Играть» в Steam.'
    Write-Host 'Один раз укажите в Steam -> Свойства -> Параметры запуска:'
    Write-Host ('"' + (Join-Path $modDir 'DMC5DualSense.Launcher.exe') + '" %command%') -ForegroundColor Cyan
}
catch {
    if ($autostartInstalled) {
        if ($runValueExisted) {
            New-ItemProperty -LiteralPath $runKey -Name $runValueName -PropertyType String -Value $runValueBefore -Force | Out-Null
        } else {
            Remove-ItemProperty -LiteralPath $runKey -Name $runValueName -ErrorAction SilentlyContinue
        }
    }
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
    throw
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
