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
