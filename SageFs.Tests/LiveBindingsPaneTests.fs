/// What the live-bindings pane knows beyond the tree: the walk mode the session is in, what the last click came to, how
/// many rows are listed and not read, and how a clicked binding's new tree replaces the old one. All pure, so the
/// dashboard, the daemon routes and the editors say the same thing.
module SageFs.Tests.LiveBindingsPaneTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.LiveValueTree
open SageFs.FsiHost.FsiProtocol

let private node label kind children : LiveValueNode =
  { Label = label
    TypeName = "T"
    Preview = "p"
    Kind = kind
    Children = children
    BestEffort = false
    Depth = 0 }

let private held label reason = node label (NodeKind.NotEvaluated reason) []

let private binding name root : LiveBindingValue =
  { Name = name; TypeSignature = "T"; Root = root }

let private snapshotOf (bindings: LiveBindingValue list) : LiveValueSnapshot =
  { SessionId = "s1"
    Generation = 3L
    Bindings = bindings
    Truncated = false
    CapturedAt = DateTimeOffset.UnixEpoch }

let private later = DateTimeOffset.UnixEpoch.AddMinutes 5.0

[<Tests>]
let countTests =
  testList "rows listed but not read" [
    testCase "a snapshot with nothing held counts zero" <| fun _ ->
      snapshotOf [ binding "a" (node "a" NodeKind.Leaf []) ]
      |> LiveBindingsPane.notEvaluatedCount
      |> Expect.equal "none" 0

    testCase "held rows count at every depth and in every binding, whatever the reason" <| fun _ ->
      let tree =
        node "box" NodeKind.Class
          [ held "Slow" NotEvaluatedReason.GetterRunsCode
            node "inner" NodeKind.Record [ held "Loops" NotEvaluatedReason.GetterLoops; node "ok" NodeKind.Leaf [] ] ]
      snapshotOf [ binding "box" tree; binding "other" (held "Seq" NotEvaluatedReason.SequenceNotEnumerated) ]
      |> LiveBindingsPane.notEvaluatedCount
      |> Expect.equal "three held rows" 3
  ]

[<Tests>]
let replaceTests =
  testList "a click's answer replaces that binding's tree" [
    testCase "the named binding gets the new tree, in place, and the others are untouched" <| fun _ ->
      let before = snapshotOf [ binding "a" (held "x" NotEvaluatedReason.GetterRunsCode); binding "b" (node "b" NodeKind.Leaf []) ]
      let after = binding "a" (node "a" NodeKind.Class [ node "x" NodeKind.Leaf [] ])
      match LiveBindingsPane.replaceBinding later after before with
      | LiveBindingsPane.Replaced replaced ->
        replaced.Bindings |> List.map (fun b -> b.Name) |> Expect.equal "same order" [ "a"; "b" ]
        replaced.Bindings.[0] |> Expect.equal "a is the new tree" after
        replaced.Bindings.[1] |> Expect.equal "b is as it was" before.Bindings.[1]
        replaced.Generation |> Expect.equal "the generation is the walk's, not the click's" before.Generation
        replaced.CapturedAt |> Expect.equal "stamped when the click answered" later
      | other -> failtestf "expected Replaced, got %A" other

    testCase "a binding the snapshot does not have is said so, not added" <| fun _ ->
      let before = snapshotOf [ binding "a" (node "a" NodeKind.Leaf []) ]
      match LiveBindingsPane.replaceBinding later (binding "gone" (node "gone" NodeKind.Leaf [])) before with
      | LiveBindingsPane.NotInSnapshot name -> name |> Expect.equal "named" "gone"
      | other -> failtestf "expected NotInSnapshot, got %A" other
  ]

[<Tests>]
let containmentTests =
  let shown containment = MemberShown(binding "a" (node "a" NodeKind.Leaf []), containment)
  testList "the containment line of the last click" [
    testCase "before any click there is no line" <| fun _ ->
      LiveBindingsPane.containmentLine LiveBindingsPane.NoClickYet
      |> Expect.equal "nothing to say" LiveBindingsPane.NothingClickedYet

    testCase "a getter that ran under the syscall filter says so" <| fun _ ->
      match LiveBindingsPane.containmentLine (LiveBindingsPane.ClickAnswered(shown (ContainedBy SandboxPolicy.NoNetworkNoWritesNoSpawn))) with
      | LiveBindingsPane.LineSays text -> text |> Expect.stringContains "the filter" "ran under a syscall filter"
      | other -> failtestf "expected a line, got %A" other

    testCase "a getter that ran with no filter says there is no I/O containment, and why" <| fun _ ->
      match LiveBindingsPane.containmentLine (LiveBindingsPane.ClickAnswered(shown (NotContained NotLinux))) with
      | LiveBindingsPane.LineSays text ->
        text |> Expect.stringStarts "leads with the fact" "no I/O containment here: "
        text |> Expect.stringContains "carries the reason" (SandboxUnavailable.describe NotLinux)
      | other -> failtestf "expected a line, got %A" other

    testCase "a refused or unavailable click says why, and a vanished binding is named" <| fun _ ->
      [ MemberRefused ClassesAreCollapsed
        MemberRefused EveryGetterAlreadyRan
        MemberUnavailable NoIsolatedHost
        MemberUnavailable(HostNotRunning "exited")
        BindingNotFound "gone" ]
      |> List.iter (fun outcome ->
        match LiveBindingsPane.containmentLine (LiveBindingsPane.ClickAnswered outcome) with
        | LiveBindingsPane.LineSays text -> text |> Expect.isNotEmpty (sprintf "%A is explained" outcome)
        | LiveBindingsPane.NothingClickedYet -> failtestf "%A said nothing" outcome)
  ]

[<Tests>]
let readingTests =
  testList "what a worker answers a live-values pull with" [
    testCase "the mode, who chose it and the snapshot survive the wire, held rows and all" <| fun _ ->
      let reading : LiveBindingsPane.LiveValuesReading =
        { Mode = WalkEverything
          Origin = LiveBindingsPane.ChosenByUser
          Snapshot = snapshotOf [ binding "a" (held "x" (NotEvaluatedReason.EvaluationThrew "boom")) ] }
      WorkerProtocol.Serialization.serialize reading
      |> WorkerProtocol.Serialization.tryDeserialize<LiveBindingsPane.LiveValuesReading>
      |> Expect.equal "round trip" (Result.Ok reading)

    testCase "the reading built around snapshot JSON reads back as the same reading, with no second pass over the tree" <| fun _ ->
      let snapshot = snapshotOf [ binding "a" (held "x" NotEvaluatedReason.GetterLoops) ]
      LiveBindingsPane.readingJson WalkOff LiveBindingsPane.StartedWithDefault (WorkerProtocol.Serialization.serialize snapshot)
      |> WorkerProtocol.Serialization.tryDeserialize<LiveBindingsPane.LiveValuesReading>
      |> Expect.equal "same reading" (Result.Ok({ Mode = WalkOff; Origin = LiveBindingsPane.StartedWithDefault; Snapshot = snapshot } : LiveBindingsPane.LiveValuesReading))

    testCase "a worker that was never told a mode is advised to take the config's, once" <| fun _ ->
      let reading origin mode : LiveBindingsPane.LiveValuesReading = { Mode = mode; Origin = origin; Snapshot = snapshotOf [] }
      LiveBindingsPane.adviseConfigured WalkEverything (reading LiveBindingsPane.StartedWithDefault WalkSafe)
      |> Expect.equal "apply it" (LiveBindingsPane.ApplyConfigured WalkEverything)
      LiveBindingsPane.adviseConfigured WalkEverything (reading LiveBindingsPane.ChosenByUser WalkSafe)
      |> Expect.equal "a user's choice is never overruled by the config" LiveBindingsPane.LeaveModeAsItIs
      LiveBindingsPane.adviseConfigured WalkSafe (reading LiveBindingsPane.StartedWithDefault WalkSafe)
      |> Expect.equal "already what the config says" LiveBindingsPane.LeaveModeAsItIs
  ]

[<Tests>]
let storeTests =
  testList "what the pane remembers per session" [
    testCase "a session nobody has touched is in Safe mode with no click" <| fun _ ->
      let store = LiveBindingsPane.PaneStore.create ()
      LiveBindingsPane.PaneStore.notesOf store "s1"
      |> Expect.equal "defaults" ({ Mode = WalkSafe; Click = LiveBindingsPane.NoClickYet } : LiveBindingsPane.PaneNotes)

    testCase "a click is remembered, and a mode switch forgets it because the clicked row is gone" <| fun _ ->
      let store = LiveBindingsPane.PaneStore.create ()
      let click = LiveBindingsPane.ClickAnswered(BindingNotFound "x")
      LiveBindingsPane.PaneStore.recordClick store "s1" click
      (LiveBindingsPane.PaneStore.notesOf store "s1").Click |> Expect.equal "remembered" click
      LiveBindingsPane.PaneStore.setMode store "s1" WalkEverything
      LiveBindingsPane.PaneStore.notesOf store "s1"
      |> Expect.equal "new mode, no stale line" ({ Mode = WalkEverything; Click = LiveBindingsPane.NoClickYet } : LiveBindingsPane.PaneNotes)

    testCase "sessions do not share notes" <| fun _ ->
      let store = LiveBindingsPane.PaneStore.create ()
      LiveBindingsPane.PaneStore.setMode store "s1" WalkOff
      (LiveBindingsPane.PaneStore.notesOf store "s2").Mode |> Expect.equal "s2 untouched" WalkSafe
  ]

// ---- the daemon's feed: pulls, clicks and mode switches, against a fake worker ----

type private FakeWorker(initial: LiveBindingsPane.LiveValuesReading) =
  let mutable reading = initial
  let sent = System.Collections.Generic.List<WorkerProtocol.WorkerMessage>()
  let mutable outcome : MemberOutcome = BindingNotFound "unset"
  member _.Sent = sent |> Seq.toList
  member _.ClickAnswers(answer: MemberOutcome) = outcome <- answer
  member _.Ask : LiveBindingsPane.Feed.Ask =
    fun message ->
      async {
        sent.Add message
        match message with
        | WorkerProtocol.WorkerMessage.GetLiveValues rid ->
          return Result.Ok(WorkerProtocol.WorkerResponse.LiveValuesResult(rid, WorkerProtocol.Serialization.serialize reading))
        | WorkerProtocol.WorkerMessage.SetValueWalk(mode, rid) ->
          reading <- { reading with Mode = mode; Origin = LiveBindingsPane.ChosenByUser }
          return Result.Ok(WorkerProtocol.WorkerResponse.LiveValuesResult(rid, WorkerProtocol.Serialization.serialize reading))
        | WorkerProtocol.WorkerMessage.EvaluateLiveMember(_, _, rid) ->
          return Result.Ok(WorkerProtocol.WorkerResponse.LiveMemberResult(rid, WorkerProtocol.Serialization.serialize outcome))
        | other -> return Result.Error(SageFsError.WorkerCommunicationFailed("s1", sprintf "unexpected %A" other))
      }

let private readingOf origin mode bindings : LiveBindingsPane.LiveValuesReading =
  { Mode = mode; Origin = origin; Snapshot = snapshotOf bindings }

let private stores () = Features.LiveBindingsAdaptive.create (), LiveBindingsPane.PaneStore.create ()

[<Tests>]
let feedTests =
  testList "the daemon's live-bindings feed" [
    testAsync "a pull puts the snapshot in the store under the session's id and remembers the mode the worker walked in" {
      let adaptive, notes = stores ()
      let worker = FakeWorker(readingOf LiveBindingsPane.ChosenByUser WalkOff [ binding "a" (node "a" NodeKind.Leaf []) ])
      do! LiveBindingsPane.Feed.pull worker.Ask adaptive notes "s1" (fun () -> WalkSafe)
      (Features.LiveBindingsAdaptive.tryGet adaptive "s1").Value.SessionId |> Expect.equal "stamped" "s1"
      (LiveBindingsPane.PaneStore.notesOf notes "s1").Mode |> Expect.equal "the worker's mode" WalkOff
    }

    testAsync "a worker nobody has told gets the config's mode, once, and the store shows the reading taken after it" {
      let adaptive, notes = stores ()
      let worker = FakeWorker(readingOf LiveBindingsPane.StartedWithDefault WalkSafe [ binding "a" (node "a" NodeKind.Leaf []) ])
      do! LiveBindingsPane.Feed.pull worker.Ask adaptive notes "s1" (fun () -> WalkEverything)
      worker.Sent
      |> List.exists (function WorkerProtocol.WorkerMessage.SetValueWalk(WalkEverything, _) -> true | _ -> false)
      |> Expect.isTrue "the config's choice was sent"
      (LiveBindingsPane.PaneStore.notesOf notes "s1").Mode |> Expect.equal "now Everything" WalkEverything
      do! LiveBindingsPane.Feed.pull worker.Ask adaptive notes "s1" (fun () -> WalkEverything)
      worker.Sent
      |> List.filter (function WorkerProtocol.WorkerMessage.SetValueWalk _ -> true | _ -> false)
      |> List.length
      |> Expect.equal "told once, not on every eval" 1
    }

    testAsync "a worker whose mode a user chose is never overruled by the config" {
      let adaptive, notes = stores ()
      let worker = FakeWorker(readingOf LiveBindingsPane.ChosenByUser WalkSafe [])
      do! LiveBindingsPane.Feed.pull worker.Ask adaptive notes "s1" (fun () -> WalkEverything)
      worker.Sent
      |> List.exists (function WorkerProtocol.WorkerMessage.SetValueWalk _ -> true | _ -> false)
      |> Expect.isFalse "nothing was sent"
    }

    testAsync "a click whose row now has a value replaces that binding's tree, notes how it was contained and keeps the generation" {
      let adaptive, notes = stores ()
      let worker = FakeWorker(readingOf LiveBindingsPane.ChosenByUser WalkSafe [ binding "box" (held "Size" NotEvaluatedReason.GetterRunsCode); binding "other" (node "other" NodeKind.Leaf []) ])
      do! LiveBindingsPane.Feed.pull worker.Ask adaptive notes "s1" (fun () -> WalkSafe)
      let after = binding "box" (node "box" NodeKind.Class [ node "Size" NodeKind.Leaf [] ])
      worker.ClickAnswers(MemberShown(after, NotContained NotLinux))
      let! outcome = LiveBindingsPane.Feed.evaluateMember worker.Ask adaptive notes "s1" "box" [ "Size" ]
      outcome |> Expect.equal "answered" (Result.Ok(MemberShown(after, NotContained NotLinux)))
      let stored = (Features.LiveBindingsAdaptive.tryGet adaptive "s1").Value
      stored.Bindings.[0] |> Expect.equal "replaced" after
      stored.Generation |> Expect.equal "same walk" 3L
      (LiveBindingsPane.PaneStore.notesOf notes "s1").Click
      |> Expect.equal "remembered" (LiveBindingsPane.ClickAnswered(MemberShown(after, NotContained NotLinux)))
    }

    testAsync "a refused click still reaches every subscriber, because its line changed" {
      let adaptive, notes = stores ()
      let worker = FakeWorker(readingOf LiveBindingsPane.ChosenByUser WalkEverything [ binding "box" (node "box" NodeKind.Class []) ])
      do! LiveBindingsPane.Feed.pull worker.Ask adaptive notes "s1" (fun () -> WalkEverything)
      let pushes = ref 0
      use _subscription = Features.LiveBindingsAdaptive.subscribe adaptive "s1" (fun _ -> pushes.Value <- pushes.Value + 1)
      let before = pushes.Value
      worker.ClickAnswers(MemberRefused EveryGetterAlreadyRan)
      let! _ = LiveBindingsPane.Feed.evaluateMember worker.Ask adaptive notes "s1" "box" [ "Size" ]
      Expect.isGreaterThan "pushed again" (pushes.Value, before)
      (LiveBindingsPane.PaneStore.notesOf notes "s1").Click
      |> Expect.equal "remembered" (LiveBindingsPane.ClickAnswered(MemberRefused EveryGetterAlreadyRan))
    }

    testAsync "a mode switch asks the worker, stores the new reading and clears the old click line" {
      let adaptive, notes = stores ()
      let worker = FakeWorker(readingOf LiveBindingsPane.ChosenByUser WalkSafe [ binding "box" (node "box" NodeKind.Class []) ])
      do! LiveBindingsPane.Feed.pull worker.Ask adaptive notes "s1" (fun () -> WalkSafe)
      LiveBindingsPane.PaneStore.recordClick notes "s1" (LiveBindingsPane.ClickAnswered(BindingNotFound "x"))
      let! _ = LiveBindingsPane.Feed.setMode worker.Ask adaptive notes "s1" WalkEverything
      LiveBindingsPane.PaneStore.notesOf notes "s1"
      |> Expect.equal "new mode, no stale line" ({ Mode = WalkEverything; Click = LiveBindingsPane.NoClickYet } : LiveBindingsPane.PaneNotes)
    }

    testAsync "a worker that cannot be reached is an error, not an empty pane" {
      let adaptive, notes = stores ()
      let unreachable : LiveBindingsPane.Feed.Ask = fun _ -> async { return Result.Error(SageFsError.WorkerCommunicationFailed("s1", "gone")) }
      match! LiveBindingsPane.Feed.evaluateMember unreachable adaptive notes "s1" "box" [ "Size" ] with
      | Result.Error _ -> ()
      | Result.Ok other -> failtestf "expected an error, got %A" other
    }
  ]
