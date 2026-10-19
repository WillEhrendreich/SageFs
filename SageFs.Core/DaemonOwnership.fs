module SageFs.DaemonOwnership

open System
open System.IO

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

/// The three flags that say who owns a daemon or when it gives up. A closed set, so the
/// spelling of each lives in exactly one function.
[<RequireQualifiedAccess>]
type OwnershipFlag =
  | OwnerPid
  | OwnerStart
  | Ttl

module OwnershipFlag =
  let all : OwnershipFlag list =
    [ OwnershipFlag.OwnerPid; OwnershipFlag.OwnerStart; OwnershipFlag.Ttl ]

  /// The flag as typed on the command line.
  let toString (flag: OwnershipFlag) : string =
    match flag with
    | OwnershipFlag.OwnerPid -> "--owner-pid"
    | OwnershipFlag.OwnerStart -> "--owner-start"
    | OwnershipFlag.Ttl -> "--ttl"

  /// A complete, valid use of the flag, for an error message to show.
  let example (flag: OwnershipFlag) : string =
    match flag with
    | OwnershipFlag.OwnerPid -> "--owner-pid 4242"
    | OwnershipFlag.OwnerStart -> "--owner-start 638000000000000000"
    | OwnershipFlag.Ttl -> "--ttl 30m"

  /// What a valid value for the flag looks like, in words.
  let validValue (flag: OwnershipFlag) : string =
    match flag with
    | OwnershipFlag.OwnerPid -> "a process id: a whole number above zero"
    | OwnershipFlag.OwnerStart -> "the owner's start time in UTC ticks: a whole number above zero"
    | OwnershipFlag.Ttl -> "a duration: a number with a unit (ms, s, m or h), or a bare number of seconds"

  /// The flag a command-line token spells, if it spells one. This is the parse boundary: a token
  /// that is not an ownership flag belongs to somebody else's parser and is left alone.
  let tryOfToken (token: string) : OwnershipFlag option =
    all |> List.tryFind (fun flag -> String.Equals(toString flag, token, StringComparison.Ordinal))

/// Why an ownership flag was refused. Every case names the flag, because the person reading it
/// is looking at a command line, not at this code.
[<RequireQualifiedAccess>]
type OwnershipArgError =
  /// The flag was the last argument, or the next argument was another flag.
  | MissingValue of flag: OwnershipFlag
  /// The flag had a value that is not one it accepts.
  | InvalidValue of flag: OwnershipFlag * raw: string
  /// `--owner-start` fences an owner pid, and there was no `--owner-pid` to fence.
  | OwnerStartWithoutOwnerPid

module OwnershipArgError =
  let describe (error: OwnershipArgError) : string =
    match error with
    | OwnershipArgError.MissingValue flag ->
      sprintf "%s needs a value, for example %s." (OwnershipFlag.toString flag) (OwnershipFlag.example flag)
    | OwnershipArgError.InvalidValue (flag, raw) ->
      sprintf "%s %s is not valid. A value for %s is %s, for example %s."
        (OwnershipFlag.toString flag) raw (OwnershipFlag.toString flag)
        (OwnershipFlag.validValue flag) (OwnershipFlag.example flag)
    | OwnershipArgError.OwnerStartWithoutOwnerPid ->
      sprintf "%s only fences %s, and there was no %s. Pass both, for example %s."
        (OwnershipFlag.toString OwnershipFlag.OwnerStart) (OwnershipFlag.toString OwnershipFlag.OwnerPid)
        (OwnershipFlag.toString OwnershipFlag.OwnerPid)
        (OwnershipFlag.example OwnershipFlag.OwnerPid)

/// Whether the owner's pid is pinned to one process. A pid alone can be recycled by the OS; the
/// owner's start time closes that race.
[<RequireQualifiedAccess>]
type OwnerFence =
  | Unfenced
  | StartedAtTicks of int64

module OwnerFence =
  /// The fence as the owner monitor and the daemon-info file carry it.
  let toTicks (fence: OwnerFence) : int64 option =
    match fence with
    | OwnerFence.Unfenced -> None
    | OwnerFence.StartedAtTicks ticks -> Some ticks

/// The process whose death ends the daemon.
type DaemonOwner = { Pid: int; Fence: OwnerFence }

/// Where a daemon's time limit came from. The nested-checkout default is logged loudly, so the
/// two have to be told apart by the value itself and not by a flag beside it.
[<RequireQualifiedAccess>]
type TtlSource =
  | Explicit
  | NestedCheckoutDefault

/// Who owns a daemon, and when it gives up. Every case is a legal combination: there is no
/// fence without a pid, and a time limit always says where it came from.
[<RequireQualifiedAccess>]
type Ownership =
  /// Neither flag: runs until someone stops it.
  | Unowned
  /// Dies with its spawner.
  | OwnedByPid of owner: DaemonOwner
  /// Gives up after sitting idle this long.
  | ExpiresAfter of ttl: TimeSpan * source: TtlSource
  /// Both. The time limit was asked for, because the nested-checkout default only applies to an
  /// unowned daemon.
  | OwnedAndExpiring of owner: DaemonOwner * ttl: TimeSpan

module Ownership =
  /// The owner, for the edges that start the owner monitor and write the daemon-info file.
  let owner (ownership: Ownership) : DaemonOwner option =
    match ownership with
    | Ownership.OwnedByPid owner
    | Ownership.OwnedAndExpiring (owner, _) -> Some owner
    | Ownership.Unowned
    | Ownership.ExpiresAfter _ -> None

  /// The owner as the owner monitor takes it: the pid, fenced by the start time when one was given. For the edge that
  /// starts a monitor: the daemon's own, and the watchdog's above it.
  let monitoredOwner (ownership: Ownership) : OwnerMonitor.Owner option =
    owner ownership
    |> Option.map (fun o -> { OwnerMonitor.Owner.Pid = o.Pid; StartTimeTicks = OwnerFence.toTicks o.Fence })

  /// The idle limit, for the edge that starts the idle timer.
  let ttl (ownership: Ownership) : TimeSpan option =
    match ownership with
    | Ownership.ExpiresAfter (ttl, _)
    | Ownership.OwnedAndExpiring (_, ttl) -> Some ttl
    | Ownership.Unowned
    | Ownership.OwnedByPid _ -> None

  /// Parse a duration like "30m", "1s", "500ms", "2h", or a bare number of seconds. Anything else
  /// is `None`: a number that is negative, not finite or too large for a `TimeSpan` included.
  /// `parse` turns that `None` into an `Error` that names `--ttl`.
  let tryParseTtl (raw: string) : TimeSpan option =
    match String.IsNullOrWhiteSpace raw with
    | true -> None
    | false ->
      let s = raw.Trim()
      // Order matters: "ms" must be tried before "s" (every "Xms" also ends with "s").
      let units =
        [ "ms", TimeSpan.TicksPerMillisecond
          "h", TimeSpan.TicksPerHour
          "m", TimeSpan.TicksPerMinute
          "s", TimeSpan.TicksPerSecond ]
      let suffix, ticksPerUnit =
        units
        |> List.tryFind (fun (suffix, _) -> s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        |> Option.defaultValue ("", TimeSpan.TicksPerSecond)
      let numeric = s.Substring(0, s.Length - suffix.Length)
      let style =
        Globalization.NumberStyles.AllowDecimalPoint
        ||| Globalization.NumberStyles.AllowLeadingWhite
        ||| Globalization.NumberStyles.AllowTrailingWhite
      match Double.TryParse(numeric, style, Globalization.CultureInfo.InvariantCulture) with
      | true, n when Double.IsFinite n && n >= 0.0 ->
        let ticks = n * float ticksPerUnit
        match ticks < float TimeSpan.MaxValue.Ticks with
        | true -> Some (TimeSpan.FromTicks (int64 ticks))
        | false -> None
      | _ -> None

  /// A whole number above zero, digits only: no sign, no separators, no whitespace.
  let tryParsePositive (raw: string) : int64 option =
    match Int64.TryParse(raw, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
    | true, n when n > 0L -> Some n
    | _ -> None

  /// What `parse` has seen so far. The options are the parser's own bookkeeping ("this flag has
  /// not appeared yet"); none of them reaches the `Ownership` it builds.
  type ParseState =
    { Pid: int option
      StartTicks: int64 option
      Ttl: TimeSpan option
      /// Newest first.
      Errors: OwnershipArgError list }

  let emptyParseState = { Pid = None; StartTicks = None; Ttl = None; Errors = [] }

  let applyFlag (flag: OwnershipFlag) (raw: string) (state: ParseState) : ParseState =
    let invalid = { state with Errors = OwnershipArgError.InvalidValue (flag, raw) :: state.Errors }
    match flag with
    | OwnershipFlag.OwnerPid ->
      match tryParsePositive raw with
      | Some pid when pid <= int64 Int32.MaxValue -> { state with Pid = Some (int pid) }
      | _ -> invalid
    | OwnershipFlag.OwnerStart ->
      match tryParsePositive raw with
      | Some ticks -> { state with StartTicks = Some ticks }
      | None -> invalid
    | OwnershipFlag.Ttl ->
      match tryParseTtl raw with
      | Some ttl -> { state with Ttl = Some ttl }
      | None -> invalid

  /// Parse `--owner-pid <pid>`, `--owner-start <ticks>`, `--ttl <duration>` out of a daemon's raw
  /// CLI args. A flag that is present and wrong is an `Error` naming it, never a daemon that
  /// quietly runs unowned: the flag exists to stop leaked daemons, so a typo in it must not
  /// produce one. Arguments that are not ownership flags are skipped; a repeated flag takes its
  /// last value.
  let parse (args: string list) : Result<Ownership, OwnershipArgError list> =
    let rec loop (state: ParseState) (remaining: string list) : ParseState =
      match remaining with
      | [] -> state
      | token :: rest ->
        match OwnershipFlag.tryOfToken token with
        | None -> loop state rest
        | Some flag ->
          match rest with
          | value :: afterValue when not (value.StartsWith("--", StringComparison.Ordinal)) ->
            loop (applyFlag flag value state) afterValue
          | _ -> loop { state with Errors = OwnershipArgError.MissingValue flag :: state.Errors } rest
    let state = loop emptyParseState args
    let orphanedFence =
      match state.Pid, state.StartTicks with
      | None, Some _ -> [ OwnershipArgError.OwnerStartWithoutOwnerPid ]
      | _ -> []
    match List.rev state.Errors @ orphanedFence with
    | _ :: _ as errors -> Result.Error errors
    | [] ->
      let owner =
        state.Pid
        |> Option.map (fun pid ->
          { Pid = pid
            Fence =
              match state.StartTicks with
              | Some ticks -> OwnerFence.StartedAtTicks ticks
              | None -> OwnerFence.Unfenced })
      match owner, state.Ttl with
      | None, None -> Result.Ok Ownership.Unowned
      | Some owner, None -> Result.Ok (Ownership.OwnedByPid owner)
      | None, Some ttl -> Result.Ok (Ownership.ExpiresAfter (ttl, TtlSource.Explicit))
      | Some owner, Some ttl -> Result.Ok (Ownership.OwnedAndExpiring (owner, ttl))

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
let defaultTtlForNestedCheckout = Timeouts.nestedCheckoutDaemonTtl

/// An unowned daemon inside another checkout gets the default TTL, and says so with
/// `TtlSource.NestedCheckoutDefault` so the caller can log it loudly. Anything that already has
/// an owner or a TTL of its own is left exactly as asked.
let applyNestedCheckoutDefault (isNested: bool) (ownership: Ownership) : Ownership =
  match ownership, isNested with
  | Ownership.Unowned, true ->
    Ownership.ExpiresAfter (defaultTtlForNestedCheckout, TtlSource.NestedCheckoutDefault)
  | _ -> ownership

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

/// The window `hasClients` uses to decide "is any MCP/SSE client still
/// around". This must be a small, fixed span — never `ttl` itself. Using
/// `ttl` as the freshness window (the bug this closes) means a SINGLE tool
/// call stays "fresh" for a full `ttl` after it happened, and the idle-check
/// callback re-stamps `lastActiveAt` to "now" on every tick while it does —
/// so a daemon never goes idle in less than roughly 2×`ttl` after its last
/// real activity, and each subsequent call within that window pushes the
/// goalposts out again. Capped at the same 2-minute convention already used
/// for "is this cohort member still around" (`DaemonMode.cohortReaperRenewWindow`,
/// itself kept under `AgentActivityTracker`'s own 5-minute hard eviction) —
/// and at `ttl` itself for a shorter custom TTL (e.g. tests), so the window
/// can never exceed the bound it is meant to serve.
let ttlClientActivityWindow (ttl: TimeSpan) : TimeSpan =
  min ttl Timeouts.agentActivityFresh

/// Whether a session's own status still represents something genuinely
/// usable — i.e. NOT a terminal, unusable state (`Faulted`: a worker that
/// crashed and gave up retrying; `Stopped`). `SessionManager` keeps a
/// `Faulted` tombstone registered (so a caller can see WHY a session died)
/// rather than removing it outright, so counting ANY registered session as
/// "live" would let one permanently-dead session block a TTL-governed
/// daemon from ever going idle — the exact bug this closes. Exposed here
/// (ownership rule 2's own concern) rather than in `SessionManager` so the
/// TTL idle-check's notion of "live" stays independent of the session/worker
/// model's internals.
let isUsableSessionStatus (status: SageFs.WorkerProtocol.SessionLifecycleStatus) : bool =
  match status with
  | SageFs.WorkerProtocol.SessionLifecycleStatus.Faulted _
  | SageFs.WorkerProtocol.SessionLifecycleStatus.Stopped -> false
  | _ -> true

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
  /// Indented, keys as written: a person reads this file when a daemon wedges.
  let private profile = Json.indented Json.standard

  let fileName = "daemon-info.json"

  /// Absolute path of the daemon-info file inside a data dir.
  let path (dataDir: string) : string = Path.Combine(dataDir, fileName)

  /// Serialize and write, atomically (write-then-move so a reader never
  /// observes a half-written file).
  let write (dataDir: string) (info: DaemonInfoFile) : unit =
    Directory.CreateDirectory(dataDir) |> ignore
    let target = path dataDir
    let tmp = target + ".tmp"
    File.WriteAllText(tmp, Json.serialize profile info)
    File.Move(tmp, target, true)

  /// The file's text for an info, for a registry entry that is the same document.
  let render (info: DaemonInfoFile) : string = Json.serialize profile info

  /// Read one daemon-info document. A file that cannot be read or is not a daemon-info is an
  /// `Error` naming the file and why.
  let read (file: string) : Result<DaemonInfoFile, SageFsError> =
    try
      Json.deserialize<DaemonInfoFile> profile (File.ReadAllText file)
      |> Result.mapError (fun error -> SageFsError.JsonParseError(file, JsonError.describe error))
    with
    | :? IOException as ex -> Result.Error (SageFsError.JsonParseError(file, sprintf "could not be read: %s" ex.Message))
    | :? UnauthorizedAccessException as ex -> Result.Error (SageFsError.JsonParseError(file, sprintf "could not be read: %s" ex.Message))

  /// `read`, for a caller that treats a file it cannot use as "no daemon here". The reason goes to
  /// stderr so a skipped file is never silent.
  let tryReadFile (file: string) : DaemonInfoFile option =
    match read file with
    | Result.Ok info -> Some info
    | Result.Error error ->
      eprintfn "SageFs: skipping a daemon-info file: %s" (SageFsError.describe error)
      None

  let tryRead (dataDir: string) : DaemonInfoFile option =
    let p = path dataDir
    match File.Exists p with
    | false -> None
    | true -> tryReadFile p

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
  File.WriteAllText(tmp, DaemonInfoFile.render info)
  File.Move(tmp, target, true)

let unregisterSpawned (realSageFsDir: string) (pid: int) : unit =
  try
    let f = Path.Combine(registryDir realSageFsDir, sprintf "%d.json" pid)
    match File.Exists f with
    | true -> File.Delete f
    | false -> ()
  with _ -> ()

let private tryReadRegistryEntry (file: string) : DaemonInfoFile option =
  DaemonInfoFile.tryReadFile file

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
