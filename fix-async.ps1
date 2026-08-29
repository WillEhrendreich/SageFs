# Script to find all files with Async.RunSynchronously and list them
$testDir = "C:\Code\Repos\SageFs\SageFs.Tests"
$files = Get-ChildItem -Path $testDir -Filter "*.fs" -Recurse
foreach ($file in $files) {
    $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
    if ($content -and $content.Contains("Async.RunSynchronously")) {
        $count = ([regex]::Matches($content, "Async\.RunSynchronously")).Count
        Write-Output "$($file.Name): $count occurrences"
    }
}
