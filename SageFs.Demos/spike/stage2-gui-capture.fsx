// SageFs.Demos/spike/stage2-gui-capture.fsx [out-dir]
// Run with: dotnet fsi SageFs.Demos/spike/stage2-gui-capture.fsx -- [out-dir]
//
// Phase-0 Stage 2: real headed Chromium inside the cell, rendering a local file:// page, captured to
// /out/stage2.mkv, with a pixel-level proof (an in-cell X11 screenshot, ImageMagick `import`) that the page
// actually painted rather than trusting the video by eye. The cell runs `cellinit stage2`.
//
// Exit: 0 proven; 1 the capture or the pixel is wrong; 3 a prerequisite is missing; 4 the publish failed; 5 the cell failed.
#load "spike-common.fsx"
open System.IO
open SpikeCommon

/// The button occupies (40,40)-(440,240), centre (240,140), rendered rgb(0,200,60); the background elsewhere is
/// rgb(16,24,32) (#101820).
let buttonCentre = "240,140"
let expectedColour = "srgb(0,200,60)"

let usage = "usage: dotnet fsi SageFs.Demos/spike/stage2-gui-capture.fsx -- [out-dir]   (default out-dir: /tmp/sagefs-demo-spike/cells/stage2/out)"

let code =
  main' usage (fun args ->
    let out = freshOut args "stage2"
    let video, shot = Path.Combine(out, "stage2.mkv"), Path.Combine(out, "stage2.png")
    for f in [ video; shot ] do if File.Exists f then File.Delete f
    if not (File.Exists(Path.Combine(chromeDir, "chrome"))) then fail (ChromiumMissing chromeDir)
    say "== Stage 2: GUI in the cell + capture =="
    say (sprintf "Host out dir: %s" out)
    say (sprintf "Chrome binary: %s" (Path.Combine(chromeDir, "chrome")))
    publishCellInit ()
    let cell = runCell "stage2" out [ (chromeDir, "/chrome"); (Path.Combine(spikeDir, "fixtures"), "/work") ] None None
    say "== Host-side verification =="
    if cell <> 0 then fail (CellFailed cell)
    let videoOk = checkNonEmpty video
    let shotOk = checkNonEmpty shot
    let pixelOk =
      match shotOk with
      | false -> false
      | true ->
        let c = run "convert" [ shot; "-format"; sprintf "%%[pixel:p{%s}]" buttonCentre; "info:" ]
        let pixel = c.Stdout.Trim()
        say (sprintf "Pixel at button centre (%s): %s" buttonCentre pixel)
        match pixel.Contains expectedColour || pixel.Contains "srgba(0,200,60" with
        | true ->
          say "PASS: button colour rendered exactly as authored"
          true
        | false ->
          say "FAIL: unexpected pixel colour, page did not render as expected"
          false
    videoOk && shotOk && pixelOk)

exit code
