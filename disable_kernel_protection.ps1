#Requires -RunAsAdministrator
Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

function Write-Step { param($msg) Write-Host "`n[*] $msg" -ForegroundColor Cyan }
function Write-OK   { param($msg) Write-Host "    [+] $msg" -ForegroundColor Green }
function Write-Fail { param($msg) Write-Host "    [-] $msg" -ForegroundColor Red }
function Write-Warn { param($msg) Write-Host "    [!] $msg" -ForegroundColor Yellow }

Write-Host "`n=== Kernel Protection Disabler - VM Dev Tool ===`n" -ForegroundColor Magenta

# 0. Check admin
Write-Step "Checking privileges"
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Fail "No admin rights! Run as Administrator."
    exit 1
}
Write-OK "Running as Administrator"
$debugPriv = whoami /priv | Select-String "SeDebugPrivilege"
if ($debugPriv) { Write-OK "SeDebugPrivilege present" }
else { Write-Warn "SeDebugPrivilege missing - try: PsExec64.exe -i -s powershell.exe" }

# 1. Tamper Protection
Write-Step "Disabling Tamper Protection"
try {
    $tpPath = "HKLM:\SOFTWARE\Microsoft\Windows Defender\Features"
    Set-ItemProperty -Path $tpPath -Name "TamperProtection" -Value 4 -Force -ErrorAction Stop
    Write-OK "TamperProtection disabled via registry"
} catch {
    Write-Warn "Registry failed, trying reg.exe..."
    & reg add "HKLM\SOFTWARE\Microsoft\Windows Defender\Features" /v TamperProtection /t REG_DWORD /d 4 /f 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-OK "TamperProtection disabled via reg.exe" }
    else { Write-Fail "Need SYSTEM session. Run: PsExec64.exe -i -s powershell.exe" }
}

# 2. Windows Defender via registry (more reliable than Set-MpPreference)
Write-Step "Disabling Windows Defender"
$defPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender"
if (-not (Test-Path $defPath)) { New-Item -Path $defPath -Force | Out-Null }

$defKeys = @{
    "DisableAntiSpyware"     = 1
    "DisableAntiVirus"       = 1
    "DisableRealtimeMonitoring" = 1
}
foreach ($k in $defKeys.GetEnumerator()) {
    try {
        Set-ItemProperty -Path $defPath -Name $k.Key -Value $k.Value -Force -ErrorAction Stop
        Write-OK "$($k.Key) = $($k.Value)"
    } catch {
        Write-Fail "$($k.Key): $($_.Exception.Message)"
    }
}

$rtPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection"
if (-not (Test-Path $rtPath)) { New-Item -Path $rtPath -Force | Out-Null }
Set-ItemProperty -Path $rtPath -Name "DisableRealtimeMonitoring" -Value 1 -Force -ErrorAction SilentlyContinue
Write-OK "Real-Time Protection disabled via policy"

# 3. Create a separate BCD boot entry with all protections disabled
Write-Step "Creating debug boot entry"

# Check if debug entry already exists
$existingDebug = & bcdedit /enum | Select-String "KernelFlirt Debug"
if ($existingDebug) {
    Write-Warn "Debug boot entry already exists, updating current entry only"
    $debugGuid = "{current}"
} else {
    # Copy current entry
    $copyOutput = & bcdedit /copy "{current}" /d "KernelFlirt Debug" 2>&1
    if ($copyOutput -match '\{([0-9a-f-]+)\}') {
        $debugGuid = "{$($Matches[1])}"
        Write-OK "Created boot entry: $debugGuid"

        # Set as default and reduce boot menu timeout
        & bcdedit /default $debugGuid 2>&1 | Out-Null
        Write-OK "Set as default boot entry"
        & bcdedit /timeout 5 2>&1 | Out-Null
        Write-OK "Boot menu timeout: 5 seconds"
    } else {
        Write-Fail "Failed to copy boot entry: $copyOutput"
        Write-Warn "Falling back to modifying current entry"
        $debugGuid = "{current}"
    }
}

Write-Step "Configuring BCD ($debugGuid)"
$bcdCmds = @(
    @("testsigning",          "on",  "Test Signing ON - unsigned drivers allowed"),
    @("nointegritychecks",    "on",  "Integrity Checks OFF"),
    @("hypervisorlaunchtype", "off", "Hypervisor OFF - disables VBS/HVCI"),
    @("vsmlaunchtype",        "off", "VSM OFF"),
    @("recoveryenabled",      "no",  "Recovery disabled"),
    @("debug",                "on",  "Kernel Debug ON")
)
foreach ($c in $bcdCmds) {
    $out = & bcdedit /set $debugGuid $c[0] $c[1] 2>&1
    if ($LASTEXITCODE -eq 0) { Write-OK $c[2] }
    else { Write-Fail "$($c[2]) => $out" }
}

# Remove per-entry debugtype override — this is critical!
# BCD entries copied from existing ones inherit "debugtype Local" which
# overrides the global {dbgsettings}. Must delete it so Serial takes effect.
Write-Step "Fixing debugtype (removing Local override)"
& bcdedit /deletevalue $debugGuid debugtype 2>&1 | Out-Null
Write-OK "Removed debugtype from $debugGuid"

# Also clean all other boot entries
$allEntries = & bcdedit /enum 2>&1
$guids = [regex]::Matches($allEntries, '\{[0-9a-f-]+\}') | ForEach-Object { $_.Value } | Sort-Object -Unique
foreach ($g in $guids) {
    & bcdedit /deletevalue $g debugtype 2>&1 | Out-Null
}
Write-OK "Cleaned debugtype from all boot entries"

# Detect which COM port the serial device is on
Write-Step "Detecting COM port number"
$comPort = 1
$serialPorts = Get-WmiObject Win32_SerialPort -ErrorAction SilentlyContinue
if ($serialPorts) {
    foreach ($sp in $serialPorts) {
        Write-OK "Found: $($sp.DeviceID) - $($sp.Name)"
        if ($sp.DeviceID -match 'COM(\d+)') {
            $comPort = [int]$Matches[1]
        }
    }
    Write-OK "Using COM$comPort"
} else {
    # Fallback: check registry for COM ports
    $portEnum = Get-ChildItem "HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM" -ErrorAction SilentlyContinue
    if ($portEnum) {
        $portNames = Get-ItemProperty "HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM" -ErrorAction SilentlyContinue
        if ($portNames) {
            $portNames.PSObject.Properties | Where-Object { $_.Name -notlike "PS*" } | ForEach-Object {
                Write-OK "Found: $($_.Name) = $($_.Value)"
                if ($_.Value -match 'COM(\d+)') {
                    $comPort = [int]$Matches[1]
                }
            }
        }
    }
    if ($comPort -eq 1) {
        Write-Warn "Could not auto-detect COM port, defaulting to COM1"
        Write-Warn "If connection fails, try: bcdedit /set {dbgsettings} debugport 2"
    }
}

# Set global debug settings via {dbgsettings} object
& bcdedit /set "{dbgsettings}" debugtype Serial 2>&1 | Out-Null
& bcdedit /set "{dbgsettings}" debugport $comPort 2>&1 | Out-Null
& bcdedit /set "{dbgsettings}" baudrate 115200 2>&1 | Out-Null
& bcdedit /dbgsettings serial debugport:$comPort baudrate:115200 2>&1 | Out-Null
Write-OK "Debug: serial COM$comPort 115200 baud (global)"

# Verify dbgsettings
$dbgOut = & bcdedit /dbgsettings 2>&1
$dtLine = $dbgOut | Select-String "debugtype"
if ($dtLine -and $dtLine.Line -match "Serial") {
    Write-OK "Verified: $($dtLine.Line.Trim())"
} else {
    Write-Fail "debugtype is NOT Serial! Output: $dbgOut"
}

# 4. Device Guard / HVCI / VBS
Write-Step "Disabling Device Guard / HVCI / VBS"
$dgEntries = @(
    @("HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard",
      "EnableVirtualizationBasedSecurity", 0),
    @("HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard",
      "RequirePlatformSecurityFeatures", 0),
    @("HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
      "Enabled", 0),
    @("HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
      "WasEnabledBy", 0),
    @("HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\KernelShadowStacks",
      "Enabled", 0)
)
foreach ($e in $dgEntries) {
    try {
        if (-not (Test-Path $e[0])) { New-Item -Path $e[0] -Force | Out-Null }
        Set-ItemProperty -Path $e[0] -Name $e[1] -Value $e[2] -Force -ErrorAction Stop
        Write-OK "$($e[1]) = $($e[2])"
    } catch {
        Write-Fail "$($e[1]): $($_.Exception.Message)"
    }
}

# 5. DSE / Code Integrity
Write-Step "Disabling DSE / Code Integrity"
$ciEntries = @(
    @("HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable", 0),
    @("HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config", "Options", 0)
)
foreach ($e in $ciEntries) {
    try {
        if (-not (Test-Path $e[0])) { New-Item -Path $e[0] -Force | Out-Null }
        Set-ItemProperty -Path $e[0] -Name $e[1] -Value $e[2] -Force -ErrorAction Stop
        Write-OK "$($e[1]) = $($e[2])"
    } catch {
        Write-Fail "$($e[1]): $($_.Exception.Message)"
    }
}

# 6. Verify BCD
Write-Step "BCD verification"
$bcdOut = & bcdedit /enum 2>&1
$checkList = @("testsigning", "nointegritychecks", "hypervisorlaunchtype", "debug", "vsmlaunchtype")
foreach ($k in $checkList) {
    $line = $bcdOut | Select-String $k
    if ($line) { Write-OK $line.Line.Trim() }
    else { Write-Warn "$k - not found in BCD" }
}

# Verify debugtype is Serial
$dbgCheck = & bcdedit /dbgsettings 2>&1
Write-Step "Debug settings verification"
$dbgCheck | ForEach-Object { Write-OK $_.ToString().Trim() }

Write-Host "`n================================================" -ForegroundColor Magenta
Write-Host "  Host:   KernelFlirt -> 10.100.102.4" -ForegroundColor Yellow
Write-Host "================================================`n" -ForegroundColor Magenta

$restart = Read-Host "Reboot now? (y/n)"
if ($restart -eq "y") { Restart-Computer -Force }
