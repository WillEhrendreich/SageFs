/// The three host suites that were indivisible, and the cases inside them that never needed to be in sequence.
///
/// `hot reload keeps live state across a save` (26 cases, 371 s), `hot reload parity with .NET Hot Reload` (349 s) and
/// `run_app metadata delta` (264 s) were each one suite in one shard, so a shard's wall was set by whichever of them it
/// held. Their cases are independent processes: each has a run dir, a host and a reserved port of its own. So each is now
/// two suites, one per runtime, which the partition can put in different shards, and the cases inside a suite run a few
/// at a time (`HostSlots`), the way the parity rows already did.
module SageFs.Tests.HostConcurrencyTests

open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests
open SageFs.Tests.HotReloadStateHarness

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The registered host suites whose name starts with `prefix`, each with the names of its cases.
let private suitesNamed (prefix: string) : (string * string list) list =
  Integration.registered ()
  |> List.choose (fun (runner, test) ->
    match runner, test with
    | Integration.Host, Expecto.TestLabel (name, _, _) when name.StartsWith("[Integration] " + prefix, System.StringComparison.Ordinal) ->
      Some (name, Expecto.Test.toTestCodeList test |> List.map (fun flat -> List.last flat.name))
    | _ -> None)

let private families =
  [ "hot reload keeps live state across a save"
    "hot reload parity with .NET Hot Reload"
    "run_app metadata delta" ]

[<Tests>]
let tests =
  testList "Host suite concurrency" [
    testList "host slots" [
      testTask "no more bodies run at once than there are slots, and all of them finish" {
        let slots = HostSlots.create 2
        let inside = ref 0
        let mostInside = ref 0
        let both = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let body (_: int) () : Task<unit> = task {
          let now = Interlocked.Increment inside
          lock mostInside (fun () -> mostInside.Value <- max mostInside.Value now)
          match now with
          | 2 -> both.TrySetResult () |> ignore
          | _ -> ()
          do! release.Task
          Interlocked.Decrement inside |> ignore
        }
        let all = [ for i in 1 .. 6 -> HostSlots.run slots (body i) ] |> Task.WhenAll
        do! both.Task
        // Two are inside, parked on `release`; the other four have to be waiting for a slot.
        inside.Value |> Expect.equal "two bodies inside while four wait" 2
        release.SetResult ()
        do! all
        mostInside.Value |> Expect.equal "never more than the slots" 2
        inside.Value |> Expect.equal "everyone left" 0
      }

      testTask "a body that fails gives its slot back" {
        let slots = HostSlots.create 1
        let! first = (HostSlots.run slots (fun () -> task { failwith "a host that never came up" })).ContinueWith(fun (t: Task<unit>) -> t.IsFaulted)
        first |> Expect.isTrue "the first body failed"
        let! second = HostSlots.run slots (fun () -> task { return 42 })
        second |> Expect.equal "and the next body still got the slot" 42
      }

      testCase "the process's slots are the number a machine of this suite is sized for" <| fun _ ->
        HostSlots.shared.Limit |> Expect.equal "three, as the parity rows always ran" TestMagnitudes.concurrentHosts
    ]

    testList "running a suite's cases concurrently" [
      testCase "a case marked concurrent runs in parallel inside the host entry point's sequenced wrapper, an unmarked one in sequence" <| fun _ ->
        let case = Expecto.Tests.testCase "case" ignore
        let sequencedOf (suiteCases: Expecto.Test list) =
          Expecto.Tests.testSequenced (Expecto.Tests.testList "Integration (host)" suiteCases)
          |> Expecto.Test.toTestCodeList
          |> List.map (fun flat -> flat.sequenced)
          |> List.distinct
        sequencedOf (Integration.concurrentCases [ case; case ])
        |> Expect.equal "parallel, whatever wraps it" [ Expecto.SequenceMethod.InParallel ]
        sequencedOf [ case; case ]
        |> Expect.equal "synchronous, as every host suite was" [ Expecto.SequenceMethod.Synchronous ]

      testCase "wrapping a case keeps its body, so the registry and the default run still find it by identity" <| fun _ ->
        let case = Expecto.Tests.testCase "case" ignore
        let bodyOf (test: Expecto.Test) = (Expecto.Test.toTestCodeList test |> List.head).test
        Integration.concurrentCases [ case ]
        |> List.map bodyOf
        |> List.forall (fun body -> obj.ReferenceEquals(body, bodyOf case))
        |> Expect.isTrue "the same test code"

      testCase "a suite's seconds are spread over the slots it runs in, a sequenced suite's are not" <| fun _ ->
        Integration.suiteWallSeconds Integration.Concurrency.Sequential 90.0 |> Expect.equal "as measured" 90.0
        Integration.suiteWallSeconds Integration.Concurrency.Concurrent 90.0
        |> Expect.equal "the sum of a concurrent suite's cases over the slots" (90.0 / float TestMagnitudes.concurrentHosts)

      testCase "a suite no one registered as concurrent is sequential" <| fun _ ->
        Integration.concurrencyOf "[Integration] never registered" |> Expect.equal "unknown is sequential" Integration.Concurrency.Sequential
    ]

    testList "the three suites, one per runtime" [
      testCase "each family is two host suites, one per runtime, each holding only its own runtime's cases" <| fun _ ->
        for family in families do
          let suites = suitesNamed family
          // The delta family also has the repl-freshness and cost suites under similar names; those are not it.
          let own = suites |> List.filter (fun (name, _) -> not (name.Contains " repl freshness" || name.Contains " cost"))
          own |> List.length |> Expect.equal (sprintf "%s: one suite per runtime" family) (List.length HostRuntime.all)
          for runtime in HostRuntime.all do
            let moniker = HostRuntime.moniker runtime
            let mine = own |> List.filter (fun (name, _) -> name.EndsWith(moniker, System.StringComparison.Ordinal))
            match mine with
            | [ _, cases ] ->
              cases |> List.isEmpty |> Expect.isFalse (sprintf "%s on %s has cases" family moniker)
              cases
              |> List.filter (fun case -> not (case.StartsWith("[" + moniker + "]", System.StringComparison.Ordinal)))
              |> Expect.isEmpty (sprintf "%s on %s holds nothing of another runtime" family moniker)
            | other -> failtestf "%s should have one suite for %s, found %d" family moniker (List.length other)

      testCase "both runtimes of a family have the same number of cases, so nothing was dropped in the split" <| fun _ ->
        for family in families do
          suitesNamed family
          |> List.filter (fun (name, _) -> not (name.Contains " repl freshness" || name.Contains " cost"))
          |> List.map (fun (_, cases) -> List.length cases)
          |> List.distinct
          |> List.length
          |> Expect.equal (sprintf "%s: the same case count on both runtimes" family) 1

      testCase "the state, parity and delta suites run their cases concurrently, and the cost suites stay in sequence" <| fun _ ->
        for family in families do
          for name, _ in suitesNamed family do
            let want =
              match name.Contains " cost" with
              | true -> Integration.Concurrency.Sequential
              | false -> Integration.Concurrency.Concurrent
            Integration.concurrencyOf name |> Expect.equal (sprintf "%s" name) want
    ]
  ]
