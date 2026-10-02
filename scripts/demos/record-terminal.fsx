// scripts/demos/record-terminal.fsx -- generic terminal-session recorder -> GIF, no X11 needed.
// Run with: dotnet fsi scripts/demos/record-terminal.fsx -- --output <path.gif> --command '<command to record>' [options]
//
// Wraps `asciinema rec` (records a scripted terminal session as a .cast file) and `agg` (converts a .cast
// to an animated GIF). This is crisper and more deterministic than video capture for terminal-only demos
// (nvim, CLI tools) and avoids Xvfb/GPU/font fragility entirely.
//
// This script knows nothing about SageFs. It is a reusable building block: callers pass in the command to
// record (e.g. a script that drives nvim headlessly over RPC and prints progress, or any scripted CLI
// session).
//
//   dotnet fsi scripts/demos/record-terminal.fsx -- --command /path/to/scripted-session --output /tmp/demo.gif
//
// Required:  --output PATH   where to write the final .gif
//            --command CMD   what to record. It runs under `asciinema rec --command`, which hands the string
//                            to the shell, so it is passed through exactly as given. Prefer pointing it at a
//                            program rather than an inline one-liner with control flow.
// Options:   --cast-path PATH  where to write the intermediate .cast file (default a temp file, deleted
//                              unless --keep-cast)
//            --keep-cast       do not delete the intermediate .cast file
//
// Requires: asciinema, agg (asciinema-gif).
// Install (user-space, no sudo, via linuxbrew): brew install asciinema agg
// Install (Arch/Omarchy, system, needs sudo):   sudo pacman -S asciinema ; yay -S asciinema-agg-bin
// Exit: 0 done; 64 bad arguments; 3 a tool is missing; 5 the recording or the conversion failed.
#load "demo-common.fsx"
open System
open System.IO
open DemoCommon

let script = "record-terminal"

let spec : ArgSpec = { Valued = [ "--command"; "--output"; "--cast-path" ]; Switches = [ "--keep-cast" ] }

let usage = "usage: record-terminal.fsx --output <path.gif> --command '<command to record>' [--cast-path PATH] [--keep-cast]"

let record (p: Parsed) : int =
  let output = required p "--output"
  let command = required p "--command"
  let keep = p.On.Contains "--keep-cast"
  requireTools [ "asciinema"; "agg" ]
  let work, cast =
    match Map.tryFind "--cast-path" p.Values with
    | Some path -> None, path
    | None ->
      let dir = Path.Combine(Path.GetTempPath(), "record-terminal." + Guid.NewGuid().ToString("N").Substring(0, 6))
      Directory.CreateDirectory dir |> ignore
      Some dir, Path.Combine(dir, "session.cast")
  try
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath cast) |> Option.ofObj |> Option.defaultValue ".") |> ignore
    log script (sprintf "recording session -> %s" cast)
    // asciinema records the real terminal, so it keeps the harness's own stdio.
    let psi = Diagnostics.ProcessStartInfo("asciinema", UseShellExecute = false)
    [ "rec"; "--command"; command; "--overwrite"; cast ] |> List.iter psi.ArgumentList.Add
    use rec' = Diagnostics.Process.Start psi
    rec'.WaitForExit()
    if rec'.ExitCode <> 0 then fail (CaptureFailed "asciinema rec failed")
    if not (File.Exists cast && FileInfo(cast).Length > 0L) then fail (CaptureFailed "asciinema produced an empty cast file")
    log script (sprintf "converting cast -> gif: %s" output)
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath output) |> Option.ofObj |> Option.defaultValue ".") |> ignore
    let agg = run "agg" [ cast; output ]
    if agg.ExitCode <> 0 then fail (CaptureFailed "agg conversion failed")
    if not (File.Exists output && FileInfo(output).Length > 0L) then fail (CaptureFailed "gif encode produced an empty file")
    let facts =
      match onPath "ffprobe" with
      | Some _ ->
        let ask (entries: string) (format: string) =
          let c = run "ffprobe" [ "-v"; "error"; "-select_streams"; "v"; "-show_entries"; entries; "-of"; format; output ]
          if c.ExitCode = 0 then c.Stdout.Trim() else "unknown"
        ask "stream=width,height" "csv=p=0:s=x", ask "stream=nb_frames" "default=noprint_wrappers=1:nokey=1"
      | None -> "unknown", "unknown (ffprobe not found)"
    log script (sprintf "done: %s (%s, %s frames, %d bytes)" output (fst facts) (snd facts) (FileInfo(output).Length))
    0
  finally
    match work, keep with
    | Some dir, false -> try Directory.Delete(dir, true) with _ -> ()
    | _, true -> log script (sprintf "cast kept at %s" cast)
    | None, false -> ()

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
