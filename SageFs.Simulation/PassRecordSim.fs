namespace SageFs.Simulation

open System
open SageFs.Build

/// Deterministic Simulation Testing for same-commit tier reuse (build/PassRecord.fs): seeded histories of commits,
/// byte-different rebuilds, SDK upgrades, a dirty tree, a drifting shard partition, flaky tiers and a damaged store,
/// with a gate run folded through the REAL `PassRecord.decide`, `serialize` and `parse` after every few events. The
/// store holds the records' TEXT, as the disk would, so a truncated or swapped file is a fault like any other.
///
/// Same design rules as the other harnesses here:
///   * Chaos is DATA: a `Scenario` is an ordered event list. Same seed, same trace.
///   * Ground truth is kept by the world, never read from the decision under test: `Truth` is the set of input keys
///     a tier actually EXECUTED green on. A reuse that is not in it is a false green.
///   * Twins reintroduce the bugs the invariants exist to catch (ignore the product binaries, ignore a dirty tree,
///     never reuse), so a green run means something.
module PassRecordSim =

  type TierSpec =
    { Name: string
      Framework: string
      Args: string
      /// A sharded tier also depends on the partition.
      Sharded: bool
      Eligibility: Eligibility }

  let tiers : TierSpec list =
    [ { Name = "default"; Framework = "net11.0"; Args = "--summary"; Sharded = false; Eligibility = Eligibility.Eligible }
      { Name = "default-net10"; Framework = "net10.0"; Args = "--summary"; Sharded = false; Eligibility = Eligibility.Eligible }
      { Name = "--integration-host[1/2]"; Framework = "net11.0"; Args = "--integration-host --shard 1/2"; Sharded = true; Eligibility = Eligibility.Eligible }
      { Name = "--integration-host[2/2]"; Framework = "net11.0"; Args = "--integration-host --shard 2/2"; Sharded = true; Eligibility = Eligibility.Eligible }
      { Name = "--integration-browser"; Framework = "net11.0"; Args = "--integration-browser"; Sharded = false; Eligibility = Eligibility.Eligible }
      { Name = "--mutation-score"; Framework = "net11.0"; Args = "--mutation-score"; Sharded = false; Eligibility = Eligibility.Ineligible "the mutation score is judged against its own bar" } ]

  [<RequireQualifiedAccess>]
  type Outcome =
    | Green
    | Red

  /// What the world looks like right now: which commit, and which bytes are on disk.
  type World =
    { Commit: int
      Tree: TreeState
      Sdk: int
      /// Per framework: the test assembly's identity, and the product closure's.
      Test: Map<string, int>
      Closure: Map<string, int>
      /// The suite-duration table's identity, which a sharded tier partitions by.
      Partition: int }

  [<RequireQualifiedAccess>]
  type Event =
    /// A new commit: new sources, so new bytes everywhere, and a clean tree.
    | Commit
    /// A rebuild of the same commit that produced different bytes in the test assembly of a framework.
    | TestBytesChange of framework: string
    /// A rebuild of the same commit that produced different bytes in a framework's product closure only.
    | ClosureBytesChange of framework: string
    | SdkUpgrade
    | PartitionDrift
    | Dirty
    | Clean
    /// The store's record for a tier is cut short.
    | Truncate of tier: string
    | Delete of tier: string
    /// One tier's record is copied over another's file.
    | Swap of from: string * into: string
    /// A gate run: whether it was asked for `--fresh`, and what each executed tier would report, in tier order.
    | Gate of Freshness * Outcome list

  type Scenario = { Seed: int; Events: Event list }

  [<RequireQualifiedAccess>]
  type Decider =
    | Real
    /// TWIN: compares everything but the product closure, so a rebuilt dependency does not rerun a tier.
    | IgnoreClosureTwin
    /// TWIN: reuses on a dirty tree.
    | IgnoreDirtyTreeTwin
    /// TWIN: never reuses, which is safe and useless; the completeness invariant must catch it.
    | NeverReuseTwin

  [<RequireQualifiedAccess>]
  type TierStatus =
    | Executed of Outcome
    | Reused of PassRecord

  /// One gate run, with everything an invariant needs to judge it at the moment it happened.
  type GateRun =
    { Freshness: Freshness
      Tree: TreeState
      Inputs: Map<string, PassInputs>
      /// What the store held for each tier going in, as the decision saw it.
      Stored: Map<string, Stored>
      /// Keys a tier had executed green on, before this run.
      TruthBefore: Set<string>
      Statuses: Map<string, TierStatus> }

  type State =
    { Step: int
      World: World
      /// (commit, tier) to the record file's text.
      Store: Map<int * string, string>
      Truth: Set<string>
      Gates: GateRun list }

  let initialWorld : World =
    { Commit = 1
      Tree = TreeState.Clean
      Sdk = 1
      Test = Map [ "net10.0", 1; "net11.0", 1 ]
      Closure = Map [ "net10.0", 1; "net11.0", 1 ]
      Partition = 1 }

  let initial : State = { Step = 0; World = initialWorld; Store = Map.empty; Truth = Set.empty; Gates = [] }

  let bump(framework: string) (m: Map<string, int>) =
    m |> Map.add framework ((Map.tryFind framework m |> Option.defaultValue 0) + 1)

  /// What the pipeline would hash for `spec` in `world`.
  let inputsOf (world: World) (spec: TierSpec) : PassInputs =
    { Sha = sprintf "commit-%d" world.Commit
      Tier = spec.Name
      Args = spec.Args
      Framework = spec.Framework
      Sdk = sprintf "sdk-%d" world.Sdk
      TestAssembly = sprintf "test-%s-%d" spec.Framework world.Test[spec.Framework]
      Closure = sprintf "closure-%s-%d" spec.Framework world.Closure[spec.Framework]
      Partition = (match spec.Sharded with | true -> sprintf "partition-%d" world.Partition | false -> "") }

  let fileOf(world: World) (spec: TierSpec) = world.Commit, spec.Name

  /// What the store hands the decision: the file's text, parsed.
  let storedAt(store: Map<int * string, string>) (key: int * string) : Stored =
    match Map.tryFind key store with
    | None -> Stored.Absent
    | Some text ->
      match PassRecord.parse text with
      | Result.Ok record -> Stored.Present record
      | Result.Error reason -> Stored.Unreadable reason

  let decide(decider: Decider) (freshness: Freshness) (tree: TreeState) (spec: TierSpec) (current: PassInputs) (stored: Stored) : Decision =
    match decider with
    | Decider.Real -> PassRecord.decide freshness (Store.At "store") tree spec.Eligibility current stored
    | Decider.IgnoreClosureTwin ->
      let lenient =
        match stored with
        | Stored.Present record -> { current with Closure = record.Inputs.Closure }
        | _ -> current
      PassRecord.decide freshness (Store.At "store") tree spec.Eligibility lenient stored
    | Decider.IgnoreDirtyTreeTwin -> PassRecord.decide freshness (Store.At "store") TreeState.Clean spec.Eligibility current stored
    | Decider.NeverReuseTwin ->
      match PassRecord.decide freshness (Store.At "store") tree spec.Eligibility current stored with
      | Decision.ReuseRecord _ -> Decision.RunTier RunBecause.NoRecord
      | other -> other

  let ledgerRowOf(spec: TierSpec) = sprintf "{\"Tier\":\"%s\",\"Verdict\":\"Trusted\"}" spec.Name

  let runGate(decider: Decider) (state: State) (freshness: Freshness) (outcomes: Outcome list) : State =
    let world = state.World
    let step = state.Step
    let perTier =
      tiers
      |> List.mapi (fun i spec ->
        let current = inputsOf world spec
        let stored = storedAt state.Store (fileOf world spec)
        let decision = decide decider freshness world.Tree spec current stored
        let outcome = outcomes |> List.tryItem i |> Option.defaultValue Outcome.Green
        spec, current, stored, decision, outcome)
    let statuses, store, truth =
      perTier
      |> List.fold
        (fun (statuses, store, truth) (spec, current, _, decision, outcome) ->
          match decision with
          | Decision.ReuseRecord record -> Map.add spec.Name (TierStatus.Reused record) statuses, store, truth
          | Decision.RunTier _ ->
            let statuses = Map.add spec.Name (TierStatus.Executed outcome) statuses
            match outcome, world.Tree with
            | Outcome.Green, TreeState.Clean ->
              let record =
                PassRecord.make current (sprintf "step-%d" step) 1.0 "Trusted" ("1", "1", "1", "0", "0", "0") "ok" (ledgerRowOf spec)
              statuses, Map.add (fileOf world spec) (PassRecord.serialize record) store, Set.add (PassRecord.key current) truth
            | Outcome.Green, TreeState.Dirty ->
              // A green run on a dirty tree is still a green EXECUTION (ground truth), but no record is written for it.
              statuses, store, Set.add (PassRecord.key current) truth
            | Outcome.Red, _ -> statuses, store, truth)
        (Map.empty, state.Store, state.Truth)
    let run =
      { Freshness = freshness
        Tree = world.Tree
        Inputs = perTier |> List.map (fun (spec, current, _, _, _) -> spec.Name, current) |> Map.ofList
        Stored = perTier |> List.map (fun (spec, _, stored, _, _) -> spec.Name, stored) |> Map.ofList
        TruthBefore = state.Truth
        Statuses = statuses }
    { state with Store = store; Truth = truth; Gates = state.Gates @ [ run ] }

  let damage(state: State) (tier: string) (change: string -> string option) : State =
    let key = state.World.Commit, tier
    match Map.tryFind key state.Store with
    | None -> state
    | Some text ->
      match change text with
      | Some changed -> { state with Store = Map.add key changed state.Store }
      | None -> { state with Store = Map.remove key state.Store }

  let step (decider: Decider) (state: State) (event: Event) : State =
    let state = { state with Step = state.Step + 1 }
    let w = state.World
    match event with
    | Event.Commit ->
      { state with
          World =
            { w with
                Commit = w.Commit + 1
                Tree = TreeState.Clean
                Test = w.Test |> Map.map (fun _ v -> v + 1)
                Closure = w.Closure |> Map.map (fun _ v -> v + 1)
                Partition = w.Partition + 1 } }
    | Event.TestBytesChange framework -> { state with World = { w with Test = bump framework w.Test } }
    | Event.ClosureBytesChange framework -> { state with World = { w with Closure = bump framework w.Closure } }
    | Event.SdkUpgrade -> { state with World = { w with Sdk = w.Sdk + 1 } }
    | Event.PartitionDrift -> { state with World = { w with Partition = w.Partition + 1 } }
    | Event.Dirty -> { state with World = { w with Tree = TreeState.Dirty } }
    | Event.Clean -> { state with World = { w with Tree = TreeState.Clean } }
    | Event.Truncate tier -> damage state tier (fun text -> Some(text.Substring(0, text.Length / 2)))
    | Event.Delete tier -> damage state tier (fun _ -> None)
    | Event.Swap(from, into) ->
      match Map.tryFind (w.Commit, from) state.Store with
      | Some text -> { state with Store = Map.add (w.Commit, into) text state.Store }
      | None -> state
    | Event.Gate(freshness, outcomes) -> runGate decider state freshness outcomes

  /// Every state, oldest first, including the initial one.
  let trace (decider: Decider) (scenario: Scenario) : State list =
    scenario.Events |> List.scan (step decider) initial

  let names= tiers |> List.map (fun t -> t.Name) |> Array.ofList
  let frameworks= [| "net10.0"; "net11.0" |]

  /// A pure function of `seed`: replaying a seed gives the identical trace.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 25 + rng.Next 40
    let gate () =
      let freshness = match rng.Next 6 with | 0 -> Freshness.Fresh | _ -> Freshness.Reuse
      // A flaky tier is red about one run in four: the case reuse exists for.
      let outcomes = tiers |> List.map (fun _ -> match rng.Next 4 with | 0 -> Outcome.Red | _ -> Outcome.Green)
      Event.Gate(freshness, outcomes)
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 16 with
          | 0 | 1 | 2 | 3 | 4 -> gate ()
          | 5 -> Event.Commit
          | 6 -> Event.TestBytesChange frameworks[rng.Next frameworks.Length]
          | 7 -> Event.ClosureBytesChange frameworks[rng.Next frameworks.Length]
          | 8 -> Event.SdkUpgrade
          | 9 -> Event.PartitionDrift
          | 10 -> Event.Dirty
          | 11 -> Event.Clean
          | 12 -> Event.Truncate names[rng.Next names.Length]
          | 13 -> Event.Delete names[rng.Next names.Length]
          | 14 -> Event.Swap(names[rng.Next names.Length], names[rng.Next names.Length])
          | _ -> gate () ]
    { Seed = seed; Events = events }
