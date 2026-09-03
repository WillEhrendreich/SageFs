#!/usr/bin/env pwsh
# e2e-dashboard.ps1 - Real-browser dashboard journeys against a real daemon.
#
# Starts a freshly installed SageFs daemon (SAGEFS_DATA_DIR isolated to a temp
# dir, non-default port, --no-resume), creates a WebLive session on the Falco +
# Datastar sample project (so hot-reload is exercisable), waits for Ready, then
# runs the Playwright dashboard specs against the dashboard port.
#
# ASCII only (no checkmarks/box-drawing): Windows PowerShell 5.1 reads BOM-less
# .ps1 files as ANSI, which mangles non-ASCII bytes inside string literals and
# can break parsing.
#
# Exits 0 if all browser specs pass, 1 if any fail.

param(
  [int]$Port = 37951,
  [int]$DaemonTimeoutSeconds = 15,
  [int]$SessionWarmupSeconds = 120,
  [string]$SampleProject = "samples\demos\SageFs.Samples.WebappDatastar\SageFs.Samples.WebappDatastar.fsproj",
  [string]$DiagnosticsDir = "e2e-diagnostics"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$baseUrl = "http://localhost:$Port"
$dashboardBaseUrl = "http://localhost:$($Port + 1)"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$samplePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $SampleProject))
$sampleProjectDir = Split-Path -Parent $samplePath
$diagnosticsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $DiagnosticsDir))
$dataDir = Join-Path $diagnosticsRoot "sagefs-data"
$daemonStdoutPath = Join-Path $diagnosticsRoot "daemon-stdout.log"
$daemonStderrPath = Join-Path $diagnosticsRoot "daemon-stderr.log"
$playwrightReport = Join-Path $diagnosticsRoot "playwright-report"
$daemonProcess = $null
$sessionReady = $false
$playwrightExit = 1

function Section([string]$title) {
  Write-Host ""
  Write-Host "-- $title" -ForegroundColor Cyan
}

# -- 1. Start the daemon (isolated data dir, non-default port, no resume) -----
Section "1. Start daemon"
if (-not (Test-Path $diagnosticsRoot)) { New-Item -ItemType Directory -Force $diagnosticsRoot | Out-Null }
Remove-Item -Path $daemonStdoutPath, $daemonStderrPath -ErrorAction SilentlyContinue
$env:SAGEFS_DATA_DIR = $dataDir
$env:SAGEFS_HOT_RELOAD = "true"

$daemonProcess = Start-Process -FilePath "sagefs" `
  -ArgumentList "--mcp-port", $Port, "--no-resume" `
  -WorkingDirectory $repoRoot `
  -RedirectStandardOutput $daemonStdoutPath `
  -RedirectStandardError $daemonStderrPath `
  -PassThru

$reachable = $false
for ($i = 0; $i -lt $DaemonTimeoutSeconds; $i++) {
  Start-Sleep -Seconds 1
  try {
    $resp = Invoke-RestMethod -Method Get -Uri "$baseUrl/health" -TimeoutSec 2 -ErrorAction Stop
    # A bare daemon with no sessions reports healthy:false — any successful
    # HTTP response means the daemon is up and serving.
    $reachable = $true
    break
  } catch {
    # not up yet - keep polling
  }
}
if (-not $reachable) {
  Write-Host "  X Daemon not reachable after ${DaemonTimeoutSeconds}s" -ForegroundColor Red
  if (Test-Path $daemonStderrPath) { Get-Content $daemonStderrPath -Tail 40 }
  exit 1
}
Write-Host "  OK Daemon healthy (data dir $dataDir)" -ForegroundColor Green

# -- 2. Create a WebLive session on the sample project; wait Ready ------------
Section "2. Session warmup"
try {
  $createBody = @{
    projects = @($samplePath)
    workingDirectory = $sampleProjectDir
    workflow = "WebLive"
  } | ConvertTo-Json
  $createResp = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/sessions/create" `
    -Body $createBody -ContentType "application/json" -TimeoutSec 15
  if ($createResp.success -ne $true) {
    Write-Host "  X Session creation failed: $($createResp | ConvertTo-Json -Compress)" -ForegroundColor Red
  } else {
    Write-Host "  Session created; waiting for Ready (up to ${SessionWarmupSeconds}s)..."
    for ($i = 0; $i -lt $SessionWarmupSeconds; $i++) {
      Start-Sleep -Seconds 1
      try {
        $sessions = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/sessions" -TimeoutSec 5
        $readySession = $sessions.sessions | Where-Object { $_.status -eq "Ready" } | Select-Object -First 1
        if ($readySession) {
          $sessionReady = $true
          Write-Host "  OK Session $($readySession.id) Ready after $($i + 1)s" -ForegroundColor Green
          break
        }
        $all = @($sessions.sessions)
        if ($all.Count -gt 0 -and ($all | Where-Object { $_.status -ne "Faulted" }).Count -eq 0) {
          Write-Host "  X All sessions Faulted" -ForegroundColor Red
          break
        }
      } catch {
        # poll error - keep waiting
      }
    }
  }
} catch {
  Write-Host "  X Session create failed: $_" -ForegroundColor Red
}
if (-not $sessionReady) {
  Write-Host "  X Session never reached Ready" -ForegroundColor Red
  if (Test-Path $daemonStderrPath) { Get-Content $daemonStderrPath -Tail 40 }
  if ($daemonProcess -and -not $daemonProcess.HasExited) { Stop-Process -Id $daemonProcess.Id -Force }
  exit 1
}

# -- 3. Run the Playwright dashboard specs ------------------------------------
Section "3. Playwright dashboard specs"
$env:PLAYWRIGHT_BASE_URL = $dashboardBaseUrl
Push-Location $repoRoot
try {
  npx playwright test --reporter=list
  $playwrightExit = $LASTEXITCODE
} finally {
  Pop-Location
}

# -- 4. Diagnostics + cleanup --------------------------------------------------
if (Test-Path "$repoRoot\test-results") {
  Copy-Item -Path "$repoRoot\test-results" -Destination $playwrightReport -Recurse -Force -ErrorAction SilentlyContinue
}
if ($daemonProcess -and -not $daemonProcess.HasExited) {
  Stop-Process -Id $daemonProcess.Id -Force -ErrorAction SilentlyContinue
}
Remove-Item Env:SAGEFS_DATA_DIR -ErrorAction SilentlyContinue
Remove-Item Env:SAGEFS_HOT_RELOAD -ErrorAction SilentlyContinue

if ($playwrightExit -ne 0) {
  Write-Host "  X Playwright specs failed (exit $playwrightExit)" -ForegroundColor Red
  if (Test-Path $daemonStderrPath) { Get-Content $daemonStderrPath -Tail 60 }
  exit 1
}
Write-Host "  OK All dashboard E2E specs passed" -ForegroundColor Green
exit 0
