# Git Hooks

This directory contains version-controlled git hooks.

## Available Hooks

### pre-push
Builds in Release mode (FS3511 safety gate) then auto-increments patch version.

**Features:**
- **FS3511 build gate** — builds Release mode before push to catch "state machine not statically compilable" errors that only surface during code generation
- Increments version in project files and Directory.Build.props
- Creates a git tag with the new version
- Amends the current commit with version changes
- Prevents double-bumping with safeguards
- Skips past existing tags automatically

## Installation

Run the installation script to copy hooks to your `.git/hooks/` directory:

```powershell
pwsh hooks/install-hooks.ps1
```
