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
$BinTest   = Join-Path $BinDir "TestDriver"

# ── Clean ─────────────────────────────────────────────────────────────────────

if ($Clean) {
    Write-Host "`n[Clean] Removing bin\ ..." -ForegroundColor Yellow
    if (Test-Path $BinDir) { Remove-Item $BinDir -Recurse -Force }

    $uiProj = Join-Path $Root "src\ui\KernelFlirt.UI.csproj"
    & dotnet clean $uiProj -c $Configuration --nologo -v quiet 2>$null
}

# ── Create output dirs ──────────────────────────────────────────────────────

foreach ($d in @($BinUI, $BinDriver, $BinLoader, $BinRelay, $BinTest)) {
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
$totalSteps = if ($UIOnly) { 1 } else { 5 }

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

        # ── Build Test Driver ───────────────────────────────────────────────
        Write-Host "`n[$stepNum/$totalSteps] Building TestDriver ($Configuration) ..." -ForegroundColor Green
        $testProj = Join-Path $Root "src\testdriver\testdriver.vcxproj"
        & $MSBuild $testProj /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "TestDriver build failed." }

        $testOut = Join-Path $Root "src\testdriver\build\testdriver\$Configuration"
        if (Test-Path "$testOut\KfTestDriver.sys") {
            Copy-Item "$testOut\KfTestDriver.sys" $BinTest -Force
            Copy-Item "$testOut\KfTestDriver.pdb" $BinTest -Force -ErrorAction SilentlyContinue
            Write-Host "  -> bin\TestDriver\KfTestDriver.sys" -ForegroundColor DarkGreen
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

# Copy RetDec decompiler
$retDecSrc = Join-Path $Root "retdec-bin"
if (Test-Path $retDecSrc) {
    $retDecBin   = Join-Path $retDecSrc "bin"
    $retDecShare = Join-Path $retDecSrc "share"
    $retDecDst   = Join-Path $BinUI "retdec"

    if (Test-Path $retDecBin) {
        if (!(Test-Path $retDecDst)) { New-Item -ItemType Directory -Path $retDecDst -Force | Out-Null }
        Copy-Item "$retDecBin\*" $retDecDst -Recurse -Force
        Write-Host "  -> bin\UI\retdec\ (RetDec decompiler)" -ForegroundColor DarkGreen
    }
    if (Test-Path $retDecShare) {
        Copy-Item $retDecShare $BinUI -Recurse -Force
        Write-Host "  -> bin\UI\share\ (RetDec support files)" -ForegroundColor DarkGreen
    }
}

# Copy themes directory from root themes/ to bin/UI/themes/
$themesSrc = Join-Path $Root "themes"
if (Test-Path $themesSrc) {
    $themesDst = Join-Path $BinUI "themes"
    if (!(Test-Path $themesDst)) { New-Item -ItemType Directory -Path $themesDst -Force | Out-Null }
    Copy-Item "$themesSrc\*" $themesDst -Recurse -Force
    Write-Host "  -> bin\UI\themes\ (color themes)" -ForegroundColor DarkGreen
}

# Create plugins directory
$pluginsDir = Join-Path $BinUI "plugins"
if (!(Test-Path $pluginsDir)) { New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null }

# Build and copy plugins
$pluginProjects = @(
    "samples\SamplePlugin\SamplePlugin.csproj",
    "samples\AntiDebugPlugin\AntiDebugPlugin.csproj",
    "samples\ThemidaPlugin\ThemidaPlugin.csproj",
    "samples\ApiMonitorPlugin\ApiMonitorPlugin.csproj",
    "samples\StringDecryptorPlugin\StringDecryptorPlugin.csproj"
)
foreach ($pluginRelPath in $pluginProjects) {
    $pluginProj = Join-Path $Root $pluginRelPath
    if (Test-Path $pluginProj) {
        & dotnet build $pluginProj -c $Configuration --nologo -v quiet 2>$null
        $pluginName = [System.IO.Path]::GetFileNameWithoutExtension($pluginRelPath)
        $pluginDir  = Join-Path $Root ([System.IO.Path]::GetDirectoryName($pluginRelPath))
        # Try net9.0-windows first (WPF plugins), then net9.0
        $pluginDll = Join-Path $pluginDir "bin\$Configuration\net9.0-windows\$pluginName.dll"
        if (!(Test-Path $pluginDll)) {
            $pluginDll = Join-Path $pluginDir "bin\$Configuration\net9.0\$pluginName.dll"
        }
        if (Test-Path $pluginDll) {
            try {
                Copy-Item $pluginDll $pluginsDir -Force -ErrorAction Stop
                Write-Host "  -> bin\UI\plugins\$pluginName.dll" -ForegroundColor DarkGreen
            } catch {
                Write-Host "  -> bin\UI\plugins\$pluginName.dll (LOCKED, skipped)" -ForegroundColor Yellow
            }
            # Copy plugin dependencies (NuGet DLLs) that aren't already in UI output
            $pluginOutDir = [System.IO.Path]::GetDirectoryName($pluginDll)
            $uiOutDir = Join-Path $Root "src\ui\bin\$Configuration\net9.0-windows"
            foreach ($dep in Get-ChildItem $pluginOutDir -Filter "*.dll") {
                if ($dep.Name -ne "$pluginName.dll" -and !(Test-Path (Join-Path $uiOutDir $dep.Name))) {
                    try {
                        Copy-Item $dep.FullName $pluginsDir -Force -ErrorAction Stop
                        Write-Host "  -> bin\UI\plugins\$($dep.Name)" -ForegroundColor DarkGray
                    } catch {
                        Write-Host "  -> bin\UI\plugins\$($dep.Name) (LOCKED, skipped)" -ForegroundColor Yellow
                    }
                }
            }
        }
    }
}

# Build AntiDebugTest (native C, needs MSVC cl.exe)
$antiDebugSrc = Join-Path $Root "samples\AntiDebugTest\antidebug_test.c"
if ((Test-Path $antiDebugSrc) -and $canBuildNative) {
    $BinSamples = Join-Path $BinDir "Samples"
    if (!(Test-Path $BinSamples)) { New-Item -ItemType Directory -Path $BinSamples -Force | Out-Null }

    # Find vcvarsall.bat from VS installation
    $vsInstall = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath 2>$null
    if ($vsInstall) {
        $vcvars = Join-Path $vsInstall "VC\Auxiliary\Build\vcvarsall.bat"
    }
    if ($vcvars -and (Test-Path $vcvars)) {
        $antiDebugDir = Join-Path $Root "samples\AntiDebugTest"
        cmd /c "call `"$vcvars`" x64 >nul 2>&1 && cd /d `"$antiDebugDir`" && cl /O2 /Zi /W3 /GS- /D_CRT_SECURE_NO_WARNINGS /DNDEBUG antidebug_test.c /Fe:antidebug_test.exe /link /DEBUG /OPT:REF /OPT:ICF /INCREMENTAL:NO user32.lib kernel32.lib >nul 2>&1"
        if ($LASTEXITCODE -eq 0) {
            Copy-Item (Join-Path $antiDebugDir "antidebug_test.exe") $BinSamples -Force
            Copy-Item (Join-Path $antiDebugDir "antidebug_test.pdb") $BinSamples -Force -ErrorAction SilentlyContinue
            Write-Host "  -> bin\Samples\antidebug_test.exe + .pdb" -ForegroundColor DarkGreen
        } else {
            Write-Host "  [WARNING] AntiDebugTest compilation failed" -ForegroundColor Yellow
        }
    }
}

# Build StringDecryptorTest samples (native C, needs MSVC cl.exe)
$strDecTestDir = Join-Path $Root "samples\StringDecryptorTest"
if ((Test-Path $strDecTestDir) -and $canBuildNative) {
    $BinSamples = Join-Path $BinDir "Samples"
    if (!(Test-Path $BinSamples)) { New-Item -ItemType Directory -Path $BinSamples -Force | Out-Null }

    $vsInstall = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath 2>$null
    if ($vsInstall) { $vcvars = Join-Path $vsInstall "VC\Auxiliary\Build\vcvarsall.bat" }

    if ($vcvars -and (Test-Path $vcvars)) {
        foreach ($testSrc in @("xor_strings.c", "rc4_strings.c")) {
            $testName = [System.IO.Path]::GetFileNameWithoutExtension($testSrc)
            cmd /c "call `"$vcvars`" x64 >nul 2>&1 && cd /d `"$strDecTestDir`" && cl /Od /Zi /W3 /GS- /MT $testSrc /Fe:$testName.exe /link /RELEASE /DEBUG /ENTRY:Entry /SUBSYSTEM:CONSOLE /NODEFAULTLIB /INCREMENTAL:NO kernel32.lib >nul 2>&1"
            if ($LASTEXITCODE -eq 0) {
                Copy-Item (Join-Path $strDecTestDir "$testName.exe") $BinSamples -Force
                Copy-Item (Join-Path $strDecTestDir "$testName.pdb") $BinSamples -Force -ErrorAction SilentlyContinue
                Write-Host "  -> bin\Samples\$testName.exe + .pdb" -ForegroundColor DarkGreen
            } else {
                Write-Host "  [WARNING] $testName compilation failed" -ForegroundColor Yellow
            }
        }
    }
}

# Copy KD debugger (kd.exe, dbgeng.dll, dbghelp.dll, symsrv.dll, etc.)
$kdSrc = Join-Path $Root "KD"
if (Test-Path $kdSrc) {
    Copy-Item "$kdSrc\*" $BinUI -Force
    Write-Host "  -> bin\UI\kd.exe + dbgeng DLLs (KD debugger)" -ForegroundColor DarkGreen
}

# ── Sign Drivers ─────────────────────────────────────────────────────────────

$driversToSign = @()
$driverSys = Join-Path $BinDriver "KernelFlirt.sys"
$testSys   = Join-Path $BinTest   "KfTestDriver.sys"
if (Test-Path $driverSys) { $driversToSign += $driverSys }
if (Test-Path $testSys)   { $driversToSign += $testSys }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($driversToSign.Count -gt 0 -and !$isAdmin) {
    Write-Host "`n[WARNING] Not running as Administrator - skipping driver signing." -ForegroundColor Yellow
    Write-Host "  Run build.ps1 from an elevated PowerShell to auto-sign drivers." -ForegroundColor Yellow
}
elseif ($driversToSign.Count -gt 0) {
    Write-Host "`nSigning drivers ..." -ForegroundColor Green

    # Find signtool.exe
    function Find-SignTool {
        $pf86 = [Environment]::GetFolderPath("ProgramFilesX86")
        $sdkRoot = Join-Path $pf86 "Windows Kits\10\bin"
        if (Test-Path $sdkRoot) {
            $versions = Get-ChildItem $sdkRoot -Directory |
                Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
                Sort-Object Name -Descending
            foreach ($v in $versions) {
                $st = Join-Path $v.FullName "x64\signtool.exe"
                if (Test-Path $st) { return $st }
            }
        }
        $inPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if ($inPath) { return $inPath.Source }
        return $null
    }

    $SignTool = Find-SignTool
    if (!$SignTool) {
        Write-Host "  [WARNING] signtool.exe not found - skipping signing. Install Windows SDK." -ForegroundColor Yellow
    }
    else {
        $CertName = "KernelFlirt Test"
        $certStore = "Cert:\LocalMachine\My"
        $rootStore = "Cert:\LocalMachine\Root"
        $tpStore   = "Cert:\LocalMachine\TrustedPublisher"

        $cert = Get-ChildItem $certStore |
            Where-Object { $_.Subject -eq "CN=$CertName" } |
            Select-Object -First 1

        if (!$cert) {
            Write-Host "  Creating self-signed certificate '$CertName' ..." -ForegroundColor Green
            $certParams = @{
                Type              = "CodeSigningCert"
                Subject           = "CN=$CertName"
                CertStoreLocation = $certStore
                NotAfter          = (Get-Date).AddYears(5)
                KeyAlgorithm      = "RSA"
                KeyLength         = 2048
                HashAlgorithm     = "SHA256"
                Provider          = "Microsoft Enhanced RSA and AES Cryptographic Provider"
            }
            $cert = New-SelfSignedCertificate @certParams
            Write-Host "  Thumbprint: $($cert.Thumbprint)" -ForegroundColor DarkGreen

            # Export to Trusted Root CA and TrustedPublisher
            $tmpCer = Join-Path $env:TEMP "KernelFlirt_test.cer"
            Export-Certificate -Cert $cert -FilePath $tmpCer -Force | Out-Null
            Import-Certificate -FilePath $tmpCer -CertStoreLocation $rootStore | Out-Null
            Import-Certificate -FilePath $tmpCer -CertStoreLocation $tpStore | Out-Null
            Remove-Item $tmpCer -Force
            Write-Host "  Certificate added to Root and TrustedPublisher stores." -ForegroundColor DarkGreen
        }
        else {
            Write-Host "  Using existing certificate '$CertName' ($($cert.Thumbprint))" -ForegroundColor Cyan
        }

        $thumbprint = $cert.Thumbprint
        foreach ($sysFile in $driversToSign) {
            $fileName = [System.IO.Path]::GetFileName($sysFile)
            Write-Host "  Signing $fileName ..." -ForegroundColor Green
            $signArgs = @("sign", "/v", "/sm", "/sha1", $thumbprint, "/fd", "SHA256", "/t", "http://timestamp.digicert.com", $sysFile)
            & $SignTool @signArgs 2>$null
            if ($LASTEXITCODE -ne 0) {
                # Retry without timestamp
                $signArgs2 = @("sign", "/v", "/sm", "/sha1", $thumbprint, "/fd", "SHA256", $sysFile)
                & $SignTool @signArgs2 2>$null
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "  [WARNING] Failed to sign $fileName" -ForegroundColor Yellow
                }
                else {
                    Write-Host "  -> $fileName signed (no timestamp)" -ForegroundColor DarkGreen
                }
            }
            else {
                Write-Host "  -> $fileName signed" -ForegroundColor DarkGreen
            }
        }
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " Build complete ($Configuration)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  bin\Driver\      KernelFlirt.sys"
Write-Host "  bin\Loader\      KfLoader.exe"
Write-Host "  bin\Relay\       KfRelay.exe"
Write-Host "  bin\TestDriver\  KfTestDriver.sys"
Write-Host "  bin\UI\          KernelFlirt.exe"
Write-Host "  bin\Samples\     antidebug_test.exe, xor_strings.exe, rc4_strings.exe"
Write-Host ""
Write-Host "Usage:" -ForegroundColor Yellow
Write-Host "  1. On VM: KfLoader.exe install + start"
Write-Host "  2. On VM: KfRelay.exe (listens on port 31337)"
Write-Host "  3. On Host: KernelFlirt.exe -> Connect -> vm_ip:31337"
Write-Host "  -- OR local: KernelFlirt.exe -> Connect -> (blank for local driver)"
Write-Host ""
