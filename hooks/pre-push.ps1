#!/usr/bin/env pwsh

# Build in Release mode to catch FS3511 and other codegen warnings
# FS3511 ("state machine not statically compilable") only surfaces during
# code generation, which may differ between Debug and Release. Building
# Release locally mirrors CI and catches it before push.
Write-Host "Building in Release mode (FS3511 safety gate)..."
$slnx = Get-ChildItem -Path . -Filter *.slnx | Select-Object -First 1
if ($slnx) {
    dotnet build $slnx.FullName -c Release --no-restore -consoleLoggerParameters:NoSummary 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Release build failed — fix errors before pushing." -ForegroundColor Red
        Write-Host "Hint: FS3511 means a task CE can't be statically compiled."
        Write-Host "      Simplify try/with nesting or restructure the async flow."
        exit 1
    }
    Write-Host "Release build passed." -ForegroundColor Green
}

# Check Directory.Build.props first (preferred location for centralized versioning)
$propsFile = "Directory.Build.props"
$versionSource = $null
$currentVersion = $null

if (Test-Path $propsFile) {
    $content = Get-Content $propsFile -Raw
    if ($content -match '<Version>([^<]+)</Version>') {
        $currentVersion = $matches[1]
        $versionSource = $propsFile
    }
}

# If not found in Directory.Build.props, look in project files
if (-not $currentVersion) {
    $projectFiles = Get-ChildItem -Path . -Recurse -Include "*.csproj","*.fsproj","*.vbproj" -File | Select-Object -ExpandProperty FullName
    foreach ($projFile in $projectFiles) {
        $content = Get-Content $projFile -Raw
        if ($content -match '<Version>([^<]+)</Version>') {
            $currentVersion = $matches[1]
            $versionSource = $projFile
            break
        }
    }
}

if (-not $currentVersion) {
    Write-Host "No version found in any project file or Directory.Build.props"
    exit 0
}

Write-Host "Found version $currentVersion in $versionSource"

# Check if HEAD commit already has a tag with this version
$headTags = git tag --points-at HEAD
$expectedTag = "v$currentVersion"

if ($headTags -contains $expectedTag) {
    Write-Host "Current commit already tagged with $expectedTag, skipping version bump"
    exit 0
}

# Check if there are staged changes (indicates we already ran)
$stagedChanges = git diff --cached --name-only
if ($stagedChanges -match 'Directory\.Build\.props|\.csproj$|\.fsproj$|\.vbproj$') {
    Write-Host "Project files already staged, skipping to prevent double-bump"
    exit 0
}

# Parse and increment patch version, skipping past any existing tags
$versionParts = $currentVersion -split '\.'
$major = $versionParts[0]
$minor = $versionParts[1]
$patch = [int]$versionParts[2] + 1
$newVersion = "$major.$minor.$patch"

while (git tag -l "v$newVersion") {
    Write-Host "Tag v$newVersion already exists on another commit, skipping to next patch"
    $patch++
    $newVersion = "$major.$minor.$patch"
}

Write-Host "Bumping version from $currentVersion to $newVersion"

# Update Directory.Build.props if it exists and has version
if ((Test-Path $propsFile) -and (Get-Content $propsFile -Raw) -match '<Version>') {
    $content = Get-Content $propsFile -Raw
    $content = $content -replace "<Version>$currentVersion</Version>", "<Version>$newVersion</Version>"
    $content = $content -replace "<AssemblyVersion>$currentVersion</AssemblyVersion>", "<AssemblyVersion>$newVersion</AssemblyVersion>"
    $content = $content -replace "<FileVersion>$currentVersion</FileVersion>", "<FileVersion>$newVersion</FileVersion>"
    Set-Content -Path $propsFile -Value $content -NoNewline
    git add $propsFile
    Write-Host "  Updated $propsFile"
}

# Update all project files that contain version
$allProjectFiles = Get-ChildItem -Path . -Recurse -Include "*.csproj","*.fsproj","*.vbproj" -File
foreach ($projFile in $allProjectFiles) {
    $content = Get-Content $projFile.FullName -Raw
    if ($content -match '<Version>') {
        $content = $content -replace "<Version>$currentVersion</Version>", "<Version>$newVersion</Version>"
        $content = $content -replace "<AssemblyVersion>$currentVersion</AssemblyVersion>", "<AssemblyVersion>$newVersion</AssemblyVersion>"
        $content = $content -replace "<FileVersion>$currentVersion</FileVersion>", "<FileVersion>$newVersion</FileVersion>"
        Set-Content -Path $projFile.FullName -Value $content -NoNewline
        git add $projFile.FullName
        Write-Host "  Updated $($projFile.Name)"
    }
}

# Amend the current commit with the version bump
git commit --amend --no-edit --no-verify

# Create tag
git tag "v$newVersion"

Write-Host "Version bumped to $newVersion and tagged"
exit 0
