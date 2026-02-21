#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Converts the hot-reload demo .webm recording (produced by the Playwright test) to an
  animated GIF suitable for sharing.

.DESCRIPTION
  Runs the Playwright integration test first (which records a .webm video of the browser
  auto-reloading when a Falco handler function is hot-patched), then uses ffmpeg to
  convert the video to a GIF.

.REQUIREMENTS
  - ffmpeg in PATH (winget install ffmpeg  /  choco install ffmpeg  /  scoop install ffmpeg)
  - A running SageFs daemon (start with: sagefs --proj SageFs.Tests\SageFs.Tests.fsproj)
  - Playwright Chromium installed (dotnet tool run playwright install chromium)

.EXAMPLE
  .\scripts\make-demo-gif.ps1
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent

# ---- Step 1: build the test project ----
Write-Host "Building SageFs.Tests..." -ForegroundColor Cyan
dotnet build "$repoRoot\SageFs.Tests\SageFs.Tests.fsproj" -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }

# ---- Step 2: run only the hot-reload browser test ----
Write-Host "`nRunning hot-reload browser test (headed Chromium, recording video)..." -ForegroundColor Cyan
dotnet test "$repoRoot\SageFs.Tests\SageFs.Tests.fsproj" `
  --filter "FullyQualifiedName~HotReload" `
  --no-build `
  --logger "console;verbosity=minimal" `
  -- RunConfiguration.TestSessionTimeout=60000
if ($LASTEXITCODE -ne 0) { Write-Error "Test failed"; exit 1 }

# ---- Step 3: find the .webm video ----
$testOutputDir = Get-ChildItem "$repoRoot\SageFs.Tests\bin\Debug\net10.0\hot-reload-demo" `
  -Filter "*.webm" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $testOutputDir) {
  Write-Warning "No .webm recording found. Did the test run correctly?"
  Write-Host "Screenshots are in: $repoRoot\SageFs.Tests\bin\Debug\net10.0\hot-reload-demo\"
  exit 0
}

$webm  = $testOutputDir.FullName
$gif   = Join-Path (Split-Path $webm) "hot-reload-demo.gif"
$paletteFile = Join-Path $env:TEMP "sagefs-palette.png"

Write-Host "`nConverting $webm → $gif ..." -ForegroundColor Cyan

# Check ffmpeg
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
  Write-Warning @"
ffmpeg not found. Install it with one of:
  winget install ffmpeg
  choco install ffmpeg
  scoop install ffmpeg

Video is at: $webm
Screenshots: $(Split-Path $webm)\01-before-reload.png
             $(Split-Path $webm)\02-after-reload.png
"@
  exit 0
}

# Two-pass GIF encoding (palette-based for best quality)
ffmpeg -y -i $webm -vf "fps=15,scale=1280:-1:flags=lanczos,palettegen" $paletteFile 2>$null
ffmpeg -y -i $webm -i $paletteFile `
  -filter_complex "fps=15,scale=1280:-1:flags=lanczos[x];[x][1:v]paletteuse" `
  $gif 2>$null

if (Test-Path $gif) {
  $sizeMb = [Math]::Round((Get-Item $gif).Length / 1MB, 1)
  Write-Host "`n✅  GIF saved: $gif ($sizeMb MB)" -ForegroundColor Green
  Write-Host "   Share it, drag it into a GitHub comment, or embed it in docs." -ForegroundColor Gray
} else {
  Write-Error "ffmpeg ran but GIF was not created"
}
