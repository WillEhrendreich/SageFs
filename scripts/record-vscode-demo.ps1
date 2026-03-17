#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Record VS Code demo GIFs by driving a real VS Code instance with Playwright.

.DESCRIPTION
  Uses Playwright .NET to connect to VS Code via Chrome DevTools Protocol (CDP),
  captures screenshots at ~10fps while running scripted demo interactions, and
  composes the frames into palette-optimized animated GIFs via ffmpeg.

  Two demos are available:
    hero      — Opens getting-started.fsx, scrolls through F# code (no extension needed)
    extension — Shows SageFs extension: eval, live testing, command palette

.PARAMETER Demo
  Which demo to record: 'hero', 'extension', or 'all' (default: all).

.PARAMETER Fps
  Frames per second for the GIF (default: 10). Lower = smaller file.

.REQUIREMENTS
  - VS Code installed and on PATH
  - ffmpeg installed (choco install ffmpeg)
  - Playwright Chromium browsers installed (dotnet tool run playwright install chromium)
  - For 'extension' demo: SageFs VS Code extension installed + daemon running

.EXAMPLE
  .\scripts\record-vscode-demo.ps1
  .\scripts\record-vscode-demo.ps1 -Demo hero
  .\scripts\record-vscode-demo.ps1 -Demo extension -Fps 15
#>

param(
  [ValidateSet('hero', 'extension', 'all')]
  [string]$Demo = 'all',
  [int]$Fps = 10
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent

# ---- Preflight checks ----
Write-Host "🔍 Preflight checks..." -ForegroundColor Cyan

if (-not (Get-Command code -ErrorAction SilentlyContinue)) {
  Write-Error "VS Code not found on PATH. Install it or set VSCODE_PATH env var."
  exit 1
}

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
  Write-Error "ffmpeg not found. Install: choco install ffmpeg"
  exit 1
}

Write-Host "  ✓ VS Code found" -ForegroundColor Green
Write-Host "  ✓ ffmpeg found" -ForegroundColor Green

# ---- Build test project ----
Write-Host "`n🔨 Building SageFs.Tests..." -ForegroundColor Cyan
dotnet build "$repoRoot\SageFs.Tests\SageFs.Tests.fsproj" -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }
Write-Host "  ✓ Build succeeded" -ForegroundColor Green

# ---- Determine which tests to run ----
$filter = switch ($Demo) {
  'hero'      { 'FullyQualifiedName~Demo recording&FullyQualifiedName~hero' }
  'extension' { 'FullyQualifiedName~Demo recording&FullyQualifiedName~extension' }
  'all'       { 'FullyQualifiedName~Demo recording' }
}

# ---- Run demo recording tests ----
Write-Host "`n🎬 Recording demo: $Demo @ ${Fps}fps..." -ForegroundColor Cyan
Write-Host "   This launches VS Code, drives interactions, and captures screenshots." -ForegroundColor Gray
Write-Host "   The VS Code window must remain visible during recording." -ForegroundColor Yellow

# Run via the compiled test exe for cleaner output
$testExe = "$repoRoot\SageFs.Tests\bin\Debug\net10.0\SageFs.Tests.exe"
& $testExe --filter "$filter"
$testExit = $LASTEXITCODE

# ---- Report results ----
Write-Host ""
$gifDir = "$repoRoot\docs\media"
$gifs = Get-ChildItem $gifDir -Filter "sagefs-*.gif" -ErrorAction SilentlyContinue |
  Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-5) }

if ($gifs) {
  Write-Host "✅ Demo GIFs produced:" -ForegroundColor Green
  foreach ($gif in $gifs) {
    $sizeMb = [Math]::Round($gif.Length / 1MB, 1)
    Write-Host "   $($gif.Name) ($sizeMb MB)" -ForegroundColor Green
  }
  Write-Host "`n   Files: $gifDir" -ForegroundColor Gray
} else {
  Write-Warning "No GIFs found. Check output above for errors."
  Write-Host "   Frame directories: C:\temp\sagefs-demo-frames\" -ForegroundColor Gray
}

if ($testExit -ne 0 -and $testExit -ne 2) {
  Write-Warning "Test runner returned exit code $testExit (some demos may have failed)"
}
