#!/usr/bin/env pwsh
# Rebuilds and reinstalls the SageFs VSCode extension, then reloads VSCode.
param([switch]$NoReload)

$ErrorActionPreference = 'Stop'
Push-Location "$PSScriptRoot\..\sagefs-vscode"

try {
  Write-Host "Compiling extension..." -ForegroundColor Cyan
  npm run compile
  if ($LASTEXITCODE -ne 0) { throw "Compile failed" }

  Write-Host "Packaging VSIX..." -ForegroundColor Cyan
  npx @vscode/vsce package --no-dependencies --skip-license
  if ($LASTEXITCODE -ne 0) { throw "Package failed" }

  $vsix = Get-ChildItem *.vsix | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  Write-Host "Installing $($vsix.Name)..." -ForegroundColor Cyan
  code --install-extension $vsix.FullName --force
  if ($LASTEXITCODE -ne 0) { throw "Install failed" }

  if (-not $NoReload) {
    Write-Host "Reloading VSCode..." -ForegroundColor Cyan
    # Re-open the workspace folder to trigger extension reload
    $workspaceRoot = (Resolve-Path "$PSScriptRoot\..").Path
    code -r $workspaceRoot
  }

  Write-Host "Done." -ForegroundColor Green
} finally {
  Pop-Location
}
