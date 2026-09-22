module SageFs.Tests.TierPlanTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Build.TierPlan

let private tiersOf (names: string list) =
  names |> List.distinct |> List.map (fun n -> { Name = n; Args = n })

let planTests =
  testList "TierPlan" [
    testProperty "order is a permutation: every tier runs exactly once" <|
      fun (names: NonEmptyString list) (known: (NonEmptyString * PositiveInt) list) ->
        let tiers = tiersOf (names |> List.map (fun n -> n.Get))
        let durations = known |> List.map (fun (n, s) -> n.Get, float s.Get) |> Map.ofList
        List.sort (order durations tiers) = List.sort tiers

    testProperty "a tier's timeout stays between ten minutes and an hour, and never undercuts its own history" <|
      fun (name: NonEmptyString) (recorded: PositiveInt) ->
        let t = { Name = name.Get; Args = name.Get }
        let seconds = float recorded.Get
        let timeout = (timeoutOf (Map.ofList [ name.Get, seconds ]) t).TotalSeconds
        timeout >= tierTimeoutFloorSeconds
        && timeout <= tierTimeoutCeilingSeconds
        && (seconds * 4.0 > tierTimeoutCeilingSeconds || timeout >= seconds * 4.0)

    testCase "a tier nobody has timed gets the full hour" <| fun _ ->
      (timeoutOf Map.empty { Name = "new"; Args = "new" }).TotalSeconds
      |> Expect.equal "a first run might just be long" tierTimeoutCeilingSeconds

    testProperty "a tier with no recorded duration starts before every timed tier" <|
      fun (names: NonEmptyString list) (known: (NonEmptyString * PositiveInt) list) ->
        let tiers = tiersOf (names |> List.map (fun n -> n.Get))
        let durations = known |> List.map (fun (n, s) -> n.Get, float s.Get) |> Map.ofList
        let ordered = order durations tiers
        let isUnknown t = not (durations.ContainsKey t.Name)
        let lastUnknown = ordered |> List.tryFindIndexBack isUnknown
        let firstKnown = ordered |> List.tryFindIndex (isUnknown >> not)
        match lastUnknown, firstKnown with
        | Some u, Some k -> u < k
        | _ -> true

    testProperty "without isolation, tiers never run concurrently" <|
      fun (PositiveInt cores) (requested: int option) ->
        parallelism Shared cores requested = 1

    testProperty "with isolation, an explicit request wins and the default stays within 1..6" <|
      fun (PositiveInt cores) (PositiveInt n) ->
        parallelism CopyOnWrite cores (Some n) = n
        && (let d = parallelism CopyOnWrite cores None in d >= 1 && d <= 6)

    // ── sharding ──
    testProperty "assign is a partition: every suite lands in exactly one shard in 1..n" <|
      fun (PositiveInt n) (names: NonEmptyString list) (known: (NonEmptyString * PositiveInt) list) ->
        let n = min n 16
        let suites = names |> List.map (fun s -> s.Get) |> List.distinct
        let d = known |> List.map (fun (k, v) -> k.Get, float v.Get) |> Map.ofList
        let plan = assign n d suites
        Set.ofSeq plan.Keys = Set.ofList suites
        && (plan |> Map.forall (fun _ shard -> shard >= 1 && shard <= n))

    testProperty "assign is deterministic: input order does not change the plan" <|
      fun (PositiveInt n) (names: NonEmptyString list) ->
        let suites = names |> List.map (fun s -> s.Get) |> List.distinct
        assign n Map.empty suites = assign n Map.empty (List.rev suites)

    testCase "assign balances measured suites: one heavy suite gets a shard to itself" <| fun _ ->
      let d = Map.ofList [ "heavy", 300.0; "a", 60.0; "b", 60.0; "c", 60.0; "d", 60.0 ]
      let plan = assign 2 d [ "a"; "b"; "c"; "d"; "heavy" ]
      let heavyShard = plan["heavy"]
      plan |> Map.filter (fun _ s -> s = heavyShard) |> Map.count
      |> Expect.equal "the 300s suite shares its shard with nothing (4 x 60s fit in the other)" 1

    testCase "shardOfArgs parses k/n and rejects nonsense" <| fun _ ->
      shardOfArgs [| "--integration-host"; "--shard"; "2/4" |] |> Expect.equal "2 of 4" (Some { Index = 2; Count = 4 })
      shardOfArgs [| "--shard"; "5/4" |] |> Expect.isNone "index beyond count"
      shardOfArgs [| "--shard"; "0/4" |] |> Expect.isNone "zero index"
      shardOfArgs [| "--shard" |] |> Expect.isNone "missing value"
      nameOfArgs "--integration-host --shard 2/4 --summary" |> Expect.equal "shard row name" "--integration-host[2/4]"
      fileNameOf "--integration-host[2/4]" |> Expect.equal "file-safe" "integration-host-2of4"

    testProperty "the modelled wall clock is bounded by the longest tier and by an even split" <|
      fun (PositiveInt slots) (durations: PositiveInt list) ->
        let slots = min slots 8
        let tiers = durations |> List.mapi (fun i _ -> { Name = string i; Args = string i })
        let d = durations |> List.mapi (fun i s -> string i, float s.Get) |> Map.ofList
        let span = makespan slots (fun t -> d[t.Name]) tiers
        let total = d |> Map.fold (fun acc _ v -> acc + v) 0.0
        let longest = if d.IsEmpty then 0.0 else d |> Map.toList |> List.map snd |> List.max
        span >= longest - 1e-9 && span >= total / float slots - 1e-9 && span <= total + 1e-9

    testCase "one slot is exactly the serial sum" <| fun _ ->
      let tiers = tiersOf [ "a"; "b"; "c" ]
      let d = Map.ofList [ "a", 17.0; "b", 4.0; "c", 2.0 ]
      makespan 1 (fun t -> d[t.Name]) tiers |> Expect.equal "serial" 23.0

    testCase "LPT on the gate's real shape: the host tier bounds the wall clock" <| fun _ ->
      // Measured per-tier minutes (serial gate, 2026-09-21): host 17, default
      // 4.3, mutation 2, browser 1, hr 1.2, lt 1.5, disconnect 1.
      let d = Map.ofList [ "host", 17.0; "default", 4.3; "mutation", 2.0; "browser", 1.0; "hr", 1.2; "lt", 1.5; "disconnect", 1.0 ]
      let tiers = tiersOf [ "default"; "mutation"; "host"; "browser"; "hr"; "lt"; "disconnect" ]
      makespan 3 (fun t -> d[t.Name]) (order d tiers) |> Expect.equal "three slots: the host tier alone" 17.0

    testCase "nameOfArgs names the default run and keeps flags" <| fun _ ->
      nameOfArgs "--summary" |> Expect.equal "default run" "default"
      nameOfArgs "--integration-host --summary" |> Expect.equal "flag tier" "--integration-host"

    // ── port ranges: the fix for cross-tier daemon-port collisions ──
    testProperty "portRangeOf partitions the pool: in bounds, disjoint, and clear of the live daemon" <|
      fun (PositiveInt slotsArg) ->
        let slots = min slotsArg 32
        let ranges = [ 0 .. slots - 1 ] |> List.map (portRangeOf slots)
        let inBounds =
          ranges |> List.forall (fun (lo, hi) -> lo >= testPortPoolLow && hi <= testPortPoolHigh && lo < hi)
        let disjoint =
          List.allPairs [ 0 .. slots - 1 ] [ 0 .. slots - 1 ]
          |> List.forall (fun (i, j) ->
            i = j ||
            (let (aLo, aHi), (bLo, bHi) = ranges[i], ranges[j] in aHi <= bLo || bHi <= aLo))
        // The live user daemon (37749/37750) is above the whole pool, so
        // clearance falls out of `inBounds` — asserted explicitly anyway,
        // since this is the property that matters most.
        let clearOfLiveDaemon = ranges |> List.forall (fun (_, hi) -> hi <= 37749)
        inBounds && disjoint && clearOfLiveDaemon

    testProperty "portRangeOf tiles the whole pool with no gaps" <|
      fun (PositiveInt slotsArg) ->
        let slots = min slotsArg 32
        let ranges = [ 0 .. slots - 1 ] |> List.map (portRangeOf slots)
        fst ranges.Head = testPortPoolLow
        && snd (List.last ranges) = testPortPoolHigh
        && (ranges |> List.pairwise |> List.forall (fun ((_, hi), (lo2, _)) -> hi = lo2))

    testProperty "portRangeOf is deterministic: the same (slots, slotIndex) always yields the same range" <|
      fun (PositiveInt slotsArg) (index: int) ->
        let slots = min slotsArg 32
        portRangeOf slots index = portRangeOf slots index

    testCase "portRangeOf with one slot is the whole pool" <| fun _ ->
      portRangeOf 1 0 |> Expect.equal "one slot, whole pool" (testPortPoolLow, testPortPoolHigh)
  ]

/// The isolation itself, for real: a process run through isolatedArgv sees
/// its clone at the checkout path, runs as this user (not root), and its
/// writes land in the clone while the original stays untouched. Skips where
/// the host forbids unprivileged user namespaces (e.g. a hardened CI image),
/// which is exactly where the pipeline falls back to Shared.
let isolationTests =
  testList "TierPlan isolation" [
    testCase "a tier's writes land in its own clone and its own /tmp, never in the shared ones" <| fun _ ->
      if not (OperatingSystem.IsLinux()) then skiptest "user namespaces are Linux-only"
      // NOT under /tmp: the private /tmp mount would hide a checkout that lives
      // there (the pipeline refuses to isolate such a checkout for that reason).
      let root = Path.Combine(AppContext.BaseDirectory, "tierplan-" + Guid.NewGuid().ToString("N"))
      let checkout = Path.Combine(root, "checkout")
      let clone = Path.Combine(root, "clone")
      let privateTmp = Path.Combine(root, "tmp")
      Directory.CreateDirectory checkout |> ignore
      Directory.CreateDirectory clone |> ignore
      Directory.CreateDirectory privateTmp |> ignore
      // A rendezvous file in the REAL /tmp, standing in for /tmp/MSBuild<pid>.
      let sharedMarker = sprintf "/tmp/tierplan-shared-%s" (Guid.NewGuid().ToString("N"))
      File.WriteAllText(sharedMarker, "shared")
      let insideName = sprintf "tierplan-inside-%s" (Guid.NewGuid().ToString("N"))
      File.WriteAllText(Path.Combine(checkout, "fixture.txt"), "original")
      // Trailing newline so `cat` and the `id -u` after it print separate lines.
      File.WriteAllText(Path.Combine(clone, "fixture.txt"), "private\n")
      try
        // Real uid/gid from /proc (no native-interop dependency needed).
        let idOf (field: string) =
          File.ReadAllLines "/proc/self/status"
          |> Array.find (fun l -> l.StartsWith(field + ":"))
          |> fun l -> int (l.Split([| '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries)[1])
        let uid = idOf "Uid"
        let gid = idOf "Gid"
        let argv =
          isolatedArgv uid gid checkout clone privateTmp
            (sprintf "sh -c 'cat fixture.txt; id -u; echo written > fixture.txt; test -e %s && echo SEES-SHARED-TMP || echo private-tmp; echo t > /tmp/%s'" sharedMarker insideName)
        let psi = Diagnostics.ProcessStartInfo(List.head argv)
        List.tail argv |> List.iter psi.ArgumentList.Add
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        use p = Diagnostics.Process.Start psi
        let out = p.StandardOutput.ReadToEnd()
        let err = p.StandardError.ReadToEnd()
        p.WaitForExit()
        if p.ExitCode <> 0 && (err.Contains "Operation not permitted" || err.Contains "superuser") then
          skiptest (sprintf "unprivileged user namespaces are unavailable here: %s" (err.Trim()))
        p.ExitCode |> Expect.equal (sprintf "isolated command should succeed (stderr: %s)" err) 0
        let lines = out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        lines[0] |> Expect.equal "the tier sees its clone at the checkout path" "private"
        lines[1] |> Expect.equal "the tier runs as the calling user, not root" (string uid)
        File.ReadAllText(Path.Combine(checkout, "fixture.txt"))
        |> Expect.equal "the shared checkout is untouched" "original"
        File.ReadAllText(Path.Combine(clone, "fixture.txt")).Trim()
        |> Expect.equal "the write landed in the tier's own clone" "written"
        lines[2]
        |> Expect.equal "the tier cannot see the shared /tmp (MSBuild node pipes live there)" "private-tmp"
        File.Exists(Path.Combine(privateTmp, insideName))
        |> Expect.isTrue "the tier's /tmp writes land in its private tmp"
        File.Exists("/tmp/" + insideName)
        |> Expect.isFalse "and never in the shared /tmp"
      finally
        try Directory.Delete(root, true) with _ -> ()
        try File.Delete sharedMarker with _ -> ()
  ]

[<Tests>]
let tests = testList "TierPlan suite" [ planTests; isolationTests ]
