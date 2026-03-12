#!/usr/bin/env pwsh
# smoke-test.ps1 — Clean-machine end-to-end validation of a SageFs install.
# Exits 0 if all checks pass, 1 if any fail.

param(
  [string]$SampleProject = "samples\from-csharp\SageFs.Samples.FromCSharp\SageFs.Samples.FromCSharp.fsproj",
  [int]$DaemonTimeoutSeconds = 15,
  [int]$SessionWarmupSeconds = 45,
  [int]$Port = 37749,
  [string]$DiagnosticsDir = "smoke-diagnostics"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$baseUrl = "http://localhost:$Port"
$dashboardBaseUrl = "http://localhost:$($Port + 1)"
$daemonProcess = $null
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$samplePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $SampleProject))
$diagnosticsRoot =
  if ([System.IO.Path]::IsPathRooted($DiagnosticsDir)) {
    [System.IO.Path]::GetFullPath($DiagnosticsDir)
  } else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $DiagnosticsDir))
  }
$diagnosticsCaptured = $false

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

function Warn([string]$step, [string]$msg) {
  Write-Host "  ⚠ $msg" -ForegroundColor Yellow
  $results[$step] = "WARN"
}

function Section([string]$title) {
  Write-Host ""
  Write-Host "── $title" -ForegroundColor Cyan
}

function Ensure-DiagnosticsDirectory() {
  if (-not (Test-Path $diagnosticsRoot)) {
    New-Item -ItemType Directory -Force $diagnosticsRoot | Out-Null
  }
}

function Write-DiagnosticText([string]$fileName, [string]$content) {
  Ensure-DiagnosticsDirectory
  Set-Content -Path (Join-Path $diagnosticsRoot $fileName) -Value $content -Encoding UTF8
}

function Write-DiagnosticJson([string]$fileName, $value) {
  $json =
    if ($null -eq $value) {
      "null"
    } else {
      $value | ConvertTo-Json -Depth 12
    }
  Write-DiagnosticText $fileName $json
}

function Capture-SmokeDiagnostics([string]$reason) {
  if ($diagnosticsCaptured) {
    return
  }

  $script:diagnosticsCaptured = $true
  Ensure-DiagnosticsDirectory
  Write-Host "  Saving diagnostics to $diagnosticsRoot" -ForegroundColor DarkGray

  $daemonInfo =
    if ($daemonProcess) {
      try {
        $proc = Get-Process -Id $daemonProcess.Id -ErrorAction Stop
        [PSCustomObject]@{
          id = $proc.Id
          name = $proc.ProcessName
          startTime = $proc.StartTime
          hasExited = $false
        }
      } catch {
        [PSCustomObject]@{
          id = $daemonProcess.Id
          hasExited = $true
          note = $_.Exception.Message
        }
      }
    } else {
      $null
    }

  $resultsSnapshot =
    foreach ($key in $results.Keys) {
      [PSCustomObject]@{
        step = $key
        result = $results[$key]
      }
    }

  Write-DiagnosticJson "context.json" ([PSCustomObject]@{
    reason = $reason
    timestampUtc = (Get-Date).ToUniversalTime()
    baseUrl = $baseUrl
    dashboardBaseUrl = $dashboardBaseUrl
    repoRoot = $repoRoot
    samplePath = $samplePath
    daemonProcess = $daemonInfo
    results = $resultsSnapshot
  })

  $endpoints = @(
    @{ File = "health.txt"; Uri = "$baseUrl/health"; Kind = "text" }
    @{ File = "version.json"; Uri = "$baseUrl/version"; Kind = "json" }
    @{ File = "daemon-info.json"; Uri = "$dashboardBaseUrl/api/daemon-info"; Kind = "json" }
    @{ File = "sessions.json"; Uri = "$baseUrl/api/sessions"; Kind = "json" }
    @{ File = "live-testing-status.json"; Uri = "$baseUrl/api/live-testing/status"; Kind = "json" }
    @{ File = "threadpool.txt"; Uri = "$baseUrl/diag/threadpool"; Kind = "text" }
  )

  foreach ($endpoint in $endpoints) {
    try {
      switch ($endpoint.Kind) {
      "json" {
        $payload = Invoke-RestMethod -Method Get -Uri $endpoint.Uri -TimeoutSec 5 -ErrorAction Stop
        Write-DiagnosticJson $endpoint.File $payload
      }
      default {
        $payload = Invoke-WebRequest -Method Get -Uri $endpoint.Uri -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
        Write-DiagnosticText $endpoint.File $payload.Content
      }
      }
    } catch {
      $errorFile = "{0}-error.txt" -f [System.IO.Path]::GetFileNameWithoutExtension($endpoint.File)
      Write-DiagnosticText $errorFile ($_ | Out-String)
    }
  }
}

function Show-Summary() {
  Write-Host ""
  Write-Host "── Summary" -ForegroundColor Cyan
  $hasFailures = $false
  foreach ($k in $results.Keys) {
    $result = $results[$k]
    $icon  = switch ($result) { "PASS" { "✓" } "WARN" { "⚠" } default { "✗" } }
    $color = switch ($result) { "PASS" { "Green" } "WARN" { "Yellow" } default { "Red" } }
    Write-Host "  $icon $k" -ForegroundColor $color
    if ($result -eq "FAIL") {
      $hasFailures = $true
    }
  }

  Write-Host ""
  return $hasFailures
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
  Capture-SmokeDiagnostics "sagefs executable was not available on PATH"
  $null = Show-Summary
  exit 1
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 2 — Daemon starts
# ─────────────────────────────────────────────────────────────────────────────
Section "2. Daemon startup"

if (-not (Test-Path $samplePath)) {
  Fail "daemon-start" "Sample project not found: $samplePath"
  Capture-SmokeDiagnostics "Sample project path was invalid"
  $null = Show-Summary
  exit 1
} else {
  Write-Host "  Starting daemon with: $SampleProject" -ForegroundColor DarkGray
  $daemonProcess = Start-Process -FilePath "sagefs" `
    -ArgumentList "--mcp-port", $Port, "--no-resume", "--proj", $samplePath `
    -WorkingDirectory $repoRoot `
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
    Capture-SmokeDiagnostics "Daemon startup timed out"
    if ($daemonProcess -and -not $daemonProcess.HasExited) {
      Stop-Process -Id $daemonProcess.Id -Force -ErrorAction SilentlyContinue
    }
    $null = Show-Summary
    exit 1
  }
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 3 — Daemon version / API health
# ─────────────────────────────────────────────────────────────────────────────
Section "3. API version"

try {
  $resp = Invoke-RestMethod -Method Get -Uri "$baseUrl/version" -TimeoutSec 10
  if ($resp.server -eq "sagefs" -and $null -ne $resp.version) {
    Pass "api-version" "API version $($resp.version) (MCP: $($resp.mcp), SSE: $($resp.sse))"
  } else {
    Fail "api-version" "Unexpected /version response: $($resp | ConvertTo-Json -Compress)"
  }
} catch {
  Fail "api-version" "/version request failed: $_"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 4 — Session warmup + Completions
# ─────────────────────────────────────────────────────────────────────────────
Section "4. Session warmup + Completions"

$sessionReady = $false
$sessionWorkDir = $repoRoot

# The daemon was started with --proj, so it auto-creates a session on startup.
# Poll the existing sessions list instead of creating a redundant second session.
Write-Host "  Waiting for auto-created daemon session to reach Ready (up to ${SessionWarmupSeconds}s)..." -ForegroundColor DarkGray

try {
  for ($i = 0; $i -lt $SessionWarmupSeconds; $i++) {
    Start-Sleep -Seconds 1
    try {
      $sessions = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/sessions" -TimeoutSec 5
      $readySession = $sessions.sessions | Where-Object { $_.status -eq "Ready" } | Select-Object -First 1
      if ($readySession) {
        $sessionReady = $true
        Write-Host "  Session $($readySession.id) ready after ${i}s" -ForegroundColor DarkGray
        break
      }
    } catch { }
  }

  if (-not $sessionReady) {
    Fail "completions" "No session reached Ready within ${SessionWarmupSeconds}s"
  } else {
    $completionBody = [PSCustomObject]@{
      code             = "let x = List."
      cursorPosition   = 14
      workingDirectory = $sessionWorkDir
    } | ConvertTo-Json

    $resp = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/completions" `
      -Body $completionBody `
      -ContentType "application/json" `
      -TimeoutSec 15
    # Response is either a JSON array or a plain-text/error string
    if ($resp -is [array] -and $resp.Count -gt 0) {
      Pass "completions" "Completions returned $($resp.Count) items"
    } elseif ($resp -is [string] -and $resp -notmatch "^Error:") {
      Pass "completions" "Completions returned results"
    } else {
      Fail "completions" "Completions returned unexpected: $resp"
    }
  }
} catch {
  Fail "completions" "Session/completions request failed: $_"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 5 — Live test status + run_tests
# ─────────────────────────────────────────────────────────────────────────────
Section "5. Tests"

Write-Host "  Enabling live testing..." -ForegroundColor DarkGray
try { Invoke-RestMethod -Method Post -Uri "$baseUrl/api/live-testing/enable" -TimeoutSec 5 | Out-Null } catch { }

# Poll for test discovery (up to 30s)
$discovered = 0
$discoveryElapsed = 0
$discoveryState = $null
$discoveryHint = $null
for ($i = 0; $i -lt 15; $i++) {
  Start-Sleep -Seconds 2
  $discoveryElapsed += 2
  try {
    $st = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/live-testing/status" -TimeoutSec 5
    $discovered = if ($null -ne $st.summary) { $st.summary.total } else { 0 }
    if ($st.PSObject.Properties.Match("DiscoveryState").Count -gt 0) {
      $discoveryState = [string]$st.DiscoveryState
    }
    if ($st.PSObject.Properties.Match("DiscoveryHint").Count -gt 0) {
      $discoveryHint = [string]$st.DiscoveryHint
    }

    if ($discoveryState -in @("ready_with_tests", "ready_zero_tests", "disabled")) {
      break
    }

    if (($null -eq $discoveryState) -and $discovered -gt 0) {
      break
    }
  } catch { }
}
Write-Host "  Tests discovered: $discovered (after ${discoveryElapsed}s, state: $discoveryState)" -ForegroundColor DarkGray

switch ($discoveryState) {
  "ready_with_tests" {
    Pass "tests-discovery" "Discovery completed with $discovered tests after ${discoveryElapsed}s"
  }
  "ready_zero_tests" {
    Fail "tests-discovery" "Discovery completed with zero tests after ${discoveryElapsed}s. $discoveryHint"
  }
  "disabled" {
    Fail "tests-discovery" "Live testing remained disabled after enable request. $discoveryHint"
  }
  "discovering" {
    Fail "tests-discovery" "Discovery did not complete within ${discoveryElapsed}s. $discoveryHint"
  }
  default {
    if ($discovered -gt 0) {
      Pass "tests-discovery" "Discovery completed with $discovered tests after ${discoveryElapsed}s"
    } else {
      Fail "tests-discovery" "Could not confirm discovery completion and 0 tests were discovered after ${discoveryElapsed}s"
    }
  }
}

try {
  $runBody = '{"timeout_seconds":30}'
  $runResp = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/live-testing/run" `
    -Body $runBody `
    -ContentType "application/json" `
    -TimeoutSec 40
  if ($runResp.success -eq $true) {
    Pass "run-tests" "run_tests accepted (discovered: $discovered)"
  } else {
    Fail "run-tests" "run_tests response: $($runResp | ConvertTo-Json -Compress)"
  }
} catch {
  Fail "run-tests" "run_tests request failed: $_"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 5b — mark-all-stale
# ─────────────────────────────────────────────────────────────────────────────
Section "5b. mark-all-stale"

try {
  $staleResp = Invoke-WebRequest -Method Post -Uri "$baseUrl/api/live-testing/mark-all-stale" `
    -Body "{}" `
    -ContentType "application/json" `
    -TimeoutSec 10 `
    -UseBasicParsing
  if ($staleResp.StatusCode -eq 202) {
    Pass "mark-all-stale" "POST /api/live-testing/mark-all-stale returned 202 Accepted"
  } else {
    Fail "mark-all-stale" "Expected 202, got $($staleResp.StatusCode)"
  }
} catch {
  Fail "mark-all-stale" "mark-all-stale request failed: $_"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 6 — Cleanup
# ─────────────────────────────────────────────────────────────────────────────
Section "6. Cleanup"

$anyFail = $results.Values -contains "FAIL"
if ($anyFail) {
  Capture-SmokeDiagnostics "One or more smoke steps failed"
}

if ($daemonProcess -and -not $daemonProcess.HasExited) {
  Stop-Process -Id $daemonProcess.Id -Force -ErrorAction SilentlyContinue
  Write-Host "  Daemon process stopped (PID $($daemonProcess.Id))" -ForegroundColor DarkGray
}

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
$null = Show-Summary
if ($anyFail) {
  Write-Host "RESULT: FAIL" -ForegroundColor Red
  exit 1
} else {
  Write-Host "RESULT: PASS" -ForegroundColor Green
  exit 0
}
