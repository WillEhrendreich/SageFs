Output ../sagefs-hero.gif
Set FontSize 20
Set Width 1200
Set Height 600
Set Padding 20
Set Theme "Catppuccin Mocha"

Hide
Type "clear"
Enter
Sleep 500ms
Show

Type "# SageFs — F# Live Development"
Enter
Sleep 1s

Type "sagefs --proj MyApp.fsproj"
Enter
Sleep 2s

Type "let taxes income ="
Enter
Sleep 300ms
Type "  match income with"
Enter
Sleep 300ms
Type "  | x when x < 10_000m -> 0.0m"
Enter
Sleep 300ms
Type "  | x when x < 50_000m -> x * 0.2m"
Enter
Sleep 300ms
Type "  | x -> x * 0.35m"
Enter
Type ";;"
Enter
Sleep 1.5s

Type "taxes 75_000m;;"
Enter
Sleep 1s

Type "// val it: decimal = 26250.0M"
Enter
Sleep 500ms

Type "// ✓ 3 tests passing — 0.4ms"
Enter
Sleep 2s
