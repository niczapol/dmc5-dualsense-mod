param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $Destination){throw 'Choose a new input directory.'}
New-Item -ItemType Directory -Path $Destination | Out-Null
$zip=Join-Path $Destination 'baseline.zip'
Invoke-WebRequest -Uri 'https://github.com/niczapol/dmc5-dualsense-mod/releases/download/v1.7.3/DMC5DualSense-Native-1.7.3-win-x64.zip' -OutFile $zip
if((Get-FileHash -LiteralPath $zip).Hash -ne '6A7DE56FC876E5355EA051991D2C6BB26F85466A9208D3852DE972BB20288974'){throw 'Pinned baseline download hash mismatch.'}
Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $Destination 'payload')
Write-Host 'Verified baseline media extracted. Builders also validate every UI/WAV against release-assets.json.'
