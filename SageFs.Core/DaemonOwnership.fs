module SageFs.DaemonOwnership

open System
open System.IO
open System.Text.Json

/// Ownership rule 2 (multi-agent vision §3.1): every externally-spawned
/// daemon gets an owner or a TTL. `sagefs --owner-pid <pid> [--owner-start
/// <ticks>]` binds the daemon's lifetime to another process (the agent,
/// test, or demo runner that spawned it); `sagefs --ttl 30m` self-terminates
/// an unowned daemon once it has had no live sessions and no MCP/SSE
/// clients for that long. Rule 3: `sagefs sweep [--kill]` reaps a daemon
/// IFF its recorded owner is gone — never by process name, never by PPID.

/// The real, un-overridden `~/.SageFs` — deliberately ignoring
/// `SAGEFS_DATA_DIR` (which isolates a single daemon's own session/manifest
/// state, e.g. for tests). The sweep registry lives at one fixed, canonical
/// location regardless of where any individual daemon's own data lives, so
/// `sagefs sweep` can find every registered daemon without scanning the
/// filesystem.
let realHomeSageFsDir () : string =
  let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
  Path.Combine(home, ".SageFs")

// ─── CLI parsing ──────────────────────────────────────────────────────────

/// Ownership flags as parsed straight off the CLI args — before the
/// nested-checkout default (`applyNestedCheckoutDefault`) is applied.
type OwnershipArgs = {
  OwnerPid: int option
  OwnerStartTicks: int64 option
  Ttl: TimeSpan option
}

module OwnershipArgs =
  let empty = { OwnerPid = None; OwnerStartTicks = None; Ttl = None }

  /// Parse a duration like "30m", "1s", "500ms", "2h", or a bare number of
  /// seconds. Unrecognized input is `None` (never throws — a malformed
  /// `--ttl` is treated the same as no `--ttl`, and the nested-checkout
  /// default below still applies where it would have).
  let tryParseTtl (raw: string) : TimeSpan option =
    match String.IsNullOrWhiteSpace raw with
    | true -> None
    | false ->
      let s = raw.Trim()
      let tryUnit (suffix: string) (toSpan: float -> TimeSpan) =
        match s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) with
        | true ->
          let numeric = s.Substring(0, s.Length - suffix.Length)
          match Double.TryParse(numeric, Globalization.CultureInfo.InvariantCulture) with
          | true, n when n >= 0.0 -> Some (toSpan n)
          | _ -> None
        | false -> None
      // Order matters: "ms" must be tried before "s" (every "Xms" also ends with "s").
      [ tryUnit "ms" TimeSpan.FromMilliseconds
        tryUnit "h" TimeSpan.FromHours
        tryUnit "m" TimeSpan.FromMinutes
        tryUnit "s" TimeSpan.FromSeconds ]
      |> List.tryPick id
      |> Option.orElseWith (fun () ->
        match Double.TryParse(s, Globalization.CultureInfo.InvariantCulture) with
        | true, n when n >= 0.0 -> Some (TimeSpan.FromSeconds n)
        | _ -> None)

  /// Parse `--owner-pid <pid>`, `--owner-start <ticks>`, `--ttl <duration>`
  /// out of a daemon's raw CLI args. Unknown/malformed values are ignored
  /// rather than raising — ownership is a safety net, not a strict parser.
  let parse (args: string list) : OwnershipArgs =
    let rec loop acc remaining =
      match remaining with
      | [] -> acc
      | "--owner-pid" :: v :: rest ->
        match Int32.TryParse v with
        | true, pid -> loop { acc with OwnerPid = Some pid } rest
        | false, _ -> loop acc rest
      | "--owner-start" :: v :: rest ->
        match Int64.TryParse v with
        | true, ticks -> loop { acc with OwnerStartTicks = Some ticks } rest
        | false, _ -> loop acc rest
      | "--ttl" :: v :: rest ->
        loop { acc with Ttl = tryParseTtl v } rest
      | _ :: rest -> loop acc rest
    loop empty args

// ─── Nested-checkout default ─────────────────────────────────────────────

/// Whether `dir` sits inside ANOTHER checkout: walking upward from its
/// PARENT (never `dir` itself — a worktree's own `.git` file marker does
/// not count) finds a directory with a checkout marker. True for a daemon
/// started inside `.claude/worktrees/agent-x`, which lives inside the main
/// checkout's own directory tree.
let isNestedCheckout (hasCheckoutMarker: string -> bool) (dir: string) : bool =
  let rec walk (d: string) =
    match d with
    | null | "" -> false
    | d when hasCheckoutMarker d -> true
    | d -> walk (Path.GetDirectoryName d)
  match Path.GetDirectoryName(Path.GetFullPath dir) with
  | null -> false
  | parent -> walk parent

/// The nested-checkout default: a daemon that starts inside another
/// checkout, with no explicit owner and no explicit TTL, would otherwise
/// run forever unowned — the exact shape of the leaked worktree-agent
/// daemons this closes (§2 seam S1). It defaults to a 30-minute TTL.
let defaultTtlForNestedCheckout = TimeSpan.FromMinutes 30.0

type EffectiveOwnership = {
  OwnerPid: int option
  OwnerStartTicks: int64 option
  Ttl: TimeSpan option
  /// True when the nested-checkout default supplied `Ttl` — callers use
  /// this to log loudly, as the spec requires.
  DefaultedTtl: bool
}

let applyNestedCheckoutDefault (isNested: bool) (args: OwnershipArgs) : EffectiveOwnership =
  match args.OwnerPid, args.Ttl, isNested with
  | None, None, true ->
    { OwnerPid = None
      OwnerStartTicks = None
      Ttl = Some defaultTtlForNestedCheckout
      DefaultedTtl = true }
  | _ ->
    { OwnerPid = args.OwnerPid
      OwnerStartTicks = args.OwnerStartTicks
      Ttl = args.Ttl
      DefaultedTtl = false }

// ─── TTL idle-check ───────────────────────────────────────────────────────

/// Whether a TTL-governed daemon should self-terminate right now: no live
/// sessions AND no MCP/SSE clients for at least `ttl` since `lastActiveAt`.
/// Pure — the caller supplies `now`, the observed activity flags, and the
/// timestamp of the last moment either flag was true.
let shouldSelfTerminate
  (now: DateTime)
  (ttl: TimeSpan)
  (lastActiveAt: DateTime)
  (hasLiveSessions: bool)
  (hasClients: bool)
  : bool =
  not hasLiveSessions && not hasClients && (now - lastActiveAt) >= ttl

// ─── Daemon-info file ─────────────────────────────────────────────────────

/// Written into a daemon's own `SAGEFS_DATA_DIR` at startup. The HTTP
/// `/api/daemon-info` endpoint (`Dashboard.fs`) already serves the live
/// equivalent over the network; this file is what a sweeper reads when the
/// HTTP side is wedged, and what `sagefs sweep` reads for daemons it never
/// talks to over HTTP at all.
type DaemonInfoFile = {
  Pid: int
  StartTime: DateTime
  OwnerPid: int option
  OwnerStart: int64 option
  McpPort: int
  DashboardPort: int
  DataDir: string
}

module DaemonInfoFile =
  let jsonOptions = JsonSerializerOptions(WriteIndented = true)

  let fileName = "daemon-info.json"

  /// Absolute path of the daemon-info file inside a data dir.
  let path (dataDir: string) : string = Path.Combine(dataDir, fileName)

  /// Serialize and write, atomically (write-then-move so a reader never
  /// observes a half-written file).
  let write (dataDir: string) (info: DaemonInfoFile) : unit =
    Directory.CreateDirectory(dataDir) |> ignore
    let target = path dataDir
    let tmp = target + ".tmp"
    File.WriteAllText(tmp, JsonSerializer.Serialize(info, jsonOptions))
    File.Move(tmp, target, true)

  let tryRead (dataDir: string) : DaemonInfoFile option =
    try
      let p = path dataDir
      match File.Exists p with
      | false -> None
      | true ->
        let info = JsonSerializer.Deserialize<DaemonInfoFile>(File.ReadAllText p, jsonOptions)
        match obj.ReferenceEquals(info, null) with
        | true -> None
        | false -> Some info
    with _ -> None

  let delete (dataDir: string) : unit =
    try
      let p = path dataDir
      match File.Exists p with
      | true -> File.Delete p
      | false -> ()
    with _ -> ()

// ─── The `~/.SageFs/spawned/` registry, for `sagefs sweep` ────────────────

/// Directory (under the REAL, un-overridden `~/.SageFs`, not a test's
/// isolated `SAGEFS_DATA_DIR`) that every externally-spawned daemon whose
/// own data dir differs registers a copy of its daemon-info into, so
/// `sagefs sweep` can find it without scanning the filesystem. Tests
/// register theirs here by design (spec §10 item 2) — it is how a leaked
/// test/agent daemon becomes reapable at all.
let registryDir (realSageFsDir: string) : string = Path.Combine(realSageFsDir, "spawned")

/// Register (or refresh) a spawned daemon's info in the shared registry.
/// A daemon whose own data dir already IS the real `~/.SageFs` needs no
/// registration — its own `daemon-info.json` is already found there.
let registerSpawned (realSageFsDir: string) (info: DaemonInfoFile) : unit =
  let dir = registryDir realSageFsDir
  Directory.CreateDirectory dir |> ignore
  let target = Path.Combine(dir, sprintf "%d.json" info.Pid)
  let tmp = target + ".tmp"
  File.WriteAllText(tmp, JsonSerializer.Serialize(info, DaemonInfoFile.jsonOptions))
  File.Move(tmp, target, true)

let unregisterSpawned (realSageFsDir: string) (pid: int) : unit =
  try
    let f = Path.Combine(registryDir realSageFsDir, sprintf "%d.json" pid)
    match File.Exists f with
    | true -> File.Delete f
    | false -> ()
  with _ -> ()

let private tryReadRegistryEntry (file: string) : DaemonInfoFile option =
  try
    let info = JsonSerializer.Deserialize<DaemonInfoFile>(File.ReadAllText file, DaemonInfoFile.jsonOptions)
    match obj.ReferenceEquals(info, null) with
    | true -> None
    | false -> Some info
  with _ -> None

/// Every daemon-info known on this machine: the primary daemon's own
/// (directly under `realSageFsDir`) plus every registered spawned daemon
/// (under `realSageFsDir/spawned/`). Returns the source file path alongside
/// each entry so a caller can delete/refresh it. Never touches process
/// ancestry or scans arbitrary directories.
let enumerateKnownDaemonInfos (realSageFsDir: string) : (string * DaemonInfoFile) list =
  let primary =
    DaemonInfoFile.tryRead realSageFsDir
    |> Option.map (fun info -> DaemonInfoFile.path realSageFsDir, info)
    |> Option.toList
  let spawned =
    let dir = registryDir realSageFsDir
    match Directory.Exists dir with
    | false -> []
    | true ->
      Directory.GetFiles(dir, "*.json")
      |> Array.choose (fun f -> tryReadRegistryEntry f |> Option.map (fun info -> f, info))
      |> Array.toList
  primary @ spawned

// ─── `sagefs sweep` ─────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type SweepVerdict =
  /// The recorded owner is gone: this daemon is reapable.
  | Reap of reason: string
  /// Never touch it — no owner was ever recorded, or the recorded owner is
  /// still alive.
  | Leave of reason: string

/// Pure sweep decision: a daemon is reapable IFF it has a recorded owner
/// AND that owner is gone. A daemon with no recorded owner (TTL-only, or
/// neither flag — an interactive `sagefs` a human is using) is never
/// touched by sweep; that is what makes sweep safe to run unattended.
/// `isOwnerAlive` is injected so this stays pure and testable — in
/// production it is `OwnerMonitor.isAlive OwnerMonitor.getProcessById`.
let decideSweep
  (isOwnerAlive: int -> int64 option -> bool)
  (info: DaemonInfoFile)
  : SweepVerdict =
  match info.OwnerPid with
  | None -> SweepVerdict.Leave "no recorded owner"
  | Some ownerPid ->
    match isOwnerAlive ownerPid info.OwnerStart with
    | true -> SweepVerdict.Leave "recorded owner is still alive"
    | false -> SweepVerdict.Reap (sprintf "recorded owner (pid %d) is gone" ownerPid)

/// Sweep every known daemon-info and return a verdict for each, alongside
/// the file it came from. Side effects (killing a process, deleting the
/// stale file) are the caller's responsibility — this stays a pure read
/// over already-loaded `DaemonInfoFile`s plus the injected liveness check.
let sweep
  (isOwnerAlive: int -> int64 option -> bool)
  (realSageFsDir: string)
  : (string * DaemonInfoFile * SweepVerdict) list =
  enumerateKnownDaemonInfos realSageFsDir
  |> List.map (fun (path, info) -> path, info, decideSweep isOwnerAlive info)
