/// cellinit: what runs INSIDE the bubblewrap cell of the Phase-0 spike stages, as PID 1 of the cell.
///
/// The spike's host-side scripts used to hand the cell an inline shell script. A cell holds only /usr and
/// what is bound into it, so the supervisor has to be a program the cell can run: this one, published
/// self-contained. Each stage is one command: it starts Xvfb on a private display, does the stage's work,
/// and tears everything down (SIGTERM, a short wait, SIGKILL only for what refuses to go).
///
///   cellinit stage1   Xvfb + ffmpeg x11grab to /out/stage1.mkv
///   cellinit stage2   Xvfb + a real headed Chromium on a file:// page, captured to /out/stage2.mkv, with an
///                     in-cell X11 screenshot (/out/stage2.png) as the pixel-level proof it painted
///   cellinit stage3   Xvfb + ffmpeg in the background, and /app/xtestapp (libXtst fake input)
///   cellinit stage4   Xvfb + a SageFs daemon on a private port + ffmpeg, and /cellagent-bin/cellagent
///                     reading its ScenarioPlan on this process's stdin and writing its StepLog to stdout
///
/// Exit codes: 0 done; 1 the stage's own failure (Xvfb never came up, the daemon never became healthy, the
/// browser died); for stage3 and stage4 the app's own exit code.
module CellInit.Program

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Threading

// ---- named values -------------------------------------------------------------------------------

let display = ":99"
let screenSize = "1280x720"
let colourDepth = 24
let xSocketDir = "/tmp/.X11-unix"
let xSocket = "/tmp/.X11-unix/X99"
let framerate = 15
let pollEvery = TimeSpan.FromMilliseconds 100.
let xvfbSocketWait = TimeSpan.FromSeconds 5.
/// After SIGTERM, how long a process gets before SIGKILL.
let termWait = TimeSpan.FromSeconds 2.
let daemonTermWait = TimeSpan.FromSeconds 4.
let chromiumPaintWait = TimeSpan.FromSeconds 10.
let daemonPort = 47749
let daemonHealthWait = TimeSpan.FromSeconds 30.
let daemonHealthPoll = TimeSpan.FromMilliseconds 500.
let captureSeconds = 2
let stage3Seconds = 4
let stage4Seconds = 6
let stageFailed = 1

[<DllImport("libc", EntryPoint = "kill")>]
extern int private sysKill(int pid, int signal)

let say (message: string) = eprintfn "CELL: %s" message

let waitUntil (timeout: TimeSpan) (condition: unit -> bool) : bool =
  let deadline = DateTime.UtcNow + timeout
  let mutable ok = condition ()
  while not ok && DateTime.UtcNow < deadline do
    Thread.Sleep pollEvery
    ok <- condition ()
  ok

/// Starts a child with its stdin closed (the cell's own stdin carries the stage 4 ScenarioPlan, and a child that
/// read it would swallow it) and its stdout and stderr in files when asked, so nothing but the stage's own
/// output reaches the boundary.
let start (file: string) (args: string list) (env: (string * string) list) (stdout: string option) (stderr: string option) : Process =
  let psi = ProcessStartInfo(file, UseShellExecute = false)
  args |> List.iter psi.ArgumentList.Add
  env |> List.iter (fun (k, v) -> psi.Environment[k] <- v)
  psi.RedirectStandardInput <- true
  psi.RedirectStandardOutput <- stdout.IsSome
  psi.RedirectStandardError <- stderr.IsSome
  let p = Process.Start psi
  p.StandardInput.Close()
  let pump (reader: StreamReader) (path: string) =
    let sink = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)
    reader.BaseStream.CopyToAsync(sink).ContinueWith(fun (_: Threading.Tasks.Task) -> sink.Dispose()) |> ignore
  stdout |> Option.iter (fun path -> pump p.StandardOutput path)
  stderr |> Option.iter (fun path -> pump p.StandardError path)
  p

/// SIGTERM, a wait, SIGKILL only if it refuses to die.
let stop (p: Process) (wait: TimeSpan) : unit =
  if not p.HasExited then
    sysKill (p.Id, 15) |> ignore
    if not (waitUntil wait (fun () -> p.HasExited)) then sysKill (p.Id, 9) |> ignore

/// Xvfb refuses to mkdir /tmp/.X11-unix itself unless euid is 0 (it is not, inside an unprivileged userns
/// mapping our own uid), so it is made here with the standard X11 socket-dir mode; Xvfb then only has to
/// create the socket file inside it.
let startXvfb (hideCursor: bool) : Result<Process, string> =
  Directory.CreateDirectory xSocketDir |> ignore
  File.SetUnixFileMode(xSocketDir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute ||| UnixFileMode.StickyBit)
  let xvfb = start "Xvfb" ([ display; "-screen"; "0"; sprintf "%sx%d" screenSize colourDepth ] @ (if hideCursor then [ "-nocursor" ] else [])) [] (Some "/tmp/xvfb.log") (Some "/tmp/xvfb.err")
  match waitUntil xvfbSocketWait (fun () -> File.Exists xSocket) with
  | true -> Ok xvfb
  | false ->
    stop xvfb termWait
    Error "Xvfb did not create its socket in time"

let capture (file: string) (seconds: int) : Process =
  start "ffmpeg"
    [ "-y"; "-f"; "x11grab"; "-framerate"; string framerate; "-video_size"; screenSize; "-i"; display; "-t"; string seconds
      "-c:v"; "libx264"; "-preset"; "ultrafast"; "-qp"; "0"; file ]
    [ ("DISPLAY", display) ] None (Some "/tmp/ffmpeg.log")

let withXvfb (hideCursor: bool) (stage: Process -> int) : int =
  match startXvfb hideCursor with
  | Error why ->
    say why
    stageFailed
  | Ok xvfb ->
    try stage xvfb
    finally stop xvfb termWait

// ---- the stages -----------------------------------------------------------------------------------

let stage1 () : int =
  withXvfb true (fun _ ->
    use id' = Process.Start(ProcessStartInfo("id", UseShellExecute = false))
    id'.WaitForExit()
    say "Xvfb up, capturing 2s to /out/stage1.mkv"
    say "X11 socket confirmed present"
    let ffmpeg = capture "/out/stage1.mkv" captureSeconds
    ffmpeg.WaitForExit()
    say (sprintf "ffmpeg exit=%d file=%s" ffmpeg.ExitCode (if File.Exists "/out/stage1.mkv" then sprintf "%d bytes" (FileInfo("/out/stage1.mkv").Length) else "missing"))
    0)

let stage2 () : int =
  withXvfb true (fun _ ->
    Directory.CreateDirectory "/home/demo/chrome-profile" |> ignore
    let chrome =
      start "/chrome/chrome"
        [ "--no-sandbox"; "--disable-gpu"; "--ozone-platform=x11"; "--window-position=0,0"; "--window-size=1280,720"
          "--user-data-dir=/home/demo/chrome-profile"; "--no-first-run"; "--disable-features=Translate"; "--disable-extensions"
          "--disable-background-networking"; "--disable-sync"; "--disable-default-apps"; "--disable-infobars"; "--no-default-browser-check"
          "--app=file:///work/stage2.html" ]
        [ ("DISPLAY", display); ("HOME", "/home/demo") ] (Some "/tmp/chrome.log") (Some "/tmp/chrome.err")
    // A bounded wait for the window to actually map: the renderer gets a fixed ceiling to paint under llvmpipe,
    // and a browser that exits early is a failure with its log.
    let died = waitUntil chromiumPaintWait (fun () -> chrome.HasExited)
    match died with
    | true ->
      say "chromium exited early, log:"
      eprintfn "%s" (try File.ReadAllText "/tmp/chrome.log" + File.ReadAllText "/tmp/chrome.err" with _ -> "")
      stageFailed
    | false ->
      say "capturing 2s to /out/stage2.mkv"
      let ffmpeg = capture "/out/stage2.mkv" captureSeconds
      ffmpeg.WaitForExit()
      say "taking pixel-proof screenshot to /out/stage2.png"
      use shot = Process.Start(ProcessStartInfo("import", [ "-window"; "root"; "-display"; display; "/out/stage2.png" ], UseShellExecute = false))
      shot.WaitForExit()
      stop chrome termWait
      say "done"
      0)

let stage3 () : int =
  withXvfb true (fun _ ->
    // Recording the whole exchange is evidence, not part of the verdict: that comes from the app's own PASS/FAIL line.
    let ffmpeg = capture "/out/stage3.mkv" stage3Seconds
    let app =
      start "/app/xtestapp" [ "/chrome/chrome"; "file:///work/stage2.html"; "240"; "140"; "/home/demo/chrome-profile" ]
        [ ("DISPLAY", display); ("HOME", "/home/demo") ] (Some "/out/xtestapp.log") (Some "/out/xtestapp.stderr.log")
    app.WaitForExit()
    ffmpeg.WaitForExit()
    say (sprintf "xtestapp exit=%d" app.ExitCode)
    app.ExitCode)

let private http = lazy (new HttpClient(Timeout = TimeSpan.FromSeconds 1.))

let stage4 () : int =
  withXvfb true (fun _ ->
    Directory.CreateDirectory "/home/demo/.sagefs" |> ignore
    // The daemon runs on a private port in the cell's own network namespace (these ports cannot collide with the
    // host's real 37749/37750 daemon even without the offset, but the offset is kept anyway) with its own data dir.
    // Two flags changed in SageFs since the spike was first written, and a daemon that is refused does not start:
    // `--no-watch` is no longer accepted, and a daemon on a non-default port must say when to give up (`--ttl`).
    // Both are the current contract, so the stage runs again.
    let daemon =
      start "/dotnet-root/dotnet" [ "/sagefs-bin/SageFs.dll"; "--mcp-port"; string daemonPort; "--ttl"; "5m"; "--no-resume" ]
        [ ("DISPLAY", display); ("HOME", "/home/demo"); ("SAGEFS_DATA_DIR", "/home/demo/.sagefs"); ("SAGEFS_BIND_HOST", "127.0.0.1"); ("DOTNET_ROOT", "/dotnet-root") ]
        (Some "/out/daemon.log") (Some "/out/daemon.err")
    try
      say (sprintf "waiting for daemon health on :%d..." daemonPort)
      let healthy () =
        try http.Value.GetAsync(sprintf "http://127.0.0.1:%d/health" daemonPort).GetAwaiter().GetResult().IsSuccessStatusCode with _ -> false
      match waitUntil daemonHealthWait healthy with
      | false ->
        say "daemon never became healthy"
        eprintfn "%s" (try File.ReadAllText "/out/daemon.log" with _ -> "")
        stageFailed
      | true ->
        say "daemon healthy"
        let ffmpeg = capture "/out/stage4.mkv" stage4Seconds
        // THE BOUNDARY: the cell-agent reads the ScenarioPlan JSON from OUR OWN stdin (piped in by bwrap from the
        // host runner's stdin) and writes its StepLog JSON straight through our stdout. Nothing here inspects or
        // rewrites either: the process inherits both.
        let psi = ProcessStartInfo("/cellagent-bin/cellagent", UseShellExecute = false)
        psi.Environment["DISPLAY"] <- display
        psi.Environment["HOME"] <- "/home/demo"
        use agent = Process.Start psi
        agent.WaitForExit()
        ffmpeg.WaitForExit()
        agent.ExitCode
    finally
      stop daemon daemonTermWait)

[<EntryPoint>]
let main argv =
  match argv with
  | [| "stage1" |] -> stage1 ()
  | [| "stage2" |] -> stage2 ()
  | [| "stage3" |] -> stage3 ()
  | [| "stage4" |] -> stage4 ()
  | _ ->
    eprintfn "usage: cellinit stage1|stage2|stage3|stage4"
    2
