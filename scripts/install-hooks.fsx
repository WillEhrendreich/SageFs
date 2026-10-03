// scripts/install-hooks.fsx   link the tracked git hooks into this clone's .git/hooks
// Run with: dotnet fsi scripts/install-hooks.fsx [-- --check]
//
//   (no argument)  install: .git/hooks/pre-push becomes a symlink to scripts/pre-push, so the hook always
//                  runs the tracked version and a `git pull` updates it. Idempotent.
//   --check        change nothing; exit 0 when the hook is installed and points at the tracked shim
//
// The hook refuses a push to master that has no gate pass or does not raise the version (see pre-push.fsx).
// All worktrees of a clone share one hooks directory, so this is done once per clone. The link is relative
// (`../../scripts/pre-push`), so the clone can move. If a different hook is already there it is left alone
// and reported: pass --force to replace it.
//
// Every step is fatal on failure, with one exit code per kind.
open System
open System.Diagnostics
open System.IO

// ── named values ─────────────────────────────────────────────────────────────

let gitTimeout = TimeSpan.FromSeconds 30.
let hookName = "pre-push"

type Mode =
  | Install of force: bool
  | Check

/// Why the script stopped. One exit code per kind, so a caller can tell them apart.
type Failure =
  | Usage of string
  | NotInGit
  | ShimMissing of path: string
  | OtherHookInTheWay of path: string
  | NotInstalled of path: string
  | LinkFailed of path: string * why: string

let exitCodeOf = function
  | Usage _ -> 64
  | NotInGit -> 9
  | ShimMissing _ -> 3
  | OtherHookInTheWay _ -> 5
  | NotInstalled _ -> 1
  | LinkFailed _ -> 4

let describe = function
  | Usage m -> m
  | NotInGit -> "this script is not inside a git checkout"
  | ShimMissing path -> sprintf "the tracked hook %s is missing; this checkout is older than the hook" path
  | OtherHookInTheWay path -> sprintf "%s is a different hook. Look at it, then rerun with --force to replace it." path
  | NotInstalled path -> sprintf "%s is not linked to the tracked hook; run: dotnet fsi scripts/install-hooks.fsx" path
  | LinkFailed (path, why) -> sprintf "could not link %s: %s" path why

exception Stop of Failure
let fail f = raise (Stop f)
let say (s: string) = printfn "install-hooks: %s" s

let parse (argv: string list) : Mode =
  match argv with
  | [] -> Install false
  | [ "--force" ] -> Install true
  | [ "--check" ] -> Check
  | other -> fail (Usage (sprintf "usage: install-hooks.fsx [--check | --force], not: %s" (String.Join(" ", other))))

// Locate the repo at RUNTIME, walking up from this script's own location until we
// find the solution file. A build-time constant would bake in the directory the
// script was COMPILED, which is not where it RUNS.
let repoRoot =
  let rec walk (dir: string) (depth: int) : string =
    if depth > 24 then "" else
    let full =
      try
        let f = Path.GetFullPath dir
        let r = Path.GetPathRoot f
        if f = r then f else Path.TrimEndingDirectorySeparator f
      with _ -> dir
    if File.Exists(Path.Combine(full, "SageFs.slnx")) then full
    else
      let parent = Path.GetDirectoryName full
      if String.IsNullOrEmpty parent || parent = full then ""
      else walk parent (depth + 1)
  // Start from the directory this script lives in, so the walk up finds the repo root at run time
  // rather than a build-time constant naming where it was compiled.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
// git needs a directory inside the checkout to run in; the repo root found by the walk is it.
let scriptsDir =
  if repoRoot = "" then fail NotInGit
  else Path.Combine(repoRoot, "scripts")

let git (args: string list) : string =
  let psi = ProcessStartInfo("git")
  psi.WorkingDirectory <- scriptsDir
  args |> List.iter psi.ArgumentList.Add
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEndAsync()
  let err = p.StandardError.ReadToEndAsync()
  match p.WaitForExit gitTimeout && p.ExitCode = 0 with
  | true -> out.Result.Trim()
  | false -> fail NotInGit

/// Where the hooks of this clone live (honouring core.hooksPath), as a full path.
let hooksDir () : string =
  Path.GetFullPath(git [ "rev-parse"; "--git-path"; "hooks" ], scriptsDir)

/// The tracked shim, taken from the main working tree when this is a linked worktree: the hooks directory is
/// shared by all worktrees, so it must not point into one that may be removed later.
let trackedShim () : string =
  let common = Path.GetFullPath(git [ "rev-parse"; "--git-common-dir" ], scriptsDir)
  let root =
    match Path.GetFileName(common.TrimEnd Path.DirectorySeparatorChar) with
    | ".git" -> Path.GetDirectoryName(common.TrimEnd Path.DirectorySeparatorChar)
    | _ -> git [ "rev-parse"; "--show-toplevel" ]
  Path.Combine(root, "scripts", hookName)

/// Whether the link at `hook` resolves to `shim`.
let pointsAt (hook: string) (shim: string) : bool =
  match FileInfo(hook).LinkTarget with
  | null -> false
  | target -> Path.GetFullPath(target, Path.GetDirectoryName hook) = shim

let ensureExecutable (path: string) : unit =
  if not (OperatingSystem.IsWindows()) then
    let mode = File.GetUnixFileMode path
    let wanted = mode ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute
    if wanted <> mode then File.SetUnixFileMode(path, wanted)

let run (mode: Mode) : int =
  let shim = trackedShim ()
  let dir = hooksDir ()
  let hook = Path.Combine(dir, hookName)
  match mode with
  | Check ->
    if not (pointsAt hook shim) then fail (NotInstalled hook)
    say (sprintf "%s is linked to %s" hook shim)
    0
  | Install force ->
    if not (File.Exists shim) then fail (ShimMissing shim)
    ensureExecutable shim
    match pointsAt hook shim, File.Exists hook || not (isNull (FileInfo(hook).LinkTarget)) with
    | true, _ -> say (sprintf "already installed: %s" hook)
    | false, true when not force -> fail (OtherHookInTheWay hook)
    | _ ->
      try
        Directory.CreateDirectory dir |> ignore
        File.Delete hook
        File.CreateSymbolicLink(hook, Path.GetRelativePath(dir, shim)) |> ignore
      with e -> fail (LinkFailed (hook, e.Message))
      say (sprintf "installed %s -> %s" hook (Path.GetRelativePath(dir, shim)))
    0

let argv = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

let code =
  try run (parse argv)
  with Stop f ->
    eprintfn "install-hooks: %s" (describe f)
    exitCodeOf f

exit code
