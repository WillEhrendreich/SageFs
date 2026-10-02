// scripts/record-demos.fsx [<tape>]   generate demo GIFs from the VHS tape files, using the vhs-fixed Docker image
// Run with: dotnet fsi scripts/record-demos.fsx [-- <tape>]
//
// Prerequisites:
//   - Docker running
//   - the vhs-fixed:latest Docker image (built from the djdarcy/vhs-windows-fixes fork)
//
//   (no argument)  record every docs/media/tapes/*.tape
//   <tape>         record one, by name without the extension: `hero` records docs/media/tapes/hero.tape
//
// Exit codes: 0 every tape recorded (or there were none); 1 Docker or the image is missing, the named tape is
// not there, or a tape failed (the last is new: the PowerShell version exited 0 whatever VHS did); 64 bad arguments.
open System
open System.Diagnostics
open System.IO

// ── named values ─────────────────────────────────────────────────────────────

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let mediaDir = Path.Combine(repoRoot, "docs", "media")
let tapesDir = Path.Combine(mediaDir, "tapes")
let imageName = "vhs-fixed"
let probeTimeout = TimeSpan.FromSeconds 30.
let recordTimeout = TimeSpan.FromMinutes 15.

/// Why the script stopped. One exit code per kind.
type Failure =
  | Usage of string
  | DockerMissing
  | ImageMissing
  | TapeNotFound of path: string
  | TapesFailed of count: int

let exitCodeOf = function
  | Usage _ -> 64
  | DockerMissing | ImageMissing | TapeNotFound _ | TapesFailed _ -> 1

let describe = function
  | Usage m -> m
  | DockerMissing -> "Docker is not installed or not in PATH."
  | ImageMissing ->
    sprintf "%s Docker image not found. Build it first (see docs/media/tapes/README.md for build instructions)" imageName
  | TapeNotFound path -> sprintf "Tape file not found: %s" path
  | TapesFailed n -> sprintf "%d tape(s) failed" n

exception Stop of Failure
let fail f = raise (Stop f)
let say (s: string) = printfn "%s" s

let run (file: string) (args: string list) (timeout: TimeSpan) (capture: bool) : int * string =
  try
    let psi = ProcessStartInfo(file)
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- capture
    psi.RedirectStandardError <- capture
    args |> List.iter psi.ArgumentList.Add
    use p = Process.Start psi
    let out = if capture then p.StandardOutput.ReadToEndAsync() else null
    let err = if capture then p.StandardError.ReadToEndAsync() else null
    match p.WaitForExit timeout with
    | true -> p.ExitCode, (if capture then out.Result + err.Result else "")
    | false ->
      (try p.Kill true with _ -> ())
      124, "timed out"
  with e -> 127, e.Message

let tapesToRecord (name: string option) : string list =
  match name with
  | Some n ->
    let path = Path.Combine(tapesDir, n + ".tape")
    if not (File.Exists path) then fail (TapeNotFound path)
    [ path ]
  | None ->
    match Directory.Exists tapesDir with
    | true -> Directory.GetFiles(tapesDir, "*.tape") |> Array.sort |> List.ofArray
    | false -> []

/// VHS tape files use LF line endings, so a CRLF checkout is rewritten before it is mounted into the container.
/// Returns the text as it is now.
let ensureLf (path: string) : string =
  let content = File.ReadAllText path
  let lf = content.Replace("\r\n", "\n")
  if content <> lf then
    File.WriteAllText(path, lf)
    say "    Fixed CR/LF to LF"
  lf

/// Where the GIF a tape writes ends up: the file the tape is named for, else the one its Output line names.
let producedGif (tapePath: string) (content: string) : string option =
  let named = Path.Combine(mediaDir, sprintf "sagefs-%s.gif" (Path.GetFileNameWithoutExtension tapePath))
  match File.Exists named with
  | true -> Some named
  | false ->
    content.Split('\n')
    |> Array.tryFind (fun l -> l.StartsWith "Output ")
    |> Option.map (fun l -> Path.Combine(mediaDir, l.Substring("Output ".Length).Trim()))
    |> Option.filter File.Exists

let record (tapePath: string) : bool =
  let name = Path.GetFileName tapePath
  say ""
  say (sprintf "  Recording: %s" name)
  let content = ensureLf tapePath
  // Run VHS in Docker, mounting the media directory.
  match run "docker" [ "run"; "--rm"; "-v"; sprintf "%s:/vhs" mediaDir; imageName; "/vhs/tapes/" + name ] recordTimeout false with
  | 0, _ ->
    match producedGif tapePath content with
    | Some gif -> say (sprintf "    ✓ Generated: %s (%s KB)" (Path.GetFileName gif) ((Math.Round(float (FileInfo(gif).Length) / 1024., 1)).ToString()))
    | None -> ()
    true
  | code, _ ->
    say (sprintf "    ✗ VHS failed with exit code %d" code)
    false

let argv = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

let code =
  try
    let name =
      match argv with
      | [] -> None
      | [ n ] -> Some n
      | other -> fail (Usage (sprintf "usage: record-demos.fsx [<tape>], not: %s" (String.Join(" ", other))))
    // Verify Docker is available and the image exists.
    if fst (run "docker" [ "--version" ] probeTimeout true) <> 0 then fail DockerMissing
    match run "docker" [ "images"; imageName; "--format"; "{{.Repository}}" ] probeTimeout true with
    | 0, output when output.Trim() = imageName -> ()
    | _ -> fail ImageMissing
    match tapesToRecord name with
    | [] ->
      say (sprintf "No tape files found in %s" tapesDir)
      0
    | tapes ->
      say (sprintf "Recording %d demo(s)..." tapes.Length)
      let failed = tapes |> List.filter (record >> not) |> List.length
      say ""
      say "Done."
      match failed with
      | 0 -> 0
      | n -> fail (TapesFailed n)
  with Stop f ->
    eprintfn "record-demos: %s" (describe f)
    exitCodeOf f

exit code
