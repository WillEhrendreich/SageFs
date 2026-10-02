// tools/agent-hooks/publish.fsx   build the Claude Code hook programs where settings.json can point at them
// Run with: dotnet fsi tools/agent-hooks/publish.fsx
//
// A PreToolUse hook runs before every Bash call, so the hook is a compiled program, not an .fsx: `dotnet fsi`
// costs about 1.3 seconds of startup per call, this costs about 15 ms (see README.md for the measurements).
// The program is published self-contained as a single ReadyToRun file straight into this folder as
// `sagefs-repl-guard` (`.exe` on Windows), the path settings.json names. Self-contained so the hook does not
// depend on DOTNET_ROOT being set in whatever environment Claude Code spawns it from.
//
// Every step is fatal on failure, with one exit code per kind.
open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices

// ── named values ─────────────────────────────────────────────────────────────

let hooksDir = __SOURCE_DIRECTORY__
let projectFile = Path.Combine(hooksDir, "SageFs.AgentHooks.fsproj")
let publishTimeout = TimeSpan.FromMinutes 10.

/// Why the script stopped. One exit code per kind, so a caller can tell them apart.
type Failure =
  | UnsupportedPlatform of os: string * arch: string
  | PublishFailed of output: string
  | NoExecutable of expected: string

let exitCodeOf = function
  | UnsupportedPlatform _ -> 2
  | PublishFailed _ -> 4
  | NoExecutable _ -> 5

let describe = function
  | UnsupportedPlatform (os, arch) -> sprintf "no runtime identifier for %s on %s" os arch
  | PublishFailed output -> "dotnet publish failed:\n" + output
  | NoExecutable path -> sprintf "dotnet publish succeeded but %s is not there" path

exception Stop of Failure
let fail f = raise (Stop f)
let say (s: string) = printfn "agent-hooks: %s" s

/// The .NET runtime identifier for this machine. RuntimeInformation.RuntimeIdentifier is the distro's own
/// (Omarchy says `omarchy.4.0.4-x64`), which no runtime pack is published for, so it is built from the parts.
let runtimeIdentifier () : string * string =
  let arch =
    match RuntimeInformation.OSArchitecture with
    | Architecture.X64 -> "x64"
    | Architecture.Arm64 -> "arm64"
    | other -> fail (UnsupportedPlatform (string RuntimeInformation.OSDescription, string other))
  match OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux() with
  | true, _, _ -> "win-" + arch, "sagefs-repl-guard.exe"
  | _, true, _ -> "osx-" + arch, "sagefs-repl-guard"
  | _, _, true -> "linux-" + arch, "sagefs-repl-guard"
  | _ -> fail (UnsupportedPlatform (RuntimeInformation.OSDescription, arch))

let run (file: string) (args: string list) : int * string =
  let psi = ProcessStartInfo(file)
  psi.WorkingDirectory <- hooksDir
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  args |> List.iter psi.ArgumentList.Add
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEndAsync()
  let err = p.StandardError.ReadToEndAsync()
  match p.WaitForExit publishTimeout with
  | true -> p.ExitCode, out.Result + err.Result
  | false ->
    (try p.Kill true with _ -> ())
    124, "timed out\n" + out.Result + err.Result

let code =
  try
    let rid, exeName = runtimeIdentifier ()
    say (sprintf "publishing %s for %s" exeName rid)
    let args =
      [ "publish"; projectFile; "-c"; "Release"; "-r"; rid
        "--self-contained"; "true"
        "-p:PublishSingleFile=true"; "-p:PublishReadyToRun=true"; "-p:DebugType=none"
        "--nologo"; "-v"; "q"; "-o"; hooksDir ]
    match run "dotnet" args with
    | 0, _ -> ()
    | _, output -> fail (PublishFailed output)
    let exe = Path.Combine(hooksDir, exeName)
    if not (File.Exists exe) then fail (NoExecutable exe)
    say (sprintf "wrote %s (%d MB)" exe (FileInfo(exe).Length / 1048576L))
    0
  with Stop f ->
    eprintfn "agent-hooks: %s" (describe f)
    exitCodeOf f

exit code
