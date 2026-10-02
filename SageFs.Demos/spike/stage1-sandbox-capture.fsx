// SageFs.Demos/spike/stage1-sandbox-capture.fsx [out-dir]
// Run with: dotnet fsi SageFs.Demos/spike/stage1-sandbox-capture.fsx -- [out-dir]
//
// Phase-0 Stage 1: bwrap cell + Xvfb + ffmpeg x11grab capture to a host-bind /out.
// Proves: sandbox construction, private display, and the RW bind-mount data plane (a file written inside the
// sealed cell lands on the host at a known path). The cell runs `cellinit stage1` (cell-init/Program.fs).
//
// Exit: 0 proven; 1 the capture is missing; 3 a prerequisite is missing; 4 the publish failed; 5 the cell failed.
#load "spike-common.fsx"
open System.IO
open SpikeCommon

let usage = "usage: dotnet fsi SageFs.Demos/spike/stage1-sandbox-capture.fsx -- [out-dir]   (default out-dir: /tmp/sagefs-demo-spike/cells/stage1/out)"

let code =
  main' usage (fun args ->
    let out = freshOut args "stage1"
    let capture = Path.Combine(out, "stage1.mkv")
    if File.Exists capture then File.Delete capture
    say "== Stage 1: sandbox + capture =="
    say (sprintf "Host out dir: %s" out)
    publishCellInit ()
    let cell = runCell "stage1" out [] None None
    say "== Host-side verification =="
    match cell with
    | 0 ->
      match checkNonEmpty capture with
      | true ->
        probeVideo capture
        true
      | false -> false
    | other -> fail (CellFailed other))

exit code
