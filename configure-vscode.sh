#!/bin/bash
# SageFs VSCode Configuration Script
# Automatically configures Ionide to use SageFs

set -e

echo "🔍 Finding SageFs installation..."

# Find SageFs.Server.dll
SageFs_DLL=$(find ~/.dotnet/tools/.store -name "SageFs.Server.dll" 2>/dev/null | head -n 1)

if [ -z "$SageFs_DLL" ]; then
    echo "❌ SageFs not found. Please install it first:"
    echo "   dotnet tool install -g SageFs.Server"
    exit 1
fi

echo "✅ Found SageFs at: $SageFs_DLL"

# Find VSCode settings file
if [ "$(uname)" == "Darwin" ]; then
    SETTINGS_PATH="$HOME/Library/Application Support/Code/User/settings.json"
else
    SETTINGS_PATH="$HOME/.config/Code/User/settings.json"
fi

if [ ! -f "$SETTINGS_PATH" ]; then
    echo "❌ VSCode settings file not found at: $SETTINGS_PATH"
    echo "   Please ensure VSCode is installed"
    exit 1
fi

echo "📝 Updating VSCode settings..."

# Use jq to update settings (install if needed)
if ! command -v jq &> /dev/null; then
    echo "❌ jq is required but not installed."
    echo "   Install it with: sudo apt-get install jq  (or brew install jq on macOS)"
    exit 1
fi

# Update the setting
jq --arg path "$SageFs_DLL" '."FSharp.fsiSdkFilePath" = $path' "$SETTINGS_PATH" > "$SETTINGS_PATH.tmp"
mv "$SETTINGS_PATH.tmp" "$SETTINGS_PATH"

echo "✅ VSCode configured successfully!"
echo ""
echo "You can now use SageFs with Ionide:"
echo "  1. Press Ctrl+Shift+P → 'FSI: Start'"
echo "  2. Use Alt+Enter to send code to SageFs"
echo ""
echo "Setting: FSharp.fsiSdkFilePath = $SageFs_DLL"
