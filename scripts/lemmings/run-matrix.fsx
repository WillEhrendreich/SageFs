// scripts/lemmings/run-matrix.fsx <plan-file|-> [--concurrency N] [--max-turns N] [--quota-limit N]
//                                              [--root DIR] [--runner PATH] [--dry-run]
// Run with: dotnet fsi scripts/lemmings/run-matrix.fsx -- <args>
//
// Runs a list of (fixture, task, model, rep) lemming runs with a concurrency cap (default 3), unique run
// ids, and free-model quota awareness (a model that hits its quota twice gets no more runs), then prints
// a table. The bookkeeping is LemMatrix (scripts/lemmings/LemMatrix), built on first use; each run is
// LemRun's run-cmd. See scripts/lemmings/README.md for the plan format.
#load "launch.fsx"
Launch.run "LemMatrix" []
