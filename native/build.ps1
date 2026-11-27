# Builds the present hook and the test apps (x64, Visual Studio 2022+ with CMake).
#   powershell -File native/build.ps1 [-Config Release|Debug] [-Generator "Visual Studio 17 2022"]
# Output: native/out/bin/<Config>/lookback-hook.dll, lookback-testapp-d3d11.exe, lookback-testapp-d3d12.exe
param(
    [ValidateSet('Release', 'Debug')][string]$Config = 'Release',
    [string]$Generator = ''
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here 'out'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath } else { $null }
$vsVersion = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion } else { $null }
if (-not $Generator) {
    $major = if ($vsVersion) { [int]($vsVersion.Split('.')[0]) } else { 17 }
    $Generator = switch ($major) { 17 { 'Visual Studio 17 2022' } 18 { 'Visual Studio 18 2026' } default { 'Visual Studio 17 2022' } }
}
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake -and $vs) {
    $cand = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (Test-Path $cand) { $cmake = $cand }
}
if (-not $cmake) { throw 'cmake not found (install CMake or the Visual Studio C++ CMake tools)' }
& $cmake -S $here -B $out -G $Generator -A x64 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed ($LASTEXITCODE)" }
& $cmake --build $out --config $Config --parallel | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cmake build failed ($LASTEXITCODE)" }
Write-Output (Join-Path $out "bin\$Config")
