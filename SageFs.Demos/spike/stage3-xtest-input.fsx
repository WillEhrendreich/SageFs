// SageFs.Demos/spike/stage3-xtest-input.fsx [out-dir]
// Run with: dotnet fsi SageFs.Demos/spike/stage3-xtest-input.fsx -- [out-dir]
//
// Phase-0 Stage 3: fake input via libXtst, delivered from INSIDE the bwrap cell.
//
// Proves: a self-contained .NET executable (built from xtest-app/Program.fs, published self-contained so the
// cell needs no dotnet SDK) P/Invokes XOpenDisplay (libX11.so.6) and XTestFakeMotionEvent/XTestFakeButtonEvent
// (libXtst.so.6) to move the pointer and click a real Chromium page's button, then reads the result back
// through Playwright (document.title changed), not by eyeballing pixels.
//
// NOTE ON THE PLAN'S §2 CLAIM: demo-gif-plan.md says libXtst is "NOT installed" on this machine and must be
// bundled/extracted. That is stale: `ldconfig -p` shows /usr/lib/libXtst.so.6 present today (see STAGE-REPORT.md).
// The system libXtst/libX11 are therefore used read-only through /usr, rather than extracting a package.
//
// Exit: 0 proven; 1 the click did not register; 3 a prerequisite is missing; 4 a publish failed; 5 the cell failed.
#load "spike-common.fsx"
open System.IO
open SpikeCommon

let usage = "usage: dotnet fsi SageFs.Demos/spike/stage3-xtest-input.fsx -- [out-dir]   (default out-dir: /tmp/sagefs-demo-spike/cells/stage3/out)"

let code =
  main' usage (fun args ->
    let out = freshOut args "stage3"
    for f in Directory.GetFiles out do File.Delete f
    if not (File.Exists(Path.Combine(chromeDir, "chrome"))) then fail (ChromiumMissing chromeDir)
    say "== Stage 3: fake input via libXtst =="
    say "Building self-contained xtestapp (outside the cell; dotnet is not bound into it)..."
    let appBuild = Path.Combine(workRoot, "xtest-app-build")
    publish (Path.Combine(spikeDir, "xtest-app", "XTestApp.fsproj")) appBuild (Path.Combine(workRoot, "xtestapp-build.log"))
    say (sprintf "Built: %s/xtestapp (%d bytes)" appBuild (fileBytes (Path.Combine(appBuild, "xtestapp"))))
    publishCellInit ()
    let cell = runCell "stage3" out [ (chromeDir, "/chrome"); (Path.Combine(spikeDir, "fixtures"), "/work"); (appBuild, "/app") ] None None
    say "== Host-side verification =="
    say (sprintf "Cell exit code: %d" cell)
    let log = Path.Combine(out, "xtestapp.log")
    if File.Exists log then
      say "--- xtestapp.log ---"
      say (File.ReadAllText log)
    match cell = 0 && File.Exists log && File.ReadLines log |> Seq.exists (fun l -> l.StartsWith "PASS:") with
    | true ->
      say "PASS: XTest-delivered click registered inside the sealed cell"
      true
    | false ->
      say (sprintf "FAIL: cell exited %d or no PASS line in xtestapp.log" cell)
      let err = Path.Combine(out, "xtestapp.stderr.log")
      if File.Exists err then
        say "--- stderr ---"
        say (File.ReadAllText err)
      false)

exit code
