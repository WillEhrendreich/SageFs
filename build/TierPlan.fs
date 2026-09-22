/// Pure planning for the pipeline's test tiers: how many run at once, in what
/// order, and how each one is wrapped so it cannot touch another's files.
///
/// Loaded by ci-pipeline.fsx (`#load`) and compiled into SageFs.Tests, so the
/// rules the pipeline runs on are the rules the tests check.
module SageFs.Build.TierPlan

/// One invocation of the test assembly (`dotnet SageFs.Tests.dll <Args>`).
type Tier = { Name: string; Args: string }

/// One slice of a sharded tier: shard `Index` (1-based) of `Count`.
type Shard = { Index: int; Count: int }

/// `--shard k/n` in an argument list, when present and well-formed.
let shardOfArgs (args: string array) : Shard option =
  match args |> Array.tryFindIndex ((=) "--shard") with
  | Some i when i + 1 < args.Length ->
    match args[i + 1].Split('/') with
    | [| k; n |] ->
      match System.Int32.TryParse k, System.Int32.TryParse n with
      | (true, k), (true, n) when n >= 1 && k >= 1 && k <= n -> Some { Index = k; Count = n }
      | _ -> None
    | _ -> None
  | _ -> None

/// Tier name of an argument string: its first token, with the bare
/// `--summary` default run named "default" (the trust ledger's Tier field),
/// and a shard suffix (`--integration-host[2/4]`) so every shard is its own row.
let nameOfArgs (args: string) =
  let tokens = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
  let baseName =
    match tokens[0] with
    | "--summary" -> "default"
    | flag -> flag
  match shardOfArgs tokens with
  | Some s -> sprintf "%s[%d/%d]" baseName s.Index s.Count
  | None -> baseName

/// A name safe to use as a file name (logs, ledgers, clones).
let fileNameOf (tierName: string) =
  tierName.TrimStart('-').Replace("/", "of").Replace("[", "-").Replace("]", "")

/// Partition `suites` into `count` shards, balanced by recorded duration
/// (longest first, each into the currently lightest shard). Pure and
/// deterministic: every shard process computes the SAME map from the same
/// inputs, so the shards together run every suite exactly once without any
/// coordination. A suite with no recorded duration counts as the mean of the
/// known ones (or 1s when nothing is known). Ties break on name.
let assign (count: int) (durations: Map<string, float>) (suites: string list) : Map<string, int> =
  let count = max 1 count
  let suites = List.distinct suites
  let known = suites |> List.choose durations.TryFind
  let fallback = match known with [] -> 1.0 | xs -> List.average xs
  let weight s = durations.TryFind s |> Option.defaultValue fallback
  let load = Array.create count 0.0
  suites
  |> List.sortWith (fun a b ->
    match compare (weight b) (weight a) with
    | 0 -> compare a b
    | c -> c)
  |> List.fold (fun (acc: Map<string, int>) s ->
    let i = load |> Array.mapi (fun i l -> i, l) |> Array.minBy (fun (i, l) -> l, i) |> fst
    load[i] <- load[i] + weight s
    acc.Add(s, i + 1)) Map.empty

let tier (args: string) = { Name = nameOfArgs args; Args = args }

/// Whether tiers can be given private copies of the checkout.
type Isolation =
  /// Copy-on-write clones (btrfs/xfs reflink) plus a private mount namespace
  /// that shows each tier ITS clone at the original checkout path. Tests
  /// locate fixtures through `__SOURCE_DIRECTORY__`, an absolute path baked
  /// into the compiled DLL, so a clone at a different path would still read
  /// and write the original. The mount is what makes the isolation real.
  | CopyOnWrite
  /// No private copies (a plain-ext4 hosted runner): tiers share one tree and
  /// must run one at a time.
  | Shared

/// How many tiers run at once. Without isolation, one: sharing a checkout is
/// exactly the conflict this module exists to rule out. With it, an explicit
/// request wins, else one tier per ~3 cores capped at 6, because each tier
/// runs its own daemons, FSI hosts and browsers, and contention fakes timing
/// failures (the trust table is where a too-high setting shows up).
let parallelism (isolation: Isolation) (cores: int) (requested: int option) =
  match isolation, requested with
  | Shared, _ -> 1
  | CopyOnWrite, Some n when n >= 1 -> n
  | CopyOnWrite, _ -> max 1 (min 6 (cores / 3))

/// Longest-expected-first (LPT) order: with a fixed number of slots, starting
/// the long tiers first minimises the wall clock. A tier with no recorded
/// duration sorts FIRST, since it may be long and starting it last is the
/// worst case. Stable for equal durations.
let order (durations: Map<string, float>) (tiers: Tier list) : Tier list =
  tiers
  |> List.sortByDescending (fun t ->
    match durations.TryFind t.Name with
    | Some seconds -> seconds
    | None -> infinity)

/// How long a tier may run before the pipeline kills it. A hung tier used to
/// sit silently until the whole stage's 90-minute budget ran out (2026-09-22,
/// a spinner deadlock in the default tier). Four times the tier's recorded
/// duration, never under ten minutes, and never over an hour. A tier with no
/// recorded duration gets the hour, since it might just be long.
let tierTimeoutFloorSeconds = 600.0
let tierTimeoutCeilingSeconds = 3600.0

let timeoutOf (durations: Map<string, float>) (tier: Tier) : System.TimeSpan =
  let seconds =
    match durations.TryFind tier.Name with
    | Some recorded -> max tierTimeoutFloorSeconds (min tierTimeoutCeilingSeconds (recorded * 4.0))
    | None -> tierTimeoutCeilingSeconds
  System.TimeSpan.FromSeconds seconds

/// Wall-clock seconds for running `ordered` greedily on `slots` slots (each
/// tier takes the earliest free slot, in order). Pure model of the scheduler,
/// used to report the expected speed-up and to test the ordering.
let makespan (slots: int) (durationOf: Tier -> float) (ordered: Tier list) =
  let free = Array.create (max 1 slots) 0.0
  for t in ordered do
    let i = free |> Array.mapi (fun i f -> i, f) |> Array.minBy snd |> fst
    free[i] <- free[i] + durationOf t
  Array.max free

/// Concurrent tiers share ONE network namespace (mount namespaces isolate
/// files, never ports), and every real-daemon-spawning test harness reserves
/// a port by binding it, reading it back, then RELEASING it before the
/// daemon itself binds — a window another tier's daemon can win in. Below,
/// the pool every tier's daemon ports are scanned from, and the disjoint
/// slice of it handed to one concurrently-running slot, so that race can
/// never cross a tier boundary: two tiers scanning disjoint slices cannot
/// both land on the same pair no matter how the race falls.
///
/// The pool sits entirely below `testPortPoolHigh` (32000): comfortably under
/// the Linux ephemeral-port floor observed on this machine (32768, from
/// `/proc/sys/net/ipv4/ip_local_port_range`) — so a client connection's own
/// randomly-assigned outbound port can never collide with a port a tier is
/// deliberately trying to bind — and nowhere near the user's own live daemon
/// (37749/37750). Both exclusions fall out of the bound; neither needs its
/// own case.
let testPortPoolLow = 20000
let testPortPoolHigh = 32000

/// The `slotIndex`-th (0-based) of `slots` equal-width, contiguous, disjoint
/// slices of the port pool — one per concurrently-running tier process (not
/// one per tier BY NAME: `runTiers` reuses a slot for every tier it pulls off
/// the queue for that slot, so the slot index is what a port range is keyed
/// to). Integer division may leave a remainder; the LAST slice absorbs it, so
/// the slices tile the whole pool with no gaps and no overlap for any
/// `slots >= 1`.
let portRangeOf (slots: int) (slotIndex: int) : int * int =
  let slots = max 1 slots
  let slotIndex = ((slotIndex % slots) + slots) % slots
  let width = (testPortPoolHigh - testPortPoolLow) / slots
  let start = testPortPoolLow + slotIndex * width
  let stop = if slotIndex = slots - 1 then testPortPoolHigh else start + width
  start, stop

/// The argv that runs `command` with `clone` mounted over `checkout` AND
/// `privateTmp` mounted over /tmp, visible only to that process tree, as the
/// calling user rather than root.
///
/// The private /tmp is not optional. Tools meet their helpers in /tmp at FIXED
/// paths that ignore TMPDIR: MSBuild's reusable build nodes listen on
/// /tmp/MSBuild<pid>. With a shared /tmp, a tier's `dotnet build` handed work
/// to a node from ANOTHER namespace, which saw the ORIGINAL checkout at the
/// checkout path. It built into the shared tree, and the tier's own compiler
/// then could not find the output in its clone (FS0078 on
/// obj/Debug/.../ref/SageFs.Core.dll, first parallel gate, 2026-09-21). A
/// private /tmp closes that whole class: X locks, NuGet scratch, any
/// rendezvous socket.
///
/// Mounting needs root inside a user namespace, so the outer namespace maps
/// root, mounts, then a nested namespace maps back to `uid`/`gid` before the
/// tier starts. Chromium refuses to run as root, and nothing else should
/// either.
let isolatedArgv
  (uid: int)
  (gid: int)
  (checkout: string)
  (clone: string)
  (privateTmp: string)
  (command: string)
  : string list =
  let quote (s: string) = "'" + s.Replace("'", "'\\''") + "'"
  [ "unshare"; "--user"; "--map-root-user"; "--mount"; "--"; "sh"; "-c"
    sprintf "mount --bind %s %s && mount --bind %s /tmp && cd %s && exec unshare --user --map-user=%d --map-group=%d -- %s"
      (quote clone) (quote checkout) (quote privateTmp) (quote checkout) uid gid command ]
