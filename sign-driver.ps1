#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Test-sign KernelFlirt.sys in bin\Driver\ with a self-signed certificate.
.DESCRIPTION
    Creates a self-signed code-signing certificate "KernelFlirt Test" in the
    local machine store (if it doesn't already exist), then signs the driver
    with signtool.exe using SHA256.
.PARAMETER CertName
    Certificate CN. Default: "KernelFlirt Test".
.PARAMETER Force
    Recreate the certificate even if it already exists.
.EXAMPLE
    .\sign-driver.ps1
    .\sign-driver.ps1 -Force
#>
param(
    [string]$CertName = "KernelFlirt Test",
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$DriverPath = Join-Path $Root "bin\Driver\KernelFlirt.sys"

if (!(Test-Path $DriverPath)) {
    throw "Driver not found at $DriverPath. Run .\build.ps1 first."
}

# ── Find signtool.exe ─────────────────────────────────────────────────────────

function Find-SignTool {
    $pf86 = [Environment]::GetFolderPath("ProgramFilesX86")
    $sdkRoot = Join-Path $pf86 "Windows Kits\10\bin"
    if (Test-Path $sdkRoot) {
        $versions = Get-ChildItem $sdkRoot -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } | Sort-Object Name -Descending
        foreach ($v in $versions) {
            $st = Join-Path $v.FullName "x64\signtool.exe"
            if (Test-Path $st) { return $st }
        }
    }
    $inPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($inPath) { return $inPath.Source }
    throw "signtool.exe not found. Install Windows SDK."
}

$SignTool = Find-SignTool
Write-Host "SignTool: $SignTool" -ForegroundColor Cyan

# ── Create or find certificate ────────────────────────────────────────────────

$certStore = "Cert:\LocalMachine\My"
$rootStore = "Cert:\LocalMachine\Root"

$cert = Get-ChildItem $certStore | Where-Object { $_.Subject -eq "CN=$CertName" } | Select-Object -First 1

if ($cert -and $Force) {
    Write-Host "Removing existing certificate '$CertName' ..." -ForegroundColor Yellow
    Remove-Item $cert.PSPath -Force
    $rootCert = Get-ChildItem $rootStore | Where-Object { $_.Subject -eq "CN=$CertName" } | Select-Object -First 1
    if ($rootCert) { Remove-Item $rootCert.PSPath -Force }
    $cert = $null
}

if (!$cert) {
    Write-Host "Creating self-signed certificate '$CertName' ..." -ForegroundColor Green

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

    # Export and import into Trusted Root CA
    $tmpCer = Join-Path $env:TEMP "KernelFlirt_test.cer"
    Export-Certificate -Cert $cert -FilePath $tmpCer -Force | Out-Null
    Import-Certificate -FilePath $tmpCer -CertStoreLocation $rootStore | Out-Null
    Remove-Item $tmpCer -Force

    # Also add to TrustedPublisher for driver loading
    $tpStore = "Cert:\LocalMachine\TrustedPublisher"
    $tmpCer2 = Join-Path $env:TEMP "KernelFlirt_test2.cer"
    Export-Certificate -Cert $cert -FilePath $tmpCer2 -Force | Out-Null
    Import-Certificate -FilePath $tmpCer2 -CertStoreLocation $tpStore | Out-Null
    Remove-Item $tmpCer2 -Force

    Write-Host "  Certificate added to Root and TrustedPublisher stores." -ForegroundColor DarkGreen
}
else {
    Write-Host "Using existing certificate '$CertName' (Thumbprint: $($cert.Thumbprint))" -ForegroundColor Cyan
}

# ── Sign the driver ───────────────────────────────────────────────────────────

Write-Host "`nSigning $DriverPath ..." -ForegroundColor Green

$thumbprint = $cert.Thumbprint
$signArgs = @("sign", "/v", "/sm", "/sha1", $thumbprint, "/fd", "SHA256", "/t", "http://timestamp.digicert.com", $DriverPath)
& $SignTool @signArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "Timestamp server failed, trying without timestamp ..." -ForegroundColor Yellow
    $signArgs2 = @("sign", "/v", "/sm", "/sha1", $thumbprint, "/fd", "SHA256", $DriverPath)
    & $SignTool @signArgs2
    if ($LASTEXITCODE -ne 0) { throw "Driver signing failed." }
}

# ── Verify ────────────────────────────────────────────────────────────────────

Write-Host "`nVerifying signature ..." -ForegroundColor Green

& $SignTool verify /v /pa $DriverPath

if ($LASTEXITCODE -ne 0) {
    Write-Host "Verification warning - this is expected for test-signed drivers." -ForegroundColor Yellow
    Write-Host "The driver will load if testsigning is enabled:" -ForegroundColor Yellow
    Write-Host "  bcdedit /set testsigning on" -ForegroundColor White
}
else {
    Write-Host "Signature verified successfully." -ForegroundColor DarkGreen
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " Driver signed successfully" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  File:        $DriverPath"
Write-Host "  Certificate: $CertName"
Write-Host "  Thumbprint:  $($cert.Thumbprint)"
Write-Host ""
Write-Host "Make sure testsigning is enabled:" -ForegroundColor Yellow
Write-Host "  bcdedit /set testsigning on" -ForegroundColor White
Write-Host ""
