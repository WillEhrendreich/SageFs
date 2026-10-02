// scripts/lemmings/run-ui-lemming.fsx <vscode|nvim> <task> <free-model> [max-turns]
// Run with: dotnet fsi scripts/lemmings/run-ui-lemming.fsx -- <args>
//
// One disposable trial where a Command Code lemming on a FREE model drives a real editor that has the
// SageFs plugin, as a client of the ONE shared SageFs daemon on 37749. This only picks the runner for
// the editor:
//
//   vscode   VS Code on Xvfb, driven over CDP        (also run-vscode-lemming.fsx)
//   nvim     Neovim in tmux, driven with keys        (also run-nvim-lemming.fsx)
//
// <task> is a file under the editor's tasks directory, without .md (ui-eval, ui-edit-reeval,
// ui-live-tests, ui-hot-reload, ui-find-help). <model> must be marked FREE in the live `cmdc --list-models`;
// anything else is refused before anything starts.
#load "launch.fsx"
Launch.run "LemRun" [ "run-ui" ]
