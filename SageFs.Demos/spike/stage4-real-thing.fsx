// SageFs.Demos/spike/stage4-real-thing.fsx [out-dir]
// Run with: dotnet fsi SageFs.Demos/spike/stage4-real-thing.fsx -- [out-dir]
//
// Phase-0 Stage 4: "the real thing": a SageFs daemon, running inside the sealed cell, driven end to end through
// the boundary demo-gif-plan.md §4.1 describes: the runner (this script) pipes a one-line ScenarioPlan JSON
// into the cell's stdin (crossing the bwrap namespace wall as the process's own stdin; nothing is opened from the
// host into the cell), and the cell-agent inside the cell drives Xvfb + a real Chromium + a libXtst click on the
// dashboard's Quick Start control, observes a session card appear via Playwright, and streams back one line of
// StepLog JSON on stdout, while ffmpeg records the whole exchange to /out/stage4.mkv. `cellinit stage4` is the
// cell's supervisor (it starts Xvfb, the daemon and ffmpeg, runs the cell-agent, and tears all of it down).
//
// CRITICAL SAFETY RULE: this daemon must NEVER touch the user's real ports (37749/37750) or real ~/.SageFs data.
// It runs on 47749/47750 inside the cell's own private network namespace (so those ports don't even collide with
// anything on the host; belt-and-suspenders) with SAGEFS_DATA_DIR pointed at the cell's own tmpfs.
//
// SAGEFS_BIN_DIR names the SageFs build to run in the cell (default <repo>/SageFs/bin/Release/net10.0).
//
// Exit: 0 proven; 1 the click did not register or no video; 3 a prerequisite is missing; 4 a publish failed; 5 the cell failed.
#load "spike-common.fsx"
open System
open System.IO
open SpikeCommon

let scenarioPlan =
  """{"scenarioId":"hello-dashboard","chromePath":"/chrome-bin/chrome","pageUrl":"http://127.0.0.1:47750/dashboard","clickSelector":"[data-testid=quick-start]","expectSelector":"[data-testid=session-card]","userDataDir":"/home/demo/chrome-profile"}"""

let usage = "usage: dotnet fsi SageFs.Demos/spike/stage4-real-thing.fsx -- [out-dir]   (default out-dir: /tmp/sagefs-demo-spike/cells/stage4/out)"

let code =
  main' usage (fun args ->
    let out = freshOut args "stage4"
    for f in Directory.GetFiles out do File.Delete f
    let sagefsBin =
      match Environment.GetEnvironmentVariable "SAGEFS_BIN_DIR" with
      | null | "" -> Path.Combine(repoRoot, "SageFs", "bin", "Release", "net10.0")
      | dir -> dir
    if not (File.Exists(Path.Combine(chromeDir, "chrome"))) then fail (ChromiumMissing chromeDir)
    if not (File.Exists(Path.Combine(sagefsBin, "SageFs.dll"))) then fail (SageFsNotBuilt sagefsBin)
    say "== Stage 4: the real thing =="
    say "Building self-contained cell-agent (outside the cell)..."
    let agentBuild = Path.Combine(workRoot, "cell-agent-build-stage4")
    publish (Path.Combine(spikeDir, "cell-agent", "CellAgent.fsproj")) agentBuild (Path.Combine(workRoot, "cellagent-build.log"))
    say (sprintf "Built: %s/cellagent" agentBuild)
    publishCellInit ()
    let dotnetDir =
      let real = run "readlink" [ "-f"; (match run "which" [ "dotnet" ] with { ExitCode = 0; Stdout = p } -> p.Trim() | _ -> fail (ChromiumMissing "dotnet on PATH")) ]
      match Path.GetDirectoryName(real.Stdout.Trim()) with
      | null -> fail (NotProven "could not resolve dotnet root")
      | d when File.Exists(Path.Combine(d, "dotnet")) -> d
      | d -> fail (NotProven (sprintf "could not resolve dotnet root from %s" d))
    let planFile = Path.Combine(workRoot, "stage4-plan.json")
    File.WriteAllText(planFile, scenarioPlan + "\n")
    let steplog = Path.Combine(out, "steplog.json")
    let cell =
      runCell "stage4" out
        [ ("/etc/ssl", "/etc/ssl"); (chromeDir, "/chrome-bin"); (agentBuild, "/cellagent-bin"); (sagefsBin, "/sagefs-bin"); (dotnetDir, "/dotnet-root") ]
        (Some planFile) (Some steplog)
    say "== Host-side verification =="
    say (sprintf "Cell exit code: %d" cell)
    say "--- daemon.log (tail) ---"
    let daemonLog = Path.Combine(out, "daemon.log")
    if File.Exists daemonLog then File.ReadAllLines daemonLog |> Array.rev |> Array.truncate 20 |> Array.rev |> Array.iter say
    say "--- StepLog (the boundary's output, read by the runner from the cell-agent's stdout) ---"
    say (if File.Exists steplog && fileBytes steplog > 0L then File.ReadAllText steplog else "(empty)")
    let passed = cell = 0 && File.Exists steplog && File.ReadAllText(steplog).Contains "\"outcome\":\"Passed\""
    say (if passed then "PASS: boundary crossed (stdin ScenarioPlan -> stdout StepLog) and the click registered" else sprintf "FAIL: cell exited %d or StepLog does not report Passed" cell)
    let video = checkNonEmpty (Path.Combine(out, "stage4.mkv"))
    passed && video)

exit code
