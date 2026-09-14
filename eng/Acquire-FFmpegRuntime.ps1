[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

# Vendored LGPL shared FFmpeg 8.1 binaries. Do not download BtbN autobuilds:
# those release assets are deleted and CI 404s. Refreshing the runtime means
# replacing these files and updating the hashes below.
# The LGPL text is checked for presence only: Git on Windows may change its
# line endings, so hashing that file is not stable.
$requiredDlls = [ordered]@{
    'runtimes\win-x64\native\avcodec-62.dll'   = 'c6033284027a2da01018503b8677176878d7caa4836de71fa695ddee59fac64f'
    'runtimes\win-x64\native\avformat-62.dll'  = 'a584a9590110c5fd631fce7a86d715de95a5dc91ea866401f1a6358973ada397'
    'runtimes\win-x64\native\avutil-60.dll'    = '4627a38fe77213af8cba4e2cee2e1376df48ca1872416e0889dd7b7dedbeadc2'
    'runtimes\win-x64\native\swresample-6.dll' = '4f17df0f7c8913baab07ae40231f100b95dac59389e314d235c1f1f678008e46'
    'runtimes\win-x64\native\swscale-9.dll'    = '32a459c634b234811c5781b0dbb0fcc143b58c000d893e20d7f65231659e89c2'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$licensePath = Join-Path $repositoryRoot 'LICENSES\FFmpeg-LGPL-3.0.txt'
$missing = New-Object System.Collections.Generic.List[string]

foreach ($relativePath in $requiredDlls.Keys) {
    $fullPath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        $missing.Add($relativePath)
        continue
    }

    $actualSha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedSha256 = $requiredDlls[$relativePath]
    if ($actualSha256 -ne $expectedSha256) {
        throw "FFmpeg runtime SHA-256 mismatch for $relativePath. Expected $expectedSha256, got $actualSha256."
    }
}

if (-not (Test-Path -LiteralPath $licensePath)) {
    $missing.Add('LICENSES\FFmpeg-LGPL-3.0.txt')
}

if ($missing.Count -gt 0) {
    throw "Vendored FFmpeg runtime is missing: $($missing -join ', '). Restore the files from git; do not download BtbN autobuild archives."
}

if ($Force) {
    Write-Host 'Vendored FFmpeg runtime hashes were rechecked.'
    return
}

Write-Host "Vendored FFmpeg runtime is present under $(Join-Path $repositoryRoot 'runtimes\win-x64\native')"
