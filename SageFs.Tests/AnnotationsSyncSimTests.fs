module SageFs.Tests.AnnotationsSyncSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.AnnotationsSyncSim
open SageFs.Simulation.AnnotationsSyncInvariants

/// DST over the protocol that replaced "send every file's annotations on connect": the daemon lists the files
/// that have annotations (`annotations_available`), a client asks for the ones it has open, and the daemon
/// pushes a file only when what it says CHANGED. A client connecting after a file's last push has that file only
/// by asking; so a client that asks on connect and when a file opens must always show what a client that was
/// connected the whole time shows. Seeded chaos is data (a list of changes, connects, drops, opens and closes);
/// the subject is the client's rule, and each twin breaks exactly one half of it.
///
///  - OPEN-FILES-ARE-CURRENT: a connected client's open file shows the version the daemon holds.
///    Broken by a client that does not ask when a file opens, and by one that does not ask on connect.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

/// Seeds swept when asking "does some scenario break this twin".
let private teethSeeds = [ 1 .. 200 ]

let private assertHolds (states: State list) =
  match violations states with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION steps=%d\n  Violations=%A" (List.length states) vs

let private violatedBy (behavior: ClientBehavior) (scenario: Scenario) =
  match openFilesAreCurrent.Check (trace behavior scenario) with
  | Outcome.Violated _ -> true
  | Outcome.Holds -> false

let private holdsFor (behavior: ClientBehavior) (scenario: Scenario) =
  match openFilesAreCurrent.Check (trace behavior scenario) with
  | Outcome.Holds -> ()
  | Outcome.Violated message -> failtestf "%s should hold for %A: %s" openFilesAreCurrent.Id behavior message

let private someSeedBreaks (behavior: ClientBehavior) =
  teethSeeds
  |> List.exists (fun seed -> violatedBy behavior (scenarioOf seed))
  |> Expect.isTrue (sprintf "some seeded scenario must make the %A twin violate %s" behavior openFilesAreCurrent.Id)

[<Tests>]
let tests =
  testList "DST annotations sync" [

    testList "the real client rule holds the invariant" [

      testPropertyWithConfig simConfig "seeded scenarios hold every invariant" <|
        fun (seed: int) -> assertHolds (trace ClientBehavior.Real (scenarioOf seed))

      testProperty "same seed => identical trace (determinism/replay)" <|
        fun (seed: int) ->
          let scenario = scenarioOf seed
          trace ClientBehavior.Real scenario
          |> Expect.equal "replaying the same seed yields the identical states" (trace ClientBehavior.Real scenario)

      testCase "a client that connects after a file's last push gets it by asking, once the file is open" <| fun _ ->
        let scenario =
          { Seed = -701
            Events =
              [ SimEvent.Change "A.fs"
                SimEvent.Connect 0
                SimEvent.Open (0, "A.fs") ] }
        holdsFor ClientBehavior.Real scenario
        let final = List.last (trace ClientBehavior.Real scenario)
        final.Clients.[0].View.["A.fs"] |> Expect.equal "the version the daemon holds" final.Versions.["A.fs"]
    ]

    testList "a client that does not ask when a file opens misses what was pushed before it connected" [

      testCase "REPRODUCED: the file changed before the client connected, and the client opens it afterwards" <| fun _ ->
        let scenario =
          { Seed = -702
            Events =
              [ SimEvent.Change "A.fs"
                SimEvent.Connect 0
                SimEvent.Open (0, "A.fs") ] }
        holdsFor ClientBehavior.Real scenario
        violatedBy ClientBehavior.NoFetchOnOpenTwin scenario
        |> Expect.isTrue "the file is listed but never asked for, and the daemon will not push it again"

      testCase "some seeded scenario makes the twin violate open-files-are-current" <| fun _ ->
        someSeedBreaks ClientBehavior.NoFetchOnOpenTwin
    ]

    testList "a client that does not ask on connect misses what changed while it was away" [

      testCase "REPRODUCED: the file is open, the client drops, the file changes, the client comes back" <| fun _ ->
        let scenario =
          { Seed = -703
            Events =
              [ SimEvent.Connect 0
                SimEvent.Open (0, "A.fs")
                SimEvent.Change "A.fs"
                SimEvent.Disconnect 0
                SimEvent.Change "A.fs"
                SimEvent.Connect 0 ] }
        holdsFor ClientBehavior.Real scenario
        violatedBy ClientBehavior.NoFetchOnConnectTwin scenario
        |> Expect.isTrue "the second change was pushed to nobody, and the reconnect does not ask"

      testCase "some seeded scenario makes the twin violate open-files-are-current" <| fun _ ->
        someSeedBreaks ClientBehavior.NoFetchOnConnectTwin
    ]

    testList "a connected client keeps up by the pushes alone" [

      testCase "change after change of open and unopened files, pushed to the connected client" <| fun _ ->
        let scenario =
          { Seed = -704
            Events =
              [ SimEvent.Change "A.fs"
                SimEvent.Connect 0
                SimEvent.Open (0, "A.fs")
                SimEvent.Change "B.fs"
                SimEvent.Change "B.fs" ] }
        holdsFor ClientBehavior.Real scenario
    ]
  ]
