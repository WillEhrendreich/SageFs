// scripts/lemmings/ui/vscode/run-vscode-lemming.fsx <task> <free-model> [max-turns]
// Run with: dotnet fsi scripts/lemmings/ui/vscode/run-vscode-lemming.fsx -- <args>
//
// One disposable VS Code trial: a Command Code lemming on a FREE model drives the real VS Code (with the
// SageFs extension from this repo) through the vsc-* tools, as a client of the ONE shared SageFs daemon on
// 37749. The harness records the run and scores it; the oracle is run by the harness outside the sandbox,
// never the model's claim. The run is pruned to out/ when it ends (LEM_KEEP_RUN=1 keeps it whole).
//
//   task      a file under scripts/lemmings/ui/vscode/tasks (without .md)
//   model     a model the live `cmdc --list-models` marks FREE (anything else is refused)
//
// Exit: the run's, plus 5 when a window appeared on the real desktop and 6 when the shared daemon's pid
// changed during the run. What is protected, and how, is the header of LemRun's VscRun.fs.
#load "../../launch.fsx"
Launch.run "LemRun" [ "run-vscode" ]
