#!/usr/bin/env pwsh
# smoke-test.ps1 — Clean-machine end-to-end validation of a SageFs install.
# Exits 0 if all checks pass, 1 if any fail.

param(
  [string]$SampleProject = "samples\from-csharp\SageFs.Samples.FromCSharp\SageFs.Samples.FromCSharp.fsproj",
  [int]$DaemonTimeoutSeconds = 15,
  [int]$Port = 37749
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$baseUrl = "http://localhost:$Port"
$daemonProcess = $null

# Track pass/fail per step
$results = [ordered]@{}

function Pass([string]$step, [string]$msg) {
  Write-Host "  ✓ $msg" -ForegroundColor Green
  $results[$step] = "PASS"
}

function Fail([string]$step, [string]$msg) {
  Write-Host "  ✗ $msg" -ForegroundColor Red
  $results[$step] = "FAIL"
}

function Section([string]$title) {
  Write-Host ""
  Write-Host "── $title" -ForegroundColor Cyan
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 1 — Prerequisites
# ─────────────────────────────────────────────────────────────────────────────
Section "1. Prerequisites"

# dotnet in PATH and .NET 10+
$dotnetOk = $false
try {
  $dotnetVersion = & dotnet --version 2>&1
  $major = [int]($dotnetVersion -split '\.')[0]
  if ($major -ge 10) {
    Pass "dotnet" ".NET $dotnetVersion found"
    $dotnetOk = $true
  } else {
    Fail "dotnet" ".NET $dotnetVersion found — need 10+"
  }
} catch {
  Fail "dotnet" "'dotnet' not found in PATH"
}

# sagefs in PATH
$sagefsOk = $false
try {
  $sagefsVersion = & sagefs --version 2>&1
  Pass "sagefs-path" "sagefs $sagefsVersion found in PATH"
  $sagefsOk = $true
} catch {
  Fail "sagefs-path" "'sagefs' not found in PATH"
  Write-Host ""
  Write-Host "ERROR: 'sagefs' not found in PATH." -ForegroundColor Red
  Write-Host "Install with: dotnet tool install --global SageFs" -ForegroundColor Yellow
  Write-Host "Then restart your terminal to update PATH." -ForegroundColor Yellow
  Write-Host ""
  # Print summary and exit early
  Write-Host "── Summary" -ForegroundColor Cyan
  foreach ($k in $results.Keys) {
    $icon = if ($results[$k] -eq "PASS") { "✓" } else { "✗" }
    $color = if ($results[$k] -eq "PASS") { "Green" } else { "Red" }
    Write-Host "  $icon $k" -ForegroundColor $color
  }
  exit 1
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 2 — Daemon starts
# ─────────────────────────────────────────────────────────────────────────────
Section "2. Daemon startup"

$samplePath = Join-Path $PSScriptRoot ".." $SampleProject
$samplePath = [System.IO.Path]::GetFullPath($samplePath)

if (-not (Test-Path $samplePath)) {
  Fail "daemon-start" "Sample project not found: $samplePath"
} else {
  Write-Host "  Starting daemon with: $SampleProject" -ForegroundColor DarkGray
  $daemonProcess = Start-Process -FilePath "sagefs" `
    -ArgumentList "--proj", $samplePath `
    -PassThru

  $reachable = $false
  $elapsed = 0
  for ($i = 0; $i -lt $DaemonTimeoutSeconds; $i++) {
    Start-Sleep -Seconds 1
    $elapsed++
    try {
      $resp = Invoke-WebRequest -Uri "$baseUrl/health" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
      if ($resp.StatusCode -eq 200) {
        $reachable = $true
        break
      }
    } catch {
      # not ready yet
    }
  }

  if ($reachable) {
    Pass "daemon-start" "Daemon started in ${elapsed}s"
  } else {
    Fail "daemon-start" "Daemon not reachable after ${DaemonTimeoutSeconds}s"
    if ($daemonProcess -and -not $daemonProcess.HasExited) {
      Stop-Process -Id $daemonProcess.Id -Force -ErrorAction SilentlyContinue
    }
    # Print summary and exit early
    Write-Host ""
    Write-Host "── Summary" -ForegroundColor Cyan
    foreach ($k in $results.Keys) {
      $icon = if ($results[$k] -eq "PASS") { "✓" } else { "✗" }
      $color = if ($results[$k] -eq "PASS") { "Green" } else { "Red" }
      Write-Host "  $icon $k" -ForegroundColor $color
    }
    exit 1
  }
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 3 — MCP tools available
# ─────────────────────────────────────────────────────────────────────────────
Section "3. MCP tools"

try {
  $body = '{"jsonrpc":"2.0","method":"tools/list","id":1,"params":{}}'
  $resp = Invoke-RestMethod -Method Post -Uri "$baseUrl/mcp" `
    -Body $body `
    -ContentType "application/json" `
    -TimeoutSec 10
  $toolNames = $resp.result.tools | ForEach-Object { $_.name }
  $hasRunTests = $toolNames -contains "run_tests"
  $hasLiveStatus = $toolNames -contains "get_live_test_status"
  if ($hasRunTests -and $hasLiveStatus) {
    Pass "mcp-tools" "MCP tools available ($($toolNames.Count) tools, run_tests + get_live_test_status confirmed)"
  } else {
    $missing = @()
    if (-not $hasRunTests) { $missing += "run_tests" }
    if (-not $hasLiveStatus) { $missing += "get_live_test_status" }
    Fail "mcp-tools" "MCP tools missing: $($missing -join ', ')"
  }
} catch {
  Fail "mcp-tools" "MCP tools/list request failed: $_"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 4 — Completions
# ─────────────────────────────────────────────────────────────────────────────
Section "4. Completions"

try {
  $completionBody = '{"code":"let x = List.","cursorPosition":14}'
  $resp = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/completions" `
    -Body $completionBody `
    -ContentType "application/json" `
    -TimeoutSec 15
  # Response is an array of completion items
  $count = if ($resp -is [array]) { $resp.Count } else { ($resp | ConvertFrom-Json).Count }
  if ($count -gt 0) {
    Pass "completions" "Completions returned $count items"
  } else {
    Fail "completions" "Completions returned empty array"
  }
} catch {
  Fail "completions" "Completions request failed: $_"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 5 — Live test status + run_tests
# ─────────────────────────────────────────────────────────────────────────────
Section "5. Tests"

Write-Host "  Waiting 5s for test discovery..." -ForegroundColor DarkGray
Start-Sleep -Seconds 5

try {
  $status = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/live-testing/status" -TimeoutSec 10
  $total = if ($null -ne $status.summary) { $status.summary.total } else { "?" }
  Write-Host "  Tests discovered: $total" -ForegroundColor DarkGray

  # Run tests
  $runBody = '{"timeout_seconds":30}'
  $runResp = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/live-testing/run" `
    -Body $runBody `
    -ContentType "application/json" `
    -TimeoutSec 40
  if ($runResp.success -eq $true) {
    Pass "run-tests" "run_tests returned results (total discovered: $total)"
  } else {
    Fail "run-tests" "run_tests response did not indicate success"
  }
} catch {
  Fail "run-tests" "run_tests request failed: $_"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 6 — Cleanup
# ─────────────────────────────────────────────────────────────────────────────
Section "6. Cleanup"

if ($daemonProcess -and -not $daemonProcess.HasExited) {
  Stop-Process -Id $daemonProcess.Id -Force -ErrorAction SilentlyContinue
  Write-Host "  Daemon process stopped (PID $($daemonProcess.Id))" -ForegroundColor DarkGray
}

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "── Summary" -ForegroundColor Cyan
$anyFail = $false
foreach ($k in $results.Keys) {
  $icon  = if ($results[$k] -eq "PASS") { "✓" } else { "✗" }
  $color = if ($results[$k] -eq "PASS") { "Green" } else { "Red" }
  Write-Host "  $icon $k" -ForegroundColor $color
  if ($results[$k] -ne "PASS") { $anyFail = $true }
}
Write-Host ""
if ($anyFail) {
  Write-Host "RESULT: FAIL" -ForegroundColor Red
  exit 1
} else {
  Write-Host "RESULT: PASS" -ForegroundColor Green
  exit 0
}
