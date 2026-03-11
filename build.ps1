#Requires -Version 5.1
<#
.SYNOPSIS
    Build script for KernelFlirt — all modules.
.DESCRIPTION
    Builds driver (.sys), loader, relay, and WPF UI.
    Copies all artifacts to bin\.
.PARAMETER Configuration
    Build configuration: Release (default) or Debug.
.PARAMETER Clean
    Remove bin\ directories before building.
.PARAMETER UIOnly
    Build only the UI (skip native C++ modules).
.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
    .\build.ps1 -Clean
    .\build.ps1 -UIOnly
#>
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$Clean,
    [switch]$UIOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$BinDir    = Join-Path $Root "bin"
$BinUI     = Join-Path $BinDir "UI"
$BinDriver = Join-Path $BinDir "Driver"
$BinLoader = Join-Path $BinDir "Loader"
$BinRelay  = Join-Path $BinDir "Relay"

# ── Clean ─────────────────────────────────────────────────────────────────────

if ($Clean) {
    Write-Host "`n[Clean] Removing bin\ ..." -ForegroundColor Yellow
    if (Test-Path $BinDir) { Remove-Item $BinDir -Recurse -Force }

    $uiProj = Join-Path $Root "src\ui\KernelFlirt.UI.csproj"
    & dotnet clean $uiProj -c $Configuration --nologo -v quiet 2>$null
}

# ── Create output dirs ──────────────────────────────────────────────────────

foreach ($d in @($BinUI, $BinDriver, $BinLoader, $BinRelay)) {
    if (!(Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# ── Find MSBuild ─────────────────────────────────────────────────────────────

function Find-MSBuild {
    # Try vswhere first
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vsPath = & $vswhere -latest -property installationPath 2>$null
        if ($vsPath) {
            $msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $msbuild) { return $msbuild }
        }
    }
    # Fallback: try PATH
    $found = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    return $null
}

$MSBuild = Find-MSBuild
$canBuildNative = $null -ne $MSBuild

$stepNum = 1
$totalSteps = if ($UIOnly) { 1 } else { 4 }

# ── Build Driver (.sys) ─────────────────────────────────────────────────────

if (!$UIOnly) {
    if (!$canBuildNative) {
        Write-Host "`n[WARNING] MSBuild not found -- skipping native modules (driver, loader, relay)" -ForegroundColor Yellow
        Write-Host "  Install Visual Studio or Build Tools with C++ workload to build native modules.`n"
        $totalSteps = 1
    }
    else {
        Write-Host "`n[$stepNum/$totalSteps] Building Driver ($Configuration) ..." -ForegroundColor Green
        $driverProj = Join-Path $Root "src\driver\driver.vcxproj"
        & $MSBuild $driverProj /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Driver build failed." }

        # Copy output
        $driverOut = Join-Path $Root "src\driver\build\driver\$Configuration"
        if (Test-Path "$driverOut\KernelFlirt.sys") {
            Copy-Item "$driverOut\KernelFlirt.sys" $BinDriver -Force
            Copy-Item "$driverOut\KernelFlirt.pdb" $BinDriver -Force -ErrorAction SilentlyContinue
            Write-Host "  -> bin\Driver\KernelFlirt.sys" -ForegroundColor DarkGreen
        }
        $stepNum++

        # ── Build Loader ─────────────────────────────────────────────────────

        Write-Host "`n[$stepNum/$totalSteps] Building Loader ($Configuration) ..." -ForegroundColor Green
        $loaderProj = Join-Path $Root "src\loader\loader.vcxproj"
        & $MSBuild $loaderProj /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Loader build failed." }

        $loaderOut = Join-Path $Root "src\loader\build\loader\$Configuration"
        if (Test-Path "$loaderOut\KfLoader.exe") {
            Copy-Item "$loaderOut\KfLoader.exe" $BinLoader -Force
            Copy-Item "$loaderOut\KfLoader.pdb" $BinLoader -Force -ErrorAction SilentlyContinue
            Write-Host "  -> bin\Loader\KfLoader.exe" -ForegroundColor DarkGreen
        }
        $stepNum++

        # ── Build Relay ──────────────────────────────────────────────────────

        Write-Host "`n[$stepNum/$totalSteps] Building Relay ($Configuration) ..." -ForegroundColor Green
        $relayProj = Join-Path $Root "src\relay\relay.vcxproj"
        & $MSBuild $relayProj /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Relay build failed." }

        $relayOut = Join-Path $Root "src\relay\build\relay\$Configuration"
        if (Test-Path "$relayOut\KfRelay.exe") {
            Copy-Item "$relayOut\KfRelay.exe" $BinRelay -Force
            Copy-Item "$relayOut\KfRelay.pdb" $BinRelay -Force -ErrorAction SilentlyContinue
            Write-Host "  -> bin\Relay\KfRelay.exe" -ForegroundColor DarkGreen
        }
        $stepNum++
    }
}

# ── Build UI ──────────────────────────────────────────────────────────────────

Write-Host "`n[$stepNum/$totalSteps] Building UI ($Configuration) ..." -ForegroundColor Green
$uiProj = Join-Path $Root "src\ui\KernelFlirt.UI.csproj"

& dotnet publish $uiProj `
    -c $Configuration `
    -o $BinUI `
    --nologo `
    -v minimal

if ($LASTEXITCODE -ne 0) { throw "UI build failed." }

Write-Host "  -> bin\UI\KernelFlirt.exe" -ForegroundColor DarkGreen

# Copy symbol DLLs (dbghelp.dll, symsrv.dll) next to the UI executable
foreach ($dll in @("dbghelp.dll", "symsrv.dll")) {
    $src = Join-Path $Root $dll
    if (Test-Path $src) {
        Copy-Item $src $BinUI -Force
        Write-Host "  -> bin\UI\$dll" -ForegroundColor DarkGreen
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " Build complete ($Configuration)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  bin\Driver\  KernelFlirt.sys"
Write-Host "  bin\Loader\  KfLoader.exe"
Write-Host "  bin\Relay\   KfRelay.exe"
Write-Host "  bin\UI\      KernelFlirt.exe"
Write-Host ""
Write-Host "Usage:" -ForegroundColor Yellow
Write-Host "  1. On VM: KfLoader.exe install + start"
Write-Host "  2. On VM: KfRelay.exe (listens on port 31337)"
Write-Host "  3. On Host: KernelFlirt.exe -> Connect -> vm_ip:31337"
Write-Host "  -- OR local: KernelFlirt.exe -> Connect -> (blank for local driver)"
Write-Host ""
