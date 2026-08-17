[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$archiveName = 'ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1.zip'
$archiveUri = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-12-13-15/' + $archiveName
$expectedSha256 = '375df631ddf38bf38feb7bbd67259c454045b8ea75b96af62c33a440ba799f48'
$requiredDlls = @(
    'avcodec-62.dll',
    'avformat-62.dll',
    'avutil-60.dll',
    'swresample-6.dll',
    'swscale-9.dll'
)

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeDirectory = Join-Path $repositoryRoot 'runtimes\win-x64\native'
$licenseDirectory = Join-Path $repositoryRoot 'LICENSES'
$licenseTarget = Join-Path $licenseDirectory 'FFmpeg-LGPL-3.0.txt'

$isComplete = (Test-Path -LiteralPath $licenseTarget)
foreach ($dll in $requiredDlls) {
    $isComplete = $isComplete -and (Test-Path -LiteralPath (Join-Path $runtimeDirectory $dll))
}

if ($isComplete -and -not $Force) {
    Write-Host "Pinned FFmpeg runtime is already present in $runtimeDirectory"
    return
}

$systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryDirectory = Join-Path $systemTemp ('VL.FFmpeg-' + [guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $temporaryDirectory $archiveName
$extractDirectory = Join-Path $temporaryDirectory 'extract'

try {
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    New-Item -ItemType Directory -Path $extractDirectory | Out-Null

    Write-Host "Downloading $archiveName"
    Invoke-WebRequest -Uri $archiveUri -OutFile $archivePath -UseBasicParsing

    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "FFmpeg archive SHA-256 mismatch. Expected $expectedSha256, got $actualSha256."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDirectory

    $codecDll = Get-ChildItem -LiteralPath $extractDirectory -Recurse -File -Filter 'avcodec-62.dll' |
        Select-Object -First 1
    if ($null -eq $codecDll -or $codecDll.Directory.Name -ne 'bin') {
        throw 'The verified archive does not contain the expected bin\avcodec-62.dll layout.'
    }

    $sourceBin = $codecDll.Directory.FullName
    $sourceRoot = Split-Path -Parent $sourceBin
    $sourceLicense = Join-Path $sourceRoot 'LICENSE.txt'
    if (-not (Test-Path -LiteralPath $sourceLicense)) {
        throw 'The verified archive does not contain LICENSE.txt.'
    }

    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null

    foreach ($dll in $requiredDlls) {
        $sourceDll = Join-Path $sourceBin $dll
        if (-not (Test-Path -LiteralPath $sourceDll)) {
            throw "The verified archive is missing $dll."
        }
        Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $runtimeDirectory $dll) -Force
    }

    Copy-Item -LiteralPath $sourceLicense -Destination $licenseTarget -Force
    Write-Host "Installed pinned LGPL FFmpeg runtime in $runtimeDirectory"
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        $resolvedTemporaryDirectory = [System.IO.Path]::GetFullPath($temporaryDirectory)
        if (-not $resolvedTemporaryDirectory.StartsWith($systemTemp, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([System.IO.Path]::GetFileName($resolvedTemporaryDirectory)).StartsWith('VL.FFmpeg-', [System.StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected temporary path: $resolvedTemporaryDirectory"
        }
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
    }
}

