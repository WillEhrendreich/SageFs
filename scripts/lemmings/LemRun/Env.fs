/// Where things are, and the knobs the environment turns. One place reads the environment, so a
/// variable that changes behaviour is named here with its default and what it is for.
module LemRun.Env

open System
open System.IO

/// A variable that is set and not empty (the bash `${NAME:-}` test).
let var (name: string) : string option =
  match Environment.GetEnvironmentVariable name with
  | null | "" -> None
  | v -> Some v

let isSet (name: string) : bool = (var name).IsSome

let varOr (name: string) (fallback: string) : string = var name |> Option.defaultValue fallback

let intVarOr (name: string) (fallback: int) : int =
  match var name |> Option.map Int32.TryParse with
  | Some (true, n) -> n
  | _ -> fallback

let home : string =
  match var "HOME" with
  | Some h -> h
  | None -> Environment.GetFolderPath Environment.SpecialFolder.UserProfile

// ---- the checkout -----------------------------------------------------------------------------

/// scripts/lemmings: the nearest directory above the binary that holds the fixtures and the tasks.
/// LEM_DIR overrides it (a test that runs from somewhere else). Empty when there is none, which the
/// entry point reports; reading it must never throw, because every module reads it when it loads.
let lemDir : string =
  let rec up (dir: DirectoryInfo | null) =
    match dir with
    | null -> ""
    | d when Directory.Exists(Path.Combine(d.FullName, "fixtures")) && Directory.Exists(Path.Combine(d.FullName, "tasks")) -> d.FullName
    | d -> up d.Parent
  match var "LEM_DIR" with
  | Some d -> d
  | None -> up (DirectoryInfo AppContext.BaseDirectory)

let repoRoot : string = Path.GetFullPath(Path.Combine(lemDir, "..", ".."))

/// The checkout that owns this worktree (`git rev-parse --git-common-dir`, one level up), which is
/// where the dev build of SageFs lives. Falls back to ~/Work/SageFs when git cannot say.
let mainCheckout () : string =
  let common = Proc.run (Proc.spec "git" [ "-C"; lemDir; "rev-parse"; "--path-format=absolute"; "--git-common-dir" ]) None
  match common.ExitCode, common.Stdout.Trim() with
  | 0, path when path <> "" -> (match Path.GetDirectoryName path with null -> Path.Combine(home, "Work", "SageFs") | d -> d)
  | _ -> Path.Combine(home, "Work", "SageFs")

// ---- run roots --------------------------------------------------------------------------------

/// The run root. Runs are named `<model-short>-<task>-<nn>` under it, and it is tmpfs on this
/// machine, so a run keeps only its evidence there (see Prune.slim).
let lemRoot : string = varOr "LEM_ROOT" "/tmp/lem"

/// Where the builds a run uses are kept once, off tmpfs: the SageFs build a lemming's MCP bridge
/// runs, and the editor driver. Read-only, keyed by what they are, shared by every run.
let storeRoot : string =
  match var "LEM_STORE" with
  | Some s -> s
  | None -> Path.Combine(home, ".local", "share", "sagefs-lemmings")

// ---- named durations and limits -----------------------------------------------------------------

module Limits =
  /// How long cmdc may run before `timeout(1)` ends it.
  let cmdcSeconds () = intVarOr "LEM_TIMEOUT_SECONDS" 1500
  /// After the limit, how long before `timeout(1)` sends KILL.
  let killAfterSeconds = 30
  /// How long the harness waits for the session watcher to see its stop file before ending it.
  let watcherStop = TimeSpan.FromSeconds 15.0
  /// The whole oracle, which includes the lemming's test run in the sandbox.
  let oracle = TimeSpan.FromSeconds 900.0
  /// A test suite run for an oracle.
  let suiteSeconds = 600
  /// The RingBuffer oracle's build-and-run.
  let smallSuiteSeconds = 300

module Names =
  /// The tag every run's working repository is born with, the state the lemming started from.
  let baselineTag = "lem-baseline"
