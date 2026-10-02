// SageFs.Demos/spike/spike-common.fsx -- #load'ed by the stage scripts; not run on its own.
//
// What the four Phase-0 stage scripts share: the sealed cell (the bubblewrap recipe is SageFs.Demos/Sandbox.fs's
// `args`, pure data, loaded here rather than copied), the self-contained publish of what runs inside it, and
// the host-side checks of what the cell left in its /out. Everything inside the cell is `cellinit`
// (cell-init/Program.fs), published self-contained, because the cell holds only /usr and what is bound in.
module SpikeCommon

#load "../Sandbox.fs"
open System
open System.Diagnostics
open System.IO
open SageFs.Demos

// ── named values ─────────────────────────────────────────────────────────────

let spikeDir = __SOURCE_DIRECTORY__
let repoRoot = Path.GetFullPath(Path.Combine(spikeDir, "..", ".."))
let workRoot = "/tmp/sagefs-demo-spike"
let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
/// The Playwright Chromium the spike ran against.
let chromeDir = Path.Combine(home, ".cache", "ms-playwright", "chromium-1208", "chrome-linux64")
let cellInitBuild = Path.Combine(workRoot, "cell-init-build")
let cellTimeout = TimeSpan.FromMinutes 3.
let cellEnv =
  [ ("HOME", "/home/demo"); ("PATH", "/usr/bin"); ("LIBGL_ALWAYS_SOFTWARE", "1")
    ("__EGL_VENDOR_LIBRARY_FILENAMES", "/usr/share/glvnd/egl_vendor.d/50_mesa.json") ]

/// Why a stage stopped. One exit code per kind.
type Failure =
  | ChromiumMissing of path: string
  | SageFsNotBuilt of path: string
  | PublishFailed of project: string * log: string
  | CellFailed of exitCode: int
  | NotProven of what: string

let exitCodeOf = function
  | ChromiumMissing _ | SageFsNotBuilt _ -> 3
  | PublishFailed _ -> 4
  | CellFailed _ -> 5
  | NotProven _ -> 1

let describe = function
  | ChromiumMissing p -> sprintf "bundled Playwright chromium not found at %s" p
  | SageFsNotBuilt p -> sprintf "SageFs.dll not found at %s - run: dotnet build %s/SageFs/SageFs.fsproj -c Release (or set SAGEFS_BIN_DIR)" p repoRoot
  | PublishFailed (project, log) -> sprintf "publish of %s failed, see %s" project log
  | CellFailed code -> sprintf "the cell exited %d" code
  | NotProven what -> what

exception Stop of Failure
let fail (f: Failure) = raise (Stop f)

let say (s: string) = printfn "%s" s

// ── processes ────────────────────────────────────────────────────────────────

type Captured = { ExitCode: int; Stdout: string; Stderr: string }

let run (file: string) (args: string list) : Captured =
  let psi = ProcessStartInfo(file, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
  args |> List.iter psi.ArgumentList.Add
  try
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    { ExitCode = p.ExitCode; Stdout = out.Result; Stderr = err.Result }
  with ex -> { ExitCode = 127; Stdout = ""; Stderr = ex.Message }

/// Publishes a project self-contained (so the cell needs no dotnet SDK) into `output`, with its log beside it.
/// NOTE: -p:PublishSingleFile=true produces a binary that SIGABRTs on startup with FileNotFoundException:
/// FSharp.Core (the single-file bundler drops it under this SDK/RID combination), so it is a plain
/// self-contained folder publish.
let publish (project: string) (output: string) (logFile: string) : unit =
  if Directory.Exists output then Directory.Delete(output, true)
  let c = run "dotnet" [ "publish"; project; "-c"; "Release"; "-r"; "linux-x64"; "--self-contained"; "true"; "-o"; output ]
  File.WriteAllText(logFile, c.Stdout + c.Stderr)
  if c.ExitCode <> 0 then fail (PublishFailed (project, logFile))

let publishCellInit () : unit =
  Directory.CreateDirectory workRoot |> ignore
  say "Building the self-contained cell supervisor (outside the cell; dotnet is not bound into it)..."
  publish (Path.Combine(spikeDir, "cell-init", "CellInit.fsproj")) cellInitBuild (Path.Combine(workRoot, "cellinit-build.log"))

// ── the cell ─────────────────────────────────────────────────────────────────

/// Runs `/cellinit/cellinit <stage>` in a sealed cell with `out` as its only writable mount (the data plane).
/// `stdinFile` is piped to the cell's stdin and `stdoutFile` receives its stdout (the control plane, stage 4).
let runCell (stage: string) (out: string) (binds: (string * string) list) (stdinFile: string option) (stdoutFile: string option) : int =
  let spec : Sandbox.CellSpec =
    { RoBinds = [ ("/etc/fonts", "/etc/fonts"); (cellInitBuild, "/cellinit") ] @ binds
      RwBinds = [ (out, "/out") ]
      Env = cellEnv
      InnerCommand = [ "/cellinit/cellinit"; stage ] }
  let psi = ProcessStartInfo("bwrap", UseShellExecute = false, RedirectStandardInput = stdinFile.IsSome, RedirectStandardOutput = true, RedirectStandardError = true)
  Sandbox.args spec |> List.iter psi.ArgumentList.Add
  use p = Process.Start psi
  // The bwrap lifecycle is blocked on THIS thread (see Sandbox.run for why: --die-with-parent is tied to the forking thread).
  let stdout = p.StandardOutput.ReadToEndAsync()
  let stderr = p.StandardError.ReadToEndAsync()
  match stdinFile with
  | Some f ->
    p.StandardInput.Write(File.ReadAllText f)
    p.StandardInput.Close()
  | None -> ()
  p.WaitForExit()
  stdoutFile |> Option.iter (fun f -> File.WriteAllText(f, stdout.Result))
  eprintf "%s" stderr.Result
  if stdoutFile.IsNone then printf "%s" stdout.Result
  p.ExitCode

// ── what the cell left on the host ──────────────────────────────────────────

let fileBytes (path: string) : int64 = if File.Exists path then FileInfo(path).Length else 0L

/// PASS/FAIL line for a non-empty file; true when it is there.
let checkNonEmpty (path: string) : bool =
  match fileBytes path with
  | 0L ->
    say (sprintf "FAIL: %s missing or empty" path)
    false
  | n ->
    say (sprintf "PASS: %s exists, size=%d bytes" path n)
    true

let probeVideo (path: string) : unit =
  let c = run "ffprobe" [ "-v"; "error"; "-show_entries"; "format=duration,size"; "-show_entries"; "stream=codec_type,width,height"; "-of"; "default=noprint_wrappers=0"; path ]
  say (c.Stdout.TrimEnd())

let freshOut (args: string list) (stage: string) : string =
  let out = match args with first :: _ -> first | [] -> Path.Combine(workRoot, "cells", stage, "out")
  Directory.CreateDirectory out |> ignore
  out

/// Runs a stage body and turns a failure into its exit code. `-h` and `--help` print the usage and stop.
let main' (usage: string) (body: string list -> bool) : int =
  let args = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")
  match args |> List.exists (fun a -> a = "-h" || a = "--help") with
  | true ->
    printfn "%s" usage
    0
  | false ->
    try
      match body args with
      | true -> 0
      | false -> 1
    with Stop f ->
      eprintfn "FAIL: %s" (describe f)
      exitCodeOf f
