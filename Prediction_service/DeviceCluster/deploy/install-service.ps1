<#
    Registers the DeviceCluster ML prediction service as a Windows service,
    so it starts at boot and survives a reboot instead of living in whatever
    PowerShell window someone left open.

    RUN THIS ON THE SERVER (SSSBPD01), in PowerShell opened with
    "Run as Administrator".

        .\install-service.ps1

    It checks everything first and stops with a clear message rather than
    half-configuring the service. Nothing is changed until every check passes.

    It deliberately does NOT set the logon account, because that needs a
    password and this file should never hold one. The script prints the
    remaining manual step at the end.

    To undo:  nssm stop DeviceClusterML
              nssm remove DeviceClusterML confirm
#>

[CmdletBinding()]
param(
    [string] $ServiceName = 'DeviceClusterML',
    [string] $Nssm        = 'C:\Tools\nssm.exe',
    [string] $Python      = 'C:\Users\RNDAdmin\AppData\Local\Programs\Python\Python313\python.exe',
    [string] $AppDir      = 'C:\Users\RNDAdmin\source\repos\DeviceIdentifier\Prediction_service\DeviceCluster',
    [string] $LogDir      = 'C:\Users\RNDAdmin\logs',
    [int]    $Port        = 8000
)

$ErrorActionPreference = 'Stop'

function Fail([string] $message) {
    Write-Host ''
    Write-Host "  STOPPED: $message" -ForegroundColor Red
    Write-Host '  Nothing was changed.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

function Ok([string] $message) {
    Write-Host "  OK    $message" -ForegroundColor Green
}

Write-Host ''
Write-Host '  Checking prerequisites' -ForegroundColor Cyan
Write-Host '  ----------------------'

# --- 1. Administrator ------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'Not running as Administrator. Close this window, right-click PowerShell, choose "Run as administrator", and try again.'
}
Ok 'running as Administrator'

# --- 2. The tools and paths exist -----------------------------------------
if (-not (Test-Path $Nssm))   { Fail "nssm.exe not found at $Nssm. Download nssm-2.24.zip from nssm.cc, unzip it, and copy win64\nssm.exe there." }
Ok "nssm found          $Nssm"

if (-not (Test-Path $Python)) { Fail "python.exe not found at $Python" }
Ok "python found        $Python"

if (-not (Test-Path $AppDir)) { Fail "Service folder not found at $AppDir" }
Ok "service folder      $AppDir"

if (-not (Test-Path (Join-Path $AppDir 'service.py'))) {
    Fail "service.py is not in $AppDir. The service starts from this folder, and the models are found relative to it."
}
Ok 'service.py present'

# --- 3. The service does not already exist --------------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Fail "A service named '$ServiceName' already exists (status: $($existing.Status)). Remove it first with:  $Nssm remove $ServiceName confirm"
}
Ok "no existing '$ServiceName' service"

# --- 4. The port is free ---------------------------------------------------
$inUse = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($inUse) {
    $procId = ($inUse | Select-Object -First 1).OwningProcess
    $proc   = Get-Process -Id $procId -ErrorAction SilentlyContinue
    Fail "Port $Port is already in use by $($proc.ProcessName) (PID $procId). That is almost certainly the service running by hand - close that PowerShell window first, then re-run this script."
}
Ok "port $Port is free"

# --- 5. Log folder ---------------------------------------------------------
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }
Ok "log folder          $LogDir"

# --- Install ---------------------------------------------------------------
Write-Host ''
Write-Host '  Installing the service' -ForegroundColor Cyan
Write-Host '  ----------------------'

$arguments = "-m uvicorn service:app --host 0.0.0.0 --port $Port"

& $Nssm install $ServiceName $Python $arguments        | Out-Null
& $Nssm set $ServiceName AppDirectory   $AppDir        | Out-Null
& $Nssm set $ServiceName DisplayName    'DeviceCluster ML Service' | Out-Null
& $Nssm set $ServiceName Description    "FastAPI prediction service for DeviceCluster, listening on port $Port" | Out-Null
& $Nssm set $ServiceName Start          SERVICE_AUTO_START        | Out-Null

# Logging matters more than usual here: as a service there is no console,
# so without these a startup failure leaves nothing to read.
& $Nssm set $ServiceName AppStdout      (Join-Path $LogDir 'ml-service.log')       | Out-Null
& $Nssm set $ServiceName AppStderr      (Join-Path $LogDir 'ml-service-error.log') | Out-Null
& $Nssm set $ServiceName AppRotateFiles 1          | Out-Null
& $Nssm set $ServiceName AppRotateBytes 10485760   | Out-Null

Ok 'service registered, set to start automatically at boot'

Write-Host ''
Write-Host '  ONE MANUAL STEP REMAINS' -ForegroundColor Yellow
Write-Host '  -----------------------'
Write-Host "  Python is installed per-user, under $($Python.Split('\')[0..2] -join '\')\..."
Write-Host '  The default LocalSystem account cannot reach it, so the service must'
Write-Host '  run as RNDAdmin or it will fail to start.'
Write-Host ''
Write-Host '    1. Press Win+R, type  services.msc  and press Enter'
Write-Host "    2. Find '$ServiceName', right-click, Properties"
Write-Host '    3. Log On tab -> This account -> .\RNDAdmin -> enter the password'
Write-Host '    4. Apply, then close'
Write-Host ''
Write-Host '  Set it there rather than on the command line, so the password is not'
Write-Host '  left in your PowerShell history.'
Write-Host ''
Write-Host '  Then start and check it:' -ForegroundColor Cyan
Write-Host "    $Nssm start $ServiceName"
Write-Host "    $Nssm status $ServiceName            # expect SERVICE_RUNNING"
Write-Host "    curl http://localhost:$Port/ml-device-identifier"
Write-Host ''
Write-Host "  If it will not start, read:  $LogDir\ml-service-error.log"
Write-Host ''
