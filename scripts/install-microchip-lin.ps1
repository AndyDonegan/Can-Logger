[CmdletBinding()]
param([switch]$DownloadOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'vendor\microchip-lin\package.json') -Raw | ConvertFrom-Json
$cache = Join-Path $projectRoot ('.vendor\microchip-lin\' + $manifest.vendorRelease)
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache $manifest.archiveFile
$installer = Join-Path $cache $manifest.installerFile

function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "Checksum mismatch: $Path. Refusing to run this package."
    }
}

if (!(Test-Path -LiteralPath $archive)) {
    $temporary = "$archive.download"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $manifest.archiveUrl -OutFile $temporary
        Assert-Hash $temporary $manifest.archiveSha256
        Move-Item -LiteralPath $temporary -Destination $archive
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
Assert-Hash $archive $manifest.archiveSha256
if (!(Test-Path -LiteralPath $installer)) {
    Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force
}
Assert-Hash $installer $manifest.installerSha256
Write-Host "Official Microchip package cached at $cache"
Write-Host 'The analyzer uses the Windows HID driver; this installs the Microchip application/libraries.'
if ($DownloadOnly) { return }

# Stage locally: installers are less reliable when executed from a WSL/UNC share.
$stage = Join-Path $env:TEMP ('CanLogger-Microchip-LIN-' + $manifest.vendorRelease)
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$localInstaller = Join-Path $stage $manifest.installerFile
Copy-Item -LiteralPath $installer -Destination $localInstaller -Force
Assert-Hash $localInstaller $manifest.installerSha256
Write-Host 'Approve the Windows UAC prompt and complete the Microchip setup wizard.'
$process = Start-Process -FilePath $localInstaller -Verb RunAs -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Microchip installer exited with code $($process.ExitCode)." }
Write-Host 'Installer completed. Device reception is a separate test; no LIN frames were requested by this script.'
