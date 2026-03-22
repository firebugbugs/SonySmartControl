param(
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

function Resolve-CMakeExe {
    $cmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "${env:ProgramFiles}\CMake\bin\cmake.exe"
        "${env:ProgramFiles(x86)}\CMake\bin\cmake.exe"
    )
    foreach ($edition in @("Community", "Professional", "Enterprise", "Preview", "BuildTools")) {
        $candidates += "${env:ProgramFiles}\Microsoft Visual Studio\2022\$edition\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    }

    foreach ($p in $candidates) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.CMake.Project -property installationPath 2>$null
        if ($LASTEXITCODE -eq 0 -and $installPath) {
            $cmake = Join-Path $installPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
            if (Test-Path -LiteralPath $cmake) { return $cmake }
        }
        $installPath = & $vswhere -latest -products * -property installationPath 2>$null
        if ($LASTEXITCODE -eq 0 -and $installPath) {
            $cmake = Join-Path $installPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
            if (Test-Path -LiteralPath $cmake) { return $cmake }
        }
    }

    return $null
}

$cmakeExe = Resolve-CMakeExe
if (-not $cmakeExe) {
    Write-Error @'
cmake.exe was not found (not on PATH and not under common VS/CMake install locations).

Fix one of:
  1) Install CMake from https://cmake.org/download/ and add it to PATH
  2) VS Installer -> Modify -> Individual components -> C++ CMake tools for Windows
  3) Run this script from x64 Native Tools Command Prompt for VS 2022
'@
}

$here = $PSScriptRoot
# ??????????????????????????????.. ??????????????????
$sdkRoot = Join-Path $here '..\CrSDK_v2.01.00_20260203a_Win64\RemoteCli'
$sdkRoot = [System.IO.Path]::GetFullPath($sdkRoot)

if (-not (Test-Path (Join-Path $sdkRoot 'app\CRSDK\CameraRemote_SDK.h'))) {
    Write-Error ('CameraRemote_SDK.h not found under RemoteCli. Current SONY_CRSDK_ROOT: ' + $sdkRoot)
}

$build = Join-Path $here "build"

if ($Clean -and (Test-Path -LiteralPath $build)) {
    Write-Host "Removing build dir (-Clean): $build" -ForegroundColor Yellow
    Remove-Item -Recurse -Force -LiteralPath $build
}

# ? CMakeCache ??????? native\SonyCrBridge????????????????? build?
$cacheFile = Join-Path $build "CMakeCache.txt"
if ((Test-Path -LiteralPath $cacheFile) -and -not $Clean) {
    $cacheContent = Get-Content -LiteralPath $cacheFile -Raw -ErrorAction SilentlyContinue
    if ($cacheContent -match 'native[/\\]SonyCrBridge' -or ($cacheContent -match 'CMAKE_HOME_DIRECTORY:INTERNAL=(.+)' -and $Matches[1] -notmatch [regex]::Escape($here))) {
        Write-Warning "CMake cache ??????????????? build ????????????: Remove-Item -Recurse -Force build ; .\build-windows.ps1?"
        Remove-Item -Recurse -Force -LiteralPath $build -ErrorAction SilentlyContinue
    }
}

New-Item -ItemType Directory -Force -Path $build | Out-Null

Write-Host "cmake=$cmakeExe" -ForegroundColor Cyan
Write-Host "SONY_CRSDK_ROOT=$sdkRoot" -ForegroundColor Cyan
Write-Host "Build dir: $build" -ForegroundColor Cyan

& $cmakeExe -S $here -B $build -A x64 `
    -DSONY_CR_BRIDGE_STUB=OFF `
    -DSONY_CRSDK_ROOT="$sdkRoot"

& $cmakeExe --build $build --config Release

$dll = Join-Path $build "Release\SonyCrBridge.dll"
if (-not (Test-Path $dll)) {
    $dll = Join-Path $build "SonyCrBridge.dll"
}
if (Test-Path $dll) {
    Write-Host "OK: $dll" -ForegroundColor Green
    Write-Host "Rebuild SonyDemo to copy this DLL into the app output if your csproj references it."
} else {
    Write-Error "SonyCrBridge.dll was not produced; see CMake/MSBuild errors above."
}
