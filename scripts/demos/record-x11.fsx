// scripts/demos/record-x11.fsx -- generic headless X11 screen recorder -> GIF.
// Run with: dotnet fsi scripts/demos/record-x11.fsx -- --output <path.gif> --command '<launch+drive command>' [options]
//
// Starts a virtual X display (Xvfb), runs a caller-supplied "drive" command under that display (the command
// is responsible for launching and driving whatever should be on screen: an editor, a browser, a terminal
// emulator, anything that opens an X11 window), records the display with ffmpeg's x11grab for a fixed
// duration, and converts the recording to an animated GIF using ffmpeg's two-pass palette filter.
//
// This script knows nothing about SageFs. It is a reusable building block: callers pass in the command
// that drives whatever they want recorded.
//
//   dotnet fsi scripts/demos/record-x11.fsx -- --display :99 --size 1280x800 --duration 5 --output /tmp/demo.gif \
//     --command 'alacritty -e sh -c "echo hello; sleep 10"'
//
// --command is split into words the way a shell would split a plain command (quotes group, a backslash
// escapes), and run directly. A pipeline needs `sh -c "..."` named in it.
//
// Required:  --output PATH   where to write the final .gif
//            --command CMD   what to run under the virtual display. Recording starts right after the launch
//                            and runs for --duration seconds; the command is not waited for.
// Options:   --display DISP  X display number to use (default :99)
//            --size WxH      virtual screen size (default 1280x800)
//            --framerate N   ffmpeg capture framerate (default 15)
//            --duration S    how long to record (default 5)
//            --gif-fps N     output gif frame rate (default 12)
//            --gif-width N   output gif width in px, height auto (default 900)
//            --keep-workdir  do not delete the intermediate mp4/palette files
//
// Requires: Xvfb, ffmpeg (with x11grab + libx264), ffprobe, setsid.
// Exit: 0 done; 64 bad arguments; 3 a tool is missing; 4 Xvfb or the drive command would not start;
//       5 a capture or encode step failed.
#load "demo-common.fsx"
open System
open System.IO
open DemoCommon

let script = "record-x11"

// ── named values ─────────────────────────────────────────────────────────────

let defaultDisplay = ":99"
let defaultSize = "1280x800"
let defaultFramerate = 15
let defaultDuration = 5
let defaultGifFps = 12
let defaultGifWidth = 900
let colourDepth = 24
let xvfbSocketAttempts = 20
/// Time for the driven app to paint a window before the grab starts.
let settleBeforeGrab = TimeSpan.FromMilliseconds 500.
let x11SocketDir = "/tmp/.X11-unix"

let spec : ArgSpec =
  { Valued = [ "--display"; "--size"; "--framerate"; "--duration"; "--output"; "--command"; "--gif-fps"; "--gif-width" ]
    Switches = [ "--keep-workdir" ] }

let usage = "usage: record-x11.fsx --output <path.gif> --command '<launch+drive command>' [--display :99] [--size WxH] [--framerate N] [--duration S] [--gif-fps N] [--gif-width N] [--keep-workdir]"

/// What ffprobe says about the finished gif.
type GifFacts = { Dims: string; Frames: string; Bytes: int64 }

let probe (gif: string) : GifFacts =
  let ask (entries: string) (format: string) =
    let c = run "ffprobe" [ "-v"; "error"; "-select_streams"; "v"; "-show_entries"; entries; "-of"; format; gif ]
    if c.ExitCode = 0 then c.Stdout.Trim() else "unknown"
  { Dims = ask "stream=width,height" "csv=p=0:s=x"
    Frames = ask "stream=nb_frames" "default=noprint_wrappers=1:nokey=1"
    Bytes = FileInfo(gif).Length }

/// One ffmpeg step; a failure names the log it wrote.
let ffmpeg (what: string) (args: string list) (logFile: string) : unit =
  let c = run "ffmpeg" ([ "-y" ] @ args)
  File.WriteAllText(logFile, c.Stdout + c.Stderr)
  if c.ExitCode <> 0 then fail (CaptureFailed (sprintf "%s, see %s" what logFile))

let record (p: Parsed) : int =
  let output = required p "--output"
  let command = required p "--command"
  let display = value p "--display" defaultDisplay
  let size = value p "--size" defaultSize
  let framerate = intValue p "--framerate" defaultFramerate
  let duration = intValue p "--duration" defaultDuration
  let gifFps = intValue p "--gif-fps" defaultGifFps
  let gifWidth = intValue p "--gif-width" defaultGifWidth
  let keep = p.On.Contains "--keep-workdir"
  requireTools [ "Xvfb"; "ffmpeg"; "ffprobe"; "setsid" ]
  let work = Path.Combine(Path.GetTempPath(), "record-x11." + Guid.NewGuid().ToString("N").Substring(0, 6))
  Directory.CreateDirectory work |> ignore
  let mp4, palette = Path.Combine(work, "capture.mp4"), Path.Combine(work, "palette.png")
  let mutable xvfb : Started option = None
  let mutable drive : Started option = None
  try
    log script (sprintf "starting Xvfb %s -screen 0 %sx%d" display size colourDepth)
    xvfb <- Some (start "Xvfb" [ display; "-screen"; "0"; sprintf "%sx%d" size colourDepth ] None [] [] false (Path.Combine(work, "xvfb.log")))
    // Give Xvfb a moment to create its socket; poll instead of a blind sleep.
    let socket = Path.Combine(x11SocketDir, "X" + display.TrimStart ':')
    waitUntil (TimeSpan.FromMilliseconds (float xvfbSocketAttempts * pollEvery.TotalMilliseconds)) (fun () -> File.Exists socket) |> ignore
    if not (alive xvfb.Value.Process) then fail (WouldNotStart (sprintf "Xvfb exited immediately, see %s" (Path.Combine(work, "xvfb.log"))))
    log script (sprintf "launching drive command under DISPLAY=%s: %s" display command)
    // WAYLAND_DISPLAY is removed for the whole drive command. On a Wayland host, GUI toolkits (chromium,
    // Electron/VS Code) prefer the Wayland backend and ignore $DISPLAY, so they render on the user's REAL
    // screen. Hiding the Wayland socket forces them onto the Xvfb display (or to fail cleanly); they can
    // never reach the real compositor. `setsid` makes the command its own process group, so ending the
    // group takes the windows it spawned with it.
    match splitCommand command with
    | [] -> fail (Usage "--command is empty")
    | exe :: args -> drive <- Some (start exe args None [ ("DISPLAY", display) ] [ "WAYLAND_DISPLAY" ] true (Path.Combine(work, "drive.log")))
    Threading.Thread.Sleep settleBeforeGrab
    log script (sprintf "recording %ds at %dfps from %s" duration framerate display)
    ffmpeg "ffmpeg capture failed" [ "-f"; "x11grab"; "-video_size"; size; "-framerate"; string framerate; "-i"; display; "-t"; string duration; mp4 ] (Path.Combine(work, "ffmpeg-record.log"))
    if not (File.Exists mp4 && FileInfo(mp4).Length > 0L) then fail (CaptureFailed "capture produced an empty file")
    let filters = sprintf "fps=%d,scale=%d:-1:flags=lanczos" gifFps gifWidth
    log script "generating palette"
    ffmpeg "palettegen failed" [ "-i"; mp4; "-update"; "1"; "-frames:v"; "1"; "-vf"; filters + ",palettegen"; palette ] (Path.Combine(work, "ffmpeg-palette.log"))
    log script (sprintf "encoding gif -> %s" output)
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath output) |> Option.ofObj |> Option.defaultValue ".") |> ignore
    ffmpeg "paletteuse failed" [ "-i"; mp4; "-i"; palette; "-lavfi"; filters + ",paletteuse"; output ] (Path.Combine(work, "ffmpeg-gif.log"))
    if not (File.Exists output && FileInfo(output).Length > 0L) then fail (CaptureFailed "gif encode produced an empty file")
    let facts = probe output
    log script (sprintf "done: %s (%s, %s frames, %d bytes)" output facts.Dims facts.Frames facts.Bytes)
    0
  finally
    drive |> Option.iter (fun d ->
      if alive d.Process then log script (sprintf "stopping drive command and its children (pgid %d)" d.Process.Id)
      killGroup d.Process)
    xvfb |> Option.iter (fun x ->
      if alive x.Process then log script (sprintf "stopping Xvfb (pid %d)" x.Process.Id)
      terminate x.Process)
    if keep then log script (sprintf "workdir kept at %s" work) else try Directory.Delete(work, true) with _ -> ()

let code =
  try
    let p = parseArgs spec (scriptArgs ())
    match p.Help with
    | true ->
      printfn "%s" usage
      0
    | false -> record p
  with Stop f ->
    eprintfn "[%s] ERROR: %s" script (describe f)
    (match f with Usage _ -> eprintfn "%s" usage | _ -> ())
    exitCodeOf f

exit code
