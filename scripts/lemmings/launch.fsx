// scripts/lemmings/launch.fsx -- #load'ed by the lemming entry points (run-lemming-cmd.fsx and friends).
//
// The harness is typed F# (LemScore, LemMatrix, LemRun). An entry point is a one-line script that
// names the tool and the command; this file builds the tool when it is stale and hands over to it, with
// the caller's own stdin, stdout, stderr and exit code. The build is the only thing done here, and it is
// done under a file lock, so two lemmings starting together build once.
//
//   dotnet fsi scripts/lemmings/run-lemming-cmd.fsx -- demoenv parse-seed stealth/space-bunny-alpha auto 40
//
// Exit codes: the tool's own, or 4 when the tool will not build (the README's "build or toolchain is missing").
module Launch

open System
open System.Diagnostics
open System.IO
open System.Threading

// ── named values ─────────────────────────────────────────────────────────────

let lemDir = __SOURCE_DIRECTORY__
let toolchainMissingExit = 4
let buildLockPoll = TimeSpan.FromMilliseconds 250.
let buildLockPatience = TimeSpan.FromMinutes 15.
let lemRoot = match Environment.GetEnvironmentVariable "LEM_ROOT" with | null | "" -> "/tmp/lem" | r -> r

/// Why the launcher stopped. One exit code per kind.
type Failure =
  | WillNotBuild of tool: string * output: string

let exitCodeOf = function
  | WillNotBuild _ -> toolchainMissingExit

exception Stop of Failure

// ── building ─────────────────────────────────────────────────────────────────

let private sources (dir: string) : string list =
  match Directory.Exists dir with
  | true ->
    Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
    |> Array.filter (fun f -> f.EndsWith ".fs" || f.EndsWith ".fsproj")
    |> Array.toList
  | false -> []

/// The project directories a tool is built from: its own, and the ones it references. LemRun also
/// compiles the editor sandbox from the Neovim driver's directory, and LemMatrix runs LemRun.
let private projectsOf (tool: string) : string list =
  match tool with
  | "LemRun" -> [ "LemRun"; "LemScore" ]
  | "LemMatrix" -> [ "LemMatrix"; "LemRun"; "LemScore" ]
  | other -> [ other; "LemScore" ]

let private inputsOf (tool: string) : string list =
  (projectsOf tool |> List.collect (fun p -> sources (Path.Combine(lemDir, p))))
  @ [ Path.Combine(lemDir, "ui", "nvim", "EditorSandbox.fs") ]

let dllOf (tool: string) = Path.Combine(lemDir, tool, "bin", "Release", "net11.0", tool + ".dll")

let private isStale (tool: string) : bool =
  let dll = dllOf tool
  match File.Exists dll with
  | false -> true
  | true ->
    let built = File.GetLastWriteTimeUtc dll
    inputsOf tool |> List.exists (fun f -> File.Exists f && File.GetLastWriteTimeUtc f > built)

let private withBuildLock (work: unit -> 'a) : 'a =
  Directory.CreateDirectory lemRoot |> ignore
  let lockFile = Path.Combine(lemRoot, ".lemscore-build.lock")
  let deadline = DateTime.UtcNow + buildLockPatience
  let rec acquire () =
    try new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
    with :? IOException when DateTime.UtcNow < deadline ->
      Thread.Sleep buildLockPoll
      acquire ()
  use _held = acquire ()
  work ()

/// Builds the tool when its dll is missing or older than one of its sources (or LemScore's).
let ensureBuilt (tool: string) : string =
  match isStale tool with
  | false -> dllOf tool
  | true ->
    withBuildLock (fun () ->
      // Another lemming may have built it while this one waited for the lock.
      match isStale tool with
      | false -> ()
      | true ->
        eprintfn "lem: building %s" tool
        let psi = ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
        [ "build"; Path.Combine(lemDir, tool, tool + ".fsproj"); "-c"; "Release"; "-nologo"; "-v"; "quiet" ] |> List.iter psi.ArgumentList.Add
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        if p.ExitCode <> 0 then raise (Stop (WillNotBuild (tool, out.Result + err.Result))))
    dllOf tool

// ── handing over ─────────────────────────────────────────────────────────────

/// The caller's own arguments: everything after the script, without fsi's `--` separators.
let callerArgs : string list =
  fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

/// Runs `dotnet <tool>.dll <command...> <args...>` with this process's stdio, and exits with its code.
let run (tool: string) (command: string list) : 'a =
  let code =
    try
      let dll = ensureBuilt tool
      let psi = ProcessStartInfo("dotnet", UseShellExecute = false)
      psi.Environment["DOTNET_NOLOGO"] <- "1"
      (dll :: command) @ callerArgs |> List.iter psi.ArgumentList.Add
      use p = Process.Start psi
      p.WaitForExit()
      p.ExitCode
    with Stop f ->
      match f with
      | WillNotBuild (name, output) ->
        eprintfn "%s" output
        eprintfn "lem: %s failed to build" name
      exitCodeOf f
  exit code
