// scripts/lemmings/run-lemming-cmd.fsx <fixture> <task> <free-model> <run-id|auto> [max-turns]
// Run with: dotnet fsi scripts/lemmings/run-lemming-cmd.fsx -- <args>
//
// One disposable trial: Command Code (cmdc) on a FREE model, given only what a new user has (the
// project, the SageFs skill where cmdc discovers skills, the SageFs MCP server registered the way the
// README says), does a task with SageFs. The harness records what happened, runs the task's oracle
// itself, writes <run-dir>/out/summary.json, and then prunes the run down to out/ (set LEM_KEEP_RUN=1 to
// keep it whole). See scripts/lemmings/README.md.
//
//   fixture    a directory under scripts/lemmings/fixtures (demoenv), or sagefs-copy:<path to a SageFs checkout>
//   task       a file scripts/lemmings/tasks/<task>.md
//   free-model a model the live `cmdc --list-models` marks FREE; anything else is refused
//   run-id     <model-short>-<task>-<nn>, e.g. space-bunny-parse-seed-01, or "auto" for the next free one
//
// Exit: 0 summary.json written (read `outcome`), 1 argument missing, 2 refused, 3 shared daemon will not
// do, 4 build or toolchain missing. The logic is LemRun (scripts/lemmings/LemRun), built on first use.
#load "launch.fsx"
Launch.run "LemRun" [ "run-cmd" ]
