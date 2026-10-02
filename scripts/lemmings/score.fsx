// scripts/lemmings/score.fsx <events.ndjson> <residue.sessions.json> --id ID --model M --exit N --seconds N
//                            --graceful X --forced X --tests X --diff FILE
// Run with: dotnet fsi scripts/lemmings/score.fsx -- <args>
//
// Turns one Claude Code run's event stream plus the harness's own checks into a summary.json on stdout.
// Everything in it is read off the stream or measured by the harness. None of it is the model's claim.
// (The cmdc harness scores through LemScore.) The logic is LemRun's Legacy.fs.
#load "launch.fsx"
Launch.run "LemRun" [ "legacy-score" ]
