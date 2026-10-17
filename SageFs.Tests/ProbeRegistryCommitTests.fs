/// `ProbeRegistry.Commit` marks the earlier, never-sighted probes of a function superseded. It used to copy and scan
/// every probe ever allocated to do it, so a long session paid for its whole history on every save. These tests keep
/// that implementation as the oracle and require the registry that scans one function's pending probes to leave
/// every probe in the same state.
module SageFs.Tests.ProbeRegistryCommitTests

open System.Collections.Generic
open Expecto
open Expecto.Flip
open SageFs.Middleware.EntryProbes

/// The registry as it was before `Commit` stopped scanning every probe: the reference the new one must agree with.
type OracleRegistry() =
  let statuses = Dictionary<int64, ProbeStatus>()
  let declarations = Dictionary<int64, string>()
  let mutable nextId = 0L

  member _.Allocate(declaration: string) : EntryProbe =
    nextId <- nextId + 1L
    statuses.[nextId] <- ProbeStatus.NotEntered
    declarations.[nextId] <- declaration
    { Id = nextId; Declaration = declaration }

  member _.Commit(probe: EntryProbe) : unit =
    for KeyValue(id, status) in Seq.toArray statuses do
      match id < probe.Id && status = ProbeStatus.NotEntered && declarations.[id] = probe.Declaration with
      | true -> statuses.[id] <- ProbeStatus.Superseded
      | false -> ()

  member _.Enter(id: int64) : unit =
    match statuses.TryGetValue id with
    | true, ProbeStatus.NotEntered -> statuses.[id] <- ProbeStatus.Entered
    | _ -> ()

  member _.Read(ids: int64 list) : ProbeStatus list =
    ids |> List.map (fun id -> match statuses.TryGetValue id with | true, s -> s | false, _ -> ProbeStatus.NotEntered)

let private declarations = [| "Fixture.alpha"; "Fixture.beta"; "Fixture.gamma" |]

let private pick (n: int) (x: int) : int = ((x % n) + n) % n

/// One step of a history. `a` and `b` are the generator's raw numbers; each step reads them as it needs.
type private Op =
  | Allocate of declaration: int
  | Commit of which: int
  | Enter of which: int

let private opOf (kind: int, a: int, b: int) : Op =
  match pick 4 kind with
  | 0 -> Allocate(pick declarations.Length a)
  | 1 -> Commit b
  | 2 -> Enter b
  // Allocation twice as often as anything else keeps histories long enough to have something to supersede.
  | _ -> Allocate(pick declarations.Length a)

/// Runs a history on both registries and returns the first step at which any probe's state differs.
let private firstDisagreement (history: (int * int * int) list) : string option =
  let current = ProbeRegistry()
  let oracle = OracleRegistry()
  let allocated = List<EntryProbe>()
  let mutable disagreement = None
  let mutable step = 0
  for raw in history do
    step <- step + 1
    match disagreement with
    | Some _ -> ()
    | None ->
      match opOf raw with
      | Allocate d ->
        let a = current.Allocate declarations.[d]
        let b = oracle.Allocate declarations.[d]
        allocated.Add a
        match a = b with
        | true -> ()
        | false -> disagreement <- Some(sprintf "step %d: allocated %A and %A" step a b)
      | Commit which ->
        match allocated.Count with
        | 0 -> ()
        | n ->
          let probe = allocated.[pick n which]
          current.Commit probe
          oracle.Commit probe
      | Enter which ->
        // One past the last id is a probe nobody allocated.
        let id = int64 (pick (allocated.Count + 2) which + 1)
        current.Enter id
        oracle.Enter id
      let ids = [ 1L .. int64 allocated.Count + 1L ]
      let now = (current.Read ids).Sightings |> List.map _.Status
      let expected = oracle.Read ids
      match now = expected, disagreement with
      | false, None -> disagreement <- Some(sprintf "step %d (%A): registry %A, oracle %A" step (opOf raw) now expected)
      | _ -> ()
  disagreement

[<Tests>]
let commitTests =
  testList "ProbeRegistry.Commit against its reference" [
    testProperty "WHY — Commit — every history of allocations, commits and entries leaves every probe in the state the full-scan registry leaves it" <| fun (history: (int * int * int) list) ->
      match firstDisagreement history with
      | None -> ()
      | Some why -> failtestf "%s" why

    testCase "WHY — Commit — a commit supersedes the earlier unsighted probes of its own function and no other" <| fun _ ->
      let registry = ProbeRegistry()
      let a1 = registry.Allocate "Fixture.alpha"
      let b1 = registry.Allocate "Fixture.beta"
      let a2 = registry.Allocate "Fixture.alpha"
      let a3 = registry.Allocate "Fixture.alpha"
      registry.Enter a2.Id
      registry.Commit a3
      (registry.Read [ a1.Id; b1.Id; a2.Id; a3.Id ]).Sightings
      |> List.map _.Status
      |> Expect.equal "alpha's unsighted earlier probe is superseded; the entered one stays entered; beta and the newest are untouched"
        [ ProbeStatus.Superseded; ProbeStatus.NotEntered; ProbeStatus.Entered; ProbeStatus.NotEntered ]

    testCase "WHY — Commit — a probe allocated after the committed one is not superseded by it, because it is the newer body" <| fun _ ->
      let registry = ProbeRegistry()
      let first = registry.Allocate "Fixture.alpha"
      let second = registry.Allocate "Fixture.alpha"
      registry.Commit first
      (registry.Read [ first.Id; second.Id ]).Sightings
      |> List.map _.Status
      |> Expect.equal "nothing earlier than the first, and the second is newer" [ ProbeStatus.NotEntered; ProbeStatus.NotEntered ]
  ]
