// scripts/lemmings/run-lemming.fsx <fixture> <task> <model> <run-id> [max-turns]
// Run with: dotnet fsi scripts/lemmings/run-lemming.fsx -- <args>
//
// The older harness, for Claude Code: one onboarding trial, a cheap model given only what a new user has,
// run on the PUBLISHED sagefs on a port and data directory of its own (never the daemon on 37749). The
// cmdc harness (run-lemming-cmd.fsx) is the one the README describes. The logic is LemRun's Legacy.fs.
#load "launch.fsx"
Launch.run "LemRun" [ "legacy-run" ]
