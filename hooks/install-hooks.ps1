#!/usr/bin/env pwsh
# Install git hooks from the tracked hooks/ folder to .git/hooks/

$hooksDir = Join-Path $PSScriptRoot ".." ".git" "hooks"

Write-Host "Installing git hooks to $hooksDir"

# Copy pre-push script
Copy-Item "$PSScriptRoot\pre-push.ps1" "$hooksDir\pre-push.ps1" -Force
Copy-Item "$PSScriptRoot\pre-push" "$hooksDir\pre-push" -Force

# Ensure executable on Unix-like systems
if ($IsLinux -or $IsMacOS) {
    chmod +x "$hooksDir/pre-push"
}

Write-Host "Git hooks installed successfully!"
Write-Host "  - pre-push (FS3511 build gate + version bumping)"
