// scripts/lemmings/ui/vscode/run-vscode-tour.fsx <tour-file> [<more tour files>...]
// Run with: dotnet fsi scripts/lemmings/ui/vscode/run-vscode-tour.fsx -- <args>
//
// Runs tour files (TOURS.md) against a fresh VS Code with the SageFs extension, as a client of the shared
// SageFs daemon, for the automated design review. No model is called. Each tour gets its own run directory,
// /tmp/lem/tour-<tour-name>-<nn>, with the numbered shots in out/shots and the driver's call log in
// out/screens; the run is pruned to out/ when it ends. Same isolation as run-vscode-lemming: Xvfb on :80 and
// up, VS Code in bubblewrap, exact pids only, `hyprctl clients` before and after.
//
// Exit: 0 every tour ran and nothing was left behind; 1 a tour step failed; 5 a window leaked onto the real
// desktop or a session was left on the dashboard.
#load "../../launch.fsx"
Launch.run "LemRun" [ "vscode-tour" ]
