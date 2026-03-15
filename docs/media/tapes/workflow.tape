Output ../sagefs-workflow.gif
Set FontSize 20
Set Width 1200
Set Height 500
Set Padding 20
Set Theme "Catppuccin Mocha"

Hide
Type "clear"
Enter
Sleep 500ms
Show

Type "# Workflow Modes: REPL vs Live"
Enter
Sleep 1s

Type "sagefs status"
Enter
Sleep 500ms
Type "Session: a1b2c3d4  [REPL]  Ready"
Enter
Type "  Type redefinition: Full"
Enter
Type "  Hot reload: Off"
Enter
Sleep 1.5s

Type "sagefs switch-workflow weblive --dry-run"
Enter
Sleep 500ms
Type "  Transition cost:"
Enter
Type "    Definitions lost: 5"
Enter
Type "    Cells lost: 3"
Enter
Type "    Standby ready: Yes (~200ms)"
Enter
Sleep 2s

Type "sagefs switch-workflow weblive"
Enter
Sleep 500ms
Type "  ⚡ Switched to [Live] mode"
Enter
Type "  Session: e5f6g7h8  [Live]  Ready"
Enter
Type "  Hot reload: Active (watching src/)"
Enter
Sleep 2s
