#!/usr/bin/env pwsh
# scripts/record-demos.ps1
# Generates demo GIFs from VHS tape files using the vhs-fixed Docker image.
#
# Prerequisites:
#   - Docker Desktop running
#   - vhs-fixed:latest Docker image (built from djdarcy/vhs-windows-fixes fork)
#
# Usage:
#   ./scripts/record-demos.ps1           # Record all demos
#   ./scripts/record-demos.ps1 hero      # Record specific demo

param(
  [Parameter(Position = 0)]
  [string]$TapeName
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$mediaDir = Join-Path $repoRoot "docs\media"
$tapesDir = Join-Path $mediaDir "tapes"

# Verify Docker is available
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
  Write-Error "Docker is not installed or not in PATH."
  exit 1
}

# Verify vhs-fixed image exists
$imageCheck = docker images vhs-fixed --format "{{.Repository}}" 2>&1
if ($imageCheck -ne "vhs-fixed") {
  Write-Host "vhs-fixed Docker image not found. Build it first:" -ForegroundColor Yellow
  Write-Host "  See docs/media/tapes/README.md for build instructions" -ForegroundColor Yellow
  exit 1
}

# Find tape files
$tapes = if ($TapeName) {
  $path = Join-Path $tapesDir "$TapeName.tape"
  if (-not (Test-Path $path)) {
    Write-Error "Tape file not found: $path"
    exit 1
  }
  @(Get-Item $path)
} else {
  Get-ChildItem $tapesDir -Filter "*.tape"
}

if ($tapes.Count -eq 0) {
  Write-Host "No tape files found in $tapesDir" -ForegroundColor Yellow
  exit 0
}

Write-Host "Recording $($tapes.Count) demo(s)..." -ForegroundColor Cyan

foreach ($tape in $tapes) {
  Write-Host "`n  Recording: $($tape.Name)" -ForegroundColor Green

  # VHS tape files use LF line endings — ensure this on Windows
  $content = [System.IO.File]::ReadAllText($tape.FullName)
  $lfContent = $content.Replace("`r`n", "`n")
  if ($content -ne $lfContent) {
    [System.IO.File]::WriteAllText($tape.FullName, $lfContent)
    Write-Host "    Fixed CR/LF → LF" -ForegroundColor DarkGray
  }

  # Run VHS in Docker, mounting the media directory
  $exitCode = 0
  docker run --rm -v "${mediaDir}:/vhs" vhs-fixed /vhs/tapes/$($tape.Name)
  $exitCode = $LASTEXITCODE

  if ($exitCode -eq 0) {
    $gifName = $tape.BaseName
    $gifPath = Join-Path $mediaDir "sagefs-$gifName.gif"
    if (Test-Path $gifPath) {
      $size = [math]::Round((Get-Item $gifPath).Length / 1024, 1)
      Write-Host "    ✓ Generated: sagefs-$gifName.gif (${size} KB)" -ForegroundColor Green
    } else {
      # Check if the output name matches the tape's Output directive
      $outputLine = ($content -split "`n") | Where-Object { $_ -match '^Output ' } | Select-Object -First 1
      if ($outputLine) {
        $outputFile = ($outputLine -replace '^Output\s+', '').Trim()
        $actualPath = Join-Path $mediaDir $outputFile
        if (Test-Path $actualPath) {
          $size = [math]::Round((Get-Item $actualPath).Length / 1024, 1)
          Write-Host "    ✓ Generated: $outputFile (${size} KB)" -ForegroundColor Green
        }
      }
    }
  } else {
    Write-Host "    ✗ VHS failed with exit code $exitCode" -ForegroundColor Red
  }
}

Write-Host "`nDone." -ForegroundColor Cyan
