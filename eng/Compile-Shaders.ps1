[CmdletBinding()]
param(
    [string] $FxcPath = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$shaderDirectory = Join-Path $repositoryRoot 'src\VL.FFmpeg\Shaders'
$source = Join-Path $shaderDirectory 'VideoConvert.hlsl'

if ([string]::IsNullOrWhiteSpace($FxcPath)) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $FxcPath = Get-ChildItem -LiteralPath $kits -Recurse -Filter fxc.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\fxc.exe' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if ([string]::IsNullOrWhiteSpace($FxcPath) -or -not (Test-Path -LiteralPath $FxcPath)) {
    throw 'fxc.exe was not found. Install a Windows 10 SDK or pass -FxcPath.'
}

& $FxcPath /nologo /O3 /T vs_5_0 /E VSMain /Fo (Join-Path $shaderDirectory 'VideoConvert.vs.cso') $source
if ($LASTEXITCODE -ne 0) { throw 'Vertex shader compilation failed.' }

& $FxcPath /nologo /O3 /T ps_5_0 /E PSMain /Fo (Join-Path $shaderDirectory 'VideoConvert.ps.cso') $source
if ($LASTEXITCODE -ne 0) { throw 'Pixel shader compilation failed.' }
