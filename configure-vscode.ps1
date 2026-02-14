# SageFs VSCode Configuration Script
# Automatically configures Ionide to use SageFs

$ErrorActionPreference = "Stop"

Write-Host "🔍 Finding SageFs installation..." -ForegroundColor Cyan

# Find SageFs.Server.dll
$SageFsDll = Get-ChildItem -Path "$env:USERPROFILE\.dotnet\tools\.store" -Recurse -Filter "SageFs.Server.dll" -ErrorAction SilentlyContinue | 
    Select-Object -First 1

if (-not $SageFsDll) {
    Write-Host "❌ SageFs not found. Please install it first:" -ForegroundColor Red
    Write-Host "   dotnet tool install -g SageFs.Server" -ForegroundColor Yellow
    exit 1
}

$dllPath = $SageFsDll.FullName
Write-Host "✅ Found SageFs at: $dllPath" -ForegroundColor Green

# Find VSCode settings file
$settingsPath = "$env:APPDATA\Code\User\settings.json"

if (-not (Test-Path $settingsPath)) {
    Write-Host "❌ VSCode settings file not found at: $settingsPath" -ForegroundColor Red
    Write-Host "   Please ensure VSCode is installed" -ForegroundColor Yellow
    exit 1
}

Write-Host "📝 Updating VSCode settings..." -ForegroundColor Cyan

# Read current settings
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json

# Escape backslashes for JSON
$dllPathJson = $dllPath -replace '\\', '\\'

# Update the setting
$settings | Add-Member -MemberType NoteProperty -Name "FSharp.fsiSdkFilePath" -Value $dllPathJson -Force

# Write back
$settings | ConvertTo-Json -Depth 100 | Set-Content $settingsPath

Write-Host "✅ VSCode configured successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "You can now use SageFs with Ionide:" -ForegroundColor Cyan
Write-Host "  1. Press Ctrl+Shift+P → 'FSI: Start'" -ForegroundColor White
Write-Host "  2. Use Alt+Enter to send code to SageFs" -ForegroundColor White
Write-Host ""
Write-Host "Setting: FSharp.fsiSdkFilePath = $dllPath" -ForegroundColor Gray
