// scripts/lemmings/ui/nvim/run-nvim-lemming.fsx <task> <free-model> [max-turns]
// scripts/lemmings/ui/nvim/run-nvim-lemming.fsx <task> <free-model> <run-id|auto> [max-turns]
// Run with: dotnet fsi scripts/lemmings/ui/nvim/run-nvim-lemming.fsx -- <args>
//
// One disposable Neovim trial: Command Code (cmdc) on a FREE model, handed a Neovim it can only drive
// with keys, does a task with the sagefs.nvim plugin against the ONE shared SageFs daemon (watched in its
// dashboard). The harness records every driver call as a numbered screen, runs the task's oracle itself,
// writes <run-dir>/out/summary.json, and prunes the run to out/ (LEM_KEEP_RUN=1 keeps it whole).
//
//   task        a file scripts/lemmings/ui/nvim/tasks/<task>.md with a <task>.env beside it
//               (ui-eval, ui-edit-reeval, ui-live-tests, ui-hot-reload, ui-find-help)
//   free-model  a model the live `cmdc --list-models` marks FREE; anything else is refused
//   run-id      <model-short>-<task>-<nn>, e.g. space-bunny-ui-eval-01, or "auto"
//
// The daemon is never started, stopped or reconfigured here. Residue is read from the daemon's own API
// first and stopped by exact session id, only for sessions under this run directory. The logic is LemRun's
// NvimRun.fs; the editor is driven by the F# driver (LemDrive.dll nvim), see ui/nvim/README.md.
#load "../../launch.fsx"
Launch.run "LemRun" [ "run-nvim" ]
