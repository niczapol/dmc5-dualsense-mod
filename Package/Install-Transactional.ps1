param([string]$GameDir,[switch]$ReplaceExistingFramework,[switch]$AllowExistingFramework,[switch]$NonInteractive)
$ErrorActionPreference='Stop'
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


function Safe-Child([string]$Root,[string]$Relative) {
    $prefix=[IO.Path]::GetFullPath($Root).TrimEnd('\')+'\'
    $path=[IO.Path]::GetFullPath((Join-Path $prefix $Relative))
    if (-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {throw "Unsafe relative path: $Relative"}
    $cursor=$path
    while ($cursor.Length -gt $prefix.Length) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw "Linked path not supported: $cursor"}
        $cursor=Split-Path -Parent $cursor
    }
    return $path
}
$manifestFile=Join-Path $PSScriptRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {throw 'Missing release-manifest.json. Extract the complete release ZIP again.'}
$release=Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($release.Schema -ne 1 -or -not $release.Files) {throw 'Invalid release manifest.'}
$expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $release.Files) {
    if (-not $expected.Add([string]$file.Path)) {throw 'Duplicate manifest path.'}
    $source=Safe-Child $PSScriptRoot ([string]$file.Path)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -ne [long]$file.Size -or (Get-FileHash -LiteralPath $source).Hash -ne [string]$file.Sha256) {
        throw "Package integrity check failed: $($file.Path). Extract the ZIP again; check antivirus quarantine."
    }
}
if (-not $expected.Contains('Install-Core.ps1') -or -not $expected.Contains('DMC5DualSense.dll')) {throw 'Incomplete installer/plugin payload.'}
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File -Recurse) {
    $relative=$file.FullName.Substring($PSScriptRoot.TrimEnd('\').Length+1).Replace('\','/')
    if ($relative -ne 'release-manifest.json' -and -not $expected.Contains($relative)) {throw "Unlisted package file: $relative. Use a fresh extraction directory."}
}
$GameDir=Find-Dmc5Directory
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'DevilMayCry5.exe') -PathType Leaf)) {throw 'DevilMayCry5.exe was not found.'}
if (Get-Process -Name 'DevilMayCry5','DMC5DualSense.Bridge' -ErrorAction SilentlyContinue) {throw 'Close DMC5 and its Bridge before installing.'}
$mod=Safe-Child $GameDir 'DMC5DualSense'
$manifest=Join-Path $mod 'install-manifest.json'
$old=if(Test-Path -LiteralPath $manifest){Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json}else{$null}
$targets=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($relative in @('dinput8.dll','re2_fw_config.txt','reframework\plugins\DMC5DualSense.dll','reframework\plugins\source\DMC5DualSense.cs')){
    [void]$targets.Add((Safe-Child $GameDir $relative))
}
foreach($file in $release.Files){if([string]$file.Path -like 'UI/*'){[void]$targets.Add((Safe-Child $GameDir ([string]$file.Path).Substring(3)))}}
if($old){
    $oldRemoved=if($old.PSObject.Properties['RemovedFiles']){@($old.RemovedFiles)}else{@()}
    foreach($file in @($old.Files)+$oldRemoved){if($file -and $file.RelativePath){[void]$targets.Add((Safe-Child $GameDir $file.RelativePath))}}
}
$roots=@($mod,(Join-Path $GameDir 'reframework'),(Join-Path $GameDir 'natives'))
$dirsBefore=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($root in $roots){
    if(-not(Test-Path -LiteralPath $root)){continue}
    [void]$dirsBefore.Add($root)
    foreach($entry in Get-ChildItem -LiteralPath $root -Recurse -Force){
        [void](Safe-Child $GameDir $entry.FullName.Substring($GameDir.TrimEnd('\').Length+1))
        if($entry.PSIsContainer){[void]$dirsBefore.Add($entry.FullName)}elseif($root -eq $mod){[void]$targets.Add($entry.FullName)}
    }
}
$snapshot=Join-Path ([IO.Path]::GetTempPath()) ('DMC5DS-transaction-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $snapshot | Out-Null
$saved=[Collections.Generic.List[object]]::new()
$pakRows=[Collections.Generic.List[object]]::new()
foreach($target in $targets){
    $relative=$target.Substring($GameDir.TrimEnd('\').Length+1)
    $exists=Test-Path -LiteralPath $target -PathType Leaf
    if($exists){
        $backup=Safe-Child $snapshot $relative
        New-Item -ItemType Directory -Path (Split-Path $backup) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $backup -Force
    }
    $saved.Add([pscustomobject]@{Path=$target;Relative=$relative;Existed=$exists})
}
# Snapshot only the old affected TOC rows, not the multi-gigabyte game archive.
if($old){foreach($entry in @($old.PakInvalidations)){
    $path=Safe-Child $GameDir $entry.PakRelativePath
    $stream=[IO.File]::OpenRead($path)
    try{
        if([long]$entry.EntryOffset -lt 16 -or [long]$entry.EntryOffset+48 -gt $stream.Length){throw 'Invalid saved PAK offset.'}
        $stream.Position=[long]$entry.EntryOffset
        $bytes=[byte[]]::new(48)
        if($stream.Read($bytes,0,48) -ne 48){throw 'Incomplete PAK row.'}
        $pakRows.Add([pscustomobject]@{Path=$path;Offset=[long]$entry.EntryOffset;Bytes=$bytes})
    }finally{$stream.Dispose()}
}}
$success=$false
try{
    & (Join-Path $PSScriptRoot 'Install-Core.ps1') -GameDir $GameDir -ReplaceExistingFramework:$ReplaceExistingFramework -AllowExistingFramework:$AllowExistingFramework -NonInteractive:$NonInteractive
    if(-not(Test-Path -LiteralPath $manifest) -and $old){throw 'Update did not complete.'}
    $success=$true
}catch{
    $failure=$_
    Write-Warning "Restoring transaction. Recovery snapshot (retained on failure): $snapshot"
    # Only restore the listed targets and this mod's state, never another mod directory.
    if(Test-Path -LiteralPath $mod){foreach($file in Get-ChildItem -LiteralPath $mod -File -Recurse -Force){
        [void](Safe-Child $GameDir $file.FullName.Substring($GameDir.TrimEnd('\').Length+1))
        Remove-Item -LiteralPath $file.FullName -Force
    }}
    foreach($file in $saved){
        if($file.Existed){
            New-Item -ItemType Directory -Path (Split-Path $file.Path) -Force | Out-Null
            $backup=Safe-Child $snapshot $file.Relative
            if(-not(Test-Path -LiteralPath $file.Path -PathType Leaf) -or (Get-FileHash -LiteralPath $file.Path).Hash -ne (Get-FileHash -LiteralPath $backup).Hash){
                Copy-Item -LiteralPath $backup -Destination $file.Path -Force
            }
        }elseif(Test-Path -LiteralPath $file.Path -PathType Leaf){Remove-Item -LiteralPath $file.Path -Force}
    }
    foreach($row in $pakRows){
        $stream=[IO.File]::Open($row.Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
        try{
            $stream.Position=$row.Offset
            $current=[byte[]]::new(48)
            if($stream.Read($current,0,48) -ne 48 -or [Convert]::ToBase64String($current,8,40) -ne [Convert]::ToBase64String($row.Bytes,8,40)){throw 'PAK metadata changed; automatic restore stopped.'}
            $stream.Position=$row.Offset
            $stream.Write($row.Bytes,0,8)
            $stream.Flush($true)
        }finally{$stream.Dispose()}
    }
    foreach($root in $roots){
        if(-not(Test-Path -LiteralPath $root)){continue}
        $dirs=@(Get-ChildItem -LiteralPath $root -Directory -Recurse -Force | ForEach-Object FullName)+@($root)
        foreach($dir in $dirs | Sort-Object Length -Descending){
            if(-not $dirsBefore.Contains($dir) -and -not(Get-ChildItem -LiteralPath $dir -Force)){Remove-Item -LiteralPath $dir -Force}
        }
    }
    Write-Warning "Installation failed; previous files restored. Recovery snapshot: $snapshot"
    throw $failure
}finally{
    if($success){
        $resolved=[IO.Path]::GetFullPath($snapshot)
        $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\DMC5DS-transaction-'
        if(-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe snapshot cleanup path.'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
