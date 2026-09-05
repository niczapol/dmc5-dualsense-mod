[CmdletBinding()]
param([string]$ToolsRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ToolsRoot) { $ToolsRoot = Join-Path $repoRoot '.tools\native' }
$tools = [IO.Path]::GetFullPath($ToolsRoot)
New-Item -ItemType Directory -Path $tools -Force | Out-Null

function Get-Hash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-PinnedFile(
    [string]$Url,
    [string]$Destination,
    [long]$Size,
    [string]$Sha256,
    [switch]$CanonicalLf
) {
    function Test-PinnedContent([string]$Path) {
        if ($CanonicalLf) {
            # Git checkouts may convert CRLF; compare the exact canonical text,
            # not a machine-specific line ending representation.
            $bytes = [Text.Encoding]::UTF8.GetBytes(([IO.File]::ReadAllText($Path)).Replace("`r`n", "`n"))
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-', '') }
            finally { $hasher.Dispose() }
            return $bytes.Length -eq $Size -and $hash -eq $Sha256
        }
        return (Get-Item -LiteralPath $Path).Length -eq $Size -and (Get-Hash $Path) -eq $Sha256
    }
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        if (-not (Test-PinnedContent $Destination)) {
            throw "Existing pinned input has the wrong size or hash: $Destination"
        }
        return
    }

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = $Destination + '.download'
    try {
        Invoke-WebRequest -Uri $Url -OutFile $temporary
        if (-not (Test-PinnedContent $temporary)) {
            throw "Downloaded pinned input failed validation: $Url"
        }
        Move-Item -LiteralPath $temporary -Destination $Destination
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

$llvmArchiveName = 'llvm-mingw-20260616-ucrt-x86_64.zip'
$llvmArchive = Join-Path $tools $llvmArchiveName
$llvmRoot = Join-Path $tools 'llvm-mingw'
$compiler = Join-Path $llvmRoot 'bin\clang++.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    Get-PinnedFile `
        'https://github.com/mstorsjo/llvm-mingw/releases/download/20260616/llvm-mingw-20260616-ucrt-x86_64.zip' `
        $llvmArchive `
        187504083 `
        'B9B68A4D276E16FA25802AABA458E4638F64B3884C290AACCDC2D87083B6CA35'

    $extractRoot = Join-Path $tools ('llvm-extract-' + [Guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -LiteralPath $llvmArchive -DestinationPath $extractRoot
        $expanded = Join-Path $extractRoot 'llvm-mingw-20260616-ucrt-x86_64'
        if (-not (Test-Path -LiteralPath (Join-Path $expanded 'bin\clang++.exe') -PathType Leaf)) {
            throw 'Pinned LLVM-MinGW archive has an unexpected layout.'
        }
        Move-Item -LiteralPath $expanded -Destination $llvmRoot
    }
    finally {
        if (Test-Path -LiteralPath $extractRoot -PathType Container) {
            $resolvedExtract = [IO.Path]::GetFullPath($extractRoot)
            $resolvedTools = $tools.TrimEnd('\') + '\'
            if (-not $resolvedExtract.StartsWith($resolvedTools, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to clean a dependency path outside ToolsRoot: $resolvedExtract"
            }
            Remove-Item -LiteralPath $resolvedExtract -Recurse -Force
        }
    }
}

$refCommit = '684ca77369ec1050e844e8651a9b1d5b7c5aa370'
$refRoot = Join-Path $tools 'vendor\REFramework'
foreach ($file in @(
    @{
        Relative = 'include\reframework\API.h'
        Size = 20200
        Hash = '6417FEDDBA2728E06BB04DF94B20281713FD5AA328AFA2BCB718A8B6DD357281'
    },
    @{
        Relative = 'include\reframework\API.hpp'
        Size = 35509
        Hash = '6F8A85A2A440C0D3C29D0829A52F85608F07478E8742804C7476DDC266BD6FE8'
    }
)) {
    $urlPath = ([string]$file.Relative).Replace('\', '/')
    Get-PinnedFile `
        ("https://raw.githubusercontent.com/praydog/REFramework/$refCommit/$urlPath") `
        (Join-Path $refRoot ([string]$file.Relative)) `
        ([long]$file.Size) `
        ([string]$file.Hash) -CanonicalLf
}

[pscustomobject]@{
    ToolsRoot = $tools
    Compiler = $compiler
    CompilerVersion = (& $compiler --version | Select-Object -First 1)
    REFrameworkCommit = $refCommit
}
