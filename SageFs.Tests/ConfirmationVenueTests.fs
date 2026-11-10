module SageFs.Tests.ConfirmationVenueTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkerProtocol

/// Where a confirming build's tests run: the pure decision, `Venue.decide`, and the ledger of what a worker holds.

let private session (n: int) : SessionId =
  match SessionId.validate (sprintf "%08x" n) with
  | Ok id -> id
  | Error reason -> invalidOp reason

let private held (evals: int) (files: string list) : SessionHandWork.Held =
  { HandEvals = evals; BufferFiles = Set.ofList files }

let private factsOf (file: string) (state: SessionHandWork.Held) (startup: RespawnAfterReap.Sequencing) (memory: MemoryHeadroom) : VenueFacts =
  { File = file; Held = state; Startup = startup; Memory = memory }

let private spawnFirst = RespawnAfterReap.Sequencing.SpawnFirst
let private binds = RespawnAfterReap.Sequencing.ReapThenSpawn "init.fsx"

/// A generated set of facts: how much the session holds, where it starts, and how much room there is.
type SomeFacts = SomeFacts of VenueFacts

type FactsGen =
  static member Facts () : Arbitrary<SomeFacts> =
    let files = [ "A.fs"; "B.fs"; "C.fs" ]
    gen {
      let! evals = Gen.choose (0, 3)
      let! buffers = Gen.subListOf files
      let! startsApp = Gen.elements [ true; false ]
      let! short = Gen.elements [ true; false ]
      return
        SomeFacts (
          factsOf
            "A.fs"
            (held evals buffers)
            (match startsApp with | true -> binds | false -> spawnFirst)
            (match short with | true -> MemoryHeadroom.Short "memory is short" | false -> MemoryHeadroom.Roomy))
    }
    |> Arb.fromGen

let private cfg = { FsCheckConfig.defaultConfig with arbitrary = [ typeof<FactsGen> ]; maxTest = 500 }

let private holdsSomething (facts: VenueFacts) : bool =
  facts.Held.HandEvals > 0 || not (List.isEmpty (SessionHandWork.buffersElsewhere facts.File facts.Held))

[<Tests>]
let venueTests =
  testList "confirmation venue" [

    testList "a session that holds nothing is swapped, as before" [
      testCase "no hand eval and only the confirmed file's own buffer" <| fun _ ->
        Venue.decide (factsOf "A.fs" (held 0 [ "A.fs" ]) spawnFirst MemoryHeadroom.Roomy)
        |> Expect.equal "swap" (Venue.SwapInPlace SwapReason.NothingWouldBeLost)

      testCase "it is swapped even when the machine is short on memory, because a swap needs no extra worker" <| fun _ ->
        Venue.decide (factsOf "A.fs" (held 0 []) spawnFirst (MemoryHeadroom.Short "memory is short"))
        |> Expect.equal "swap" (Venue.SwapInPlace SwapReason.NothingWouldBeLost)

      testCase "and when its startup starts an app, because nothing is lost by replacing the worker" <| fun _ ->
        Venue.decide (factsOf "A.fs" (held 0 []) binds MemoryHeadroom.Roomy)
        |> Expect.equal "swap" (Venue.SwapInPlace SwapReason.NothingWouldBeLost)
    ]

    testList "a session that holds something is confirmed in a throwaway worker and keeps it" [
      testCase "a hand eval" <| fun _ ->
        Venue.decide (factsOf "A.fs" (held 2 []) spawnFirst MemoryHeadroom.Roomy)
        |> Expect.equal "throwaway, keeping the definitions" (Venue.ThrowawayWorker [ KeptState.ReplDefinitions 2 ])

      testCase "an evaluated buffer of another file" <| fun _ ->
        Venue.decide (factsOf "A.fs" (held 0 [ "A.fs"; "B.fs" ]) spawnFirst MemoryHeadroom.Roomy)
        |> Expect.equal "throwaway, keeping the other buffer" (Venue.ThrowawayWorker [ KeptState.UnsavedBuffers [ "B.fs" ] ])

      testCase "both are named" <| fun _ ->
        Venue.decide (factsOf "A.fs" (held 1 [ "B.fs"; "C.fs" ]) spawnFirst MemoryHeadroom.Roomy)
        |> Expect.equal "both kept" (Venue.ThrowawayWorker [ KeptState.ReplDefinitions 1; KeptState.UnsavedBuffers [ "B.fs"; "C.fs" ] ])
    ]

    testList "it is refused, never swapped, when a throwaway worker cannot be had" [
      testCase "memory is short" <| fun _ ->
        Venue.decide (factsOf "A.fs" (held 1 []) spawnFirst (MemoryHeadroom.Short "memory is short"))
        |> Expect.equal "refused for memory" (Venue.Refused (ConfirmRefusal.MemoryPressure "memory is short"))

      testCase "its startup starts an app that would fight for its port" <| fun _ ->
        Venue.decide (factsOf "A.fs" (held 1 []) binds MemoryHeadroom.Roomy)
        |> Expect.equal "refused for the startup profile" (Venue.Refused (ConfirmRefusal.StartupBindsAtLoad "init.fsx"))

      testCase "a refusal says what was left alone" <| fun _ ->
        ConfirmRefusal.describe (ConfirmRefusal.MemoryPressure "the machine is almost out of memory")
        |> Expect.stringContains "the sentence says the REPL state was kept" "REPL state was left alone"
    ]

    testList "properties" [
      testPropertyWithConfig cfg "SwapInPlace is returned exactly when the session holds nothing a swap would lose" <| fun (SomeFacts facts) ->
        match Venue.decide facts with
        | Venue.SwapInPlace _ -> not (holdsSomething facts)
        | Venue.ThrowawayWorker _
        | Venue.Refused _ -> holdsSomething facts

      testPropertyWithConfig cfg "a session that holds something never loses it: the answer is a throwaway or a refusal" <| fun (SomeFacts facts) ->
        match holdsSomething facts, Venue.decide facts with
        | true, Venue.SwapInPlace _ -> false
        | _ -> true

      testPropertyWithConfig cfg "the throwaway names exactly what the session holds" <| fun (SomeFacts facts) ->
        match Venue.decide facts with
        | Venue.ThrowawayWorker keeps ->
          let evals = keeps |> List.sumBy (function KeptState.ReplDefinitions n -> n | KeptState.UnsavedBuffers _ -> 0)
          let files = keeps |> List.collect (function KeptState.UnsavedBuffers fs -> fs | KeptState.ReplDefinitions _ -> [])
          evals = facts.Held.HandEvals && files = SessionHandWork.buffersElsewhere facts.File facts.Held
        | Venue.SwapInPlace _
        | Venue.Refused _ -> true

      testPropertyWithConfig cfg "a startup that starts an app or a short machine never gets a throwaway" <| fun (SomeFacts facts) ->
        match Venue.decide facts, facts.Startup, facts.Memory with
        | Venue.ThrowawayWorker _, RespawnAfterReap.Sequencing.ReapThenSpawn _, _ -> false
        | Venue.ThrowawayWorker _, _, MemoryHeadroom.Short _ -> false
        | _ -> true
    ]

    testList "what a message puts into a worker's session" [
      testCase "a REPL eval and a loaded script are hand work" <| fun _ ->
        WorkPut.ofMessage (WorkerMessage.EvalCode ("let x = 1", "r1")) |> Expect.equal "an eval" WorkPut.HandEval
        WorkPut.ofMessage (WorkerMessage.LoadScript ("a.fsx", "r2")) |> Expect.equal "a script" WorkPut.HandEval

      testCase "an evaluated editor buffer names its file" <| fun _ ->
        WorkPut.ofMessage (WorkerMessage.EvalLiveTestFile ("/work/B.fs", "module B", "r3"))
        |> Expect.equal "a buffer" (WorkPut.Buffer "/work/B.fs")

      testCase "a reset empties the session" <| fun _ ->
        WorkPut.ofMessage (WorkerMessage.ResetSession "r4") |> Expect.equal "soft reset" WorkPut.Cleared
        WorkPut.ofMessage (WorkerMessage.HardResetSession (true, "r5")) |> Expect.equal "hard reset" WorkPut.Cleared

      testCase "a status read puts nothing in" <| fun _ ->
        WorkPut.ofMessage (WorkerMessage.GetStatus "r6") |> Expect.equal "nothing" WorkPut.Nothing

      testAsync "a proxy says what it is about to put in, before the message is sent, and not for a read" {
        let said = System.Collections.Generic.List<WorkPut>()
        let sent = System.Collections.Generic.List<string>()
        let proxy : SessionProxy =
          fun _ ->
            async {
              sent.Add "sent"
              return WorkerResponse.WorkerReady
            }
        let noting = SessionProxy.notingWork (fun put -> said.Add put; sent.Add "said") proxy
        let! _ = noting (WorkerMessage.EvalCode ("let x = 1", "r1"))
        let! _ = noting (WorkerMessage.GetStatus "r2")
        List.ofSeq said |> Expect.equal "only the eval was said" [ WorkPut.HandEval ]
        List.ofSeq sent |> Expect.equal "said first, then sent; the read was only sent" [ "said"; "sent"; "sent" ]
      }
    ]

    testList "the store of what each worker holds" [
      testCase "hand evals and buffers add up for the worker, and a reset empties them" <| fun _ ->
        let store = ConfirmWorkers.HandWorkStore()
        let sid = session 7
        store.Note sid 10 WorkPut.HandEval
        store.Note sid 10 WorkPut.HandEval
        store.Note sid 10 (WorkPut.Buffer "B.fs")
        store.HeldBy sid 10 |> Expect.equal "two evals and a buffer" (held 2 [ "B.fs" ])
        store.Note sid 10 WorkPut.Cleared
        store.HeldBy sid 10 |> Expect.equal "emptied by the reset" SessionHandWork.nothing

      testCase "a swap starts from nothing, and the new worker is counted afresh" <| fun _ ->
        let store = ConfirmWorkers.HandWorkStore()
        let sid = session 8
        store.Note sid 10 WorkPut.HandEval
        store.HeldBy sid 11 |> Expect.equal "the replacement holds nothing" SessionHandWork.nothing
        store.Note sid 11 WorkPut.HandEval
        store.HeldBy sid 11 |> Expect.equal "counted for the replacement" (held 1 [])
        store.HeldBy sid 10 |> Expect.equal "and the old worker's count is gone" SessionHandWork.nothing
    ]

    testList "the ledger of what a worker holds" [
      testCase "a worker the ledger has not seen holds nothing" <| fun _ ->
        SessionHandWork.heldBy (session 1) 10 Map.empty
        |> Expect.equal "nothing" SessionHandWork.nothing

      testCase "hand evals and buffers are counted for the worker that was asked" <| fun _ ->
        let ledger =
          Map.empty
          |> SessionHandWork.noteHandEval (session 1) 10
          |> SessionHandWork.noteHandEval (session 1) 10
          |> SessionHandWork.noteBuffer (session 1) 10 "B.fs"
        SessionHandWork.heldBy (session 1) 10 ledger
        |> Expect.equal "two evals and one buffer" (held 2 [ "B.fs" ])

      testCase "a swap starts from nothing: a count for the old worker says nothing about the new one" <| fun _ ->
        let ledger = Map.empty |> SessionHandWork.noteHandEval (session 1) 10
        SessionHandWork.heldBy (session 1) 11 ledger
        |> Expect.equal "the new worker holds nothing" SessionHandWork.nothing
        let counted = ledger |> SessionHandWork.noteHandEval (session 1) 11
        SessionHandWork.heldBy (session 1) 11 counted
        |> Expect.equal "counted afresh" (held 1 [])

      testCase "sessions do not share a count" <| fun _ ->
        let ledger = Map.empty |> SessionHandWork.noteHandEval (session 1) 10
        SessionHandWork.heldBy (session 2) 10 ledger
        |> Expect.equal "the other session holds nothing" SessionHandWork.nothing
    ]
  ]
