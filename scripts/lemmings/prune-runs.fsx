// scripts/lemmings/prune-runs.fsx [run-id ...] [--older-than-days N] [--dry-run] [--root DIR] [--whole] [--builds]
// Run with: dotnet fsi scripts/lemmings/prune-runs.fsx -- <args>
//
// Prunes finished lemming runs from the run root (default $LEM_ROOT, else /tmp/lem). A run ends by pruning
// itself to out/ (summary.json, timeline.ndjson, shots, logs), so this is for runs that were kept
// (LEM_KEEP_RUN=1) or that ended before this existed. Nothing is touched unless you name runs or give an
// age, and a run with no out/summary.json (or out/tour.json) is never touched (it may be running).
//
//   --whole    remove the run directory too, evidence included
//   --builds   also remove stored SageFs and driver builds nobody has used for a week (the newest three are kept)
//
//   dotnet fsi scripts/lemmings/prune-runs.fsx -- --older-than-days 7 --dry-run
//   dotnet fsi scripts/lemmings/prune-runs.fsx -- space-bunny-parse-seed-03
#load "launch.fsx"
Launch.run "LemRun" [ "prune"; "--root"; Launch.lemRoot ]
