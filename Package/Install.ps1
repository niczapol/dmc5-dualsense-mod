param([string]$GameDir,[switch]$ReplaceExistingFramework,[switch]$AllowExistingFramework,[switch]$NonInteractive)
& (Join-Path $PSScriptRoot 'Install-Transactional.ps1') @PSBoundParameters
