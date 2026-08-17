[CmdletBinding()]
param(
    [string] $NuGetExe = "",
    [string] $OutputDirectory = "artifacts\packages"
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nuspecPath = Join-Path $repositoryRoot 'deployment\VL.FFmpeg.nuspec'
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$libPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'lib'))

if ([string]::IsNullOrWhiteSpace($NuGetExe)) {
    $nugetCommand = Get-Command 'nuget.exe' -ErrorAction SilentlyContinue
    if ($null -ne $nugetCommand) {
        $NuGetExe = $nugetCommand.Source
    }
    else {
        $vvvvRoot = 'C:\Program Files\vvvv'
        $NuGetExe = Get-ChildItem -LiteralPath $vvvvRoot -Directory -Filter 'vvvv_gamma_*' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object { Join-Path $_.FullName 'tools\NuGet.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
    }
}

if ([string]::IsNullOrWhiteSpace($NuGetExe) -or -not (Test-Path -LiteralPath $NuGetExe)) {
    throw 'NuGet.exe was not found. Pass its path with -NuGetExe.'
}

$requiredFiles = @(
    'VL.FFmpeg.vl',
    'runtimes\win-x64\native\avcodec-62.dll',
    'runtimes\win-x64\native\avformat-62.dll',
    'runtimes\win-x64\native\avutil-60.dll',
    'runtimes\win-x64\native\swresample-6.dll',
    'runtimes\win-x64\native\swscale-9.dll',
    'LICENSES\FFmpeg-LGPL-3.0.txt',
    'help\HowTo FFmpeg Video Playback.vl'
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath))) {
        throw "Required file is missing: $relativePath"
    }
}

$rootVlPath = Join-Path $repositoryRoot 'VL.FFmpeg.vl'
$rootVlText = Get-Content -LiteralPath $rootVlPath -Raw -Encoding utf8
if ($rootVlText -notmatch 'lib[/\\]net8\.0[/\\]VL\.FFmpeg\.dll') {
    throw 'VL.FFmpeg.vl must reference lib/net8.0/VL.FFmpeg.dll before packing.'
}
if ($rootVlText -notmatch '\bIsForward="true"') {
    throw 'VL.FFmpeg.vl must mark its VL.FFmpeg.dll PlatformDependency as Is Forward before packing.'
}

Push-Location -LiteralPath $repositoryRoot
try {
    $expectedLibPath = $repositoryRoot.TrimEnd('\') + '\lib'
    if (-not $libPath.Equals($expectedLibPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected lib path: $libPath"
    }
    if (Test-Path -LiteralPath $libPath) {
        Remove-Item -LiteralPath $libPath -Recurse -Force
    }

    & dotnet build 'src\VL.FFmpeg\VL.FFmpeg.csproj' -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    $expectedBuildOutputs = @(
        'lib\net8.0\VL.FFmpeg.dll',
        'lib\net8.0\VL.FFmpeg.xml'
    )
    foreach ($relativePath in $expectedBuildOutputs) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath))) {
            throw "Build output is missing or misplaced: $relativePath"
        }
    }

    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    & $NuGetExe pack $nuspecPath -OutputDirectory $outputPath -NonInteractive
    if ($LASTEXITCODE -ne 0) {
        throw "nuget pack failed with exit code $LASTEXITCODE."
    }

    [xml] $nuspec = Get-Content -LiteralPath $nuspecPath -Raw -Encoding utf8
    $version = [string] $nuspec.package.metadata.version
    $packagePath = Join-Path $outputPath "VL.FFmpeg.$version.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath)) {
        throw "Expected package was not created: $packagePath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $requiredEntries = @(
            'VL.FFmpeg.vl',
            'lib/net8.0/VL.FFmpeg.dll',
            'lib/net8.0/VL.FFmpeg.xml',
            'help/HowTo FFmpeg Video Playback.vl'
        )
        foreach ($entry in $requiredEntries) {
            if ($entries -cnotcontains $entry) {
                throw "Package entry is missing or has incorrect casing/path: $entry"
            }
        }

        $managedDlls = @($entries | Where-Object { $_ -match '^lib/.+\.dll$' })
        $expectedManagedDlls = @(
            'lib/net8.0/VL.FFmpeg.dll'
        )
        $unexpectedManagedDlls = @($managedDlls | Where-Object { $expectedManagedDlls -cnotcontains $_ })
        if ($unexpectedManagedDlls.Count -gt 0) {
            throw "Unexpected managed DLLs were packed: $($unexpectedManagedDlls -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Host "Created $packagePath"
    Write-Warning 'This command does not compile VL.FFmpeg.vl. Validate the package in Gamma before publishing.'
}
finally {
    Pop-Location
}
