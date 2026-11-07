/// The pane's rows, assembled from what the files say, the walked values, what was done to each row and the session's last reload.
/// Real walked values (the live-values walk's own snapshot of real F# values) against real inspections (the nudge door's own
/// listing of a real file), so a row's state is what a user would see for the same session.
module SageFs.Tests.BindingTweakRowsTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Tests.SharedGenerators
open SageFs.Tests.BindingTweakTests

type TuningRecord = { JumpVelocity: float; MaxHealth: int; Scaled: float }

let patience = SageFs.Timeouts.compileQueueWait

let walked (values: (string * obj) list) : LiveValueTree.LiveValueSnapshot =
  LiveValueTree.buildSnapshot "s" 1L (values |> List.map (fun (name, value) -> name, "t", value))

let tuningIndex = indexOf [ inspectedFile "Tuning.fs" tuningFile [ "gravity"; "tuning"; "jump"; "maxHealth" ] ]

let buildRows (index: SourceIndex) (snapshot: LiveValueTree.LiveValueSnapshot) (memory: Memory) : TweakView =
  Rows.build index snapshot memory SessionReload.NoReloadYet 0L patience SageFs.Features.Tweak.LimitsBook.empty

let stateOf (view: TweakView) (key: RowKey) : PersistenceState =
  match TweakView.tryRow view key with
  | Some row -> row.State
  | None -> failtestf "no row for %A in %A" key (view.Rows |> Map.toList |> List.map fst)

let receiptFor (address: TweakAddress) : Receipt =
  { File = "Tuning.fs"
    Address = address
    Before = "9.8"
    After = "10.0"
    HashAfter = String('d', 64)
    FileHashBefore = String('e', 64)
    FileHashAfter = String('f', 64)
    EventId = 1 }

let ranOf (outcome: NudgeOutcome) : Result<Ran, NudgeRefusal> = Ok { Outcome = outcome; Notes = [] }

let gravityRef : SourceRef =
  match SourceIndex.factsFor tuningIndex "gravity" [] with
  | SourceFacts.OneSource place -> place
  | other -> failtestf "gravity: %A" other

let gravityKey = RowKey.top "gravity"

[<Tests>]
let rowTests =
  testList "the pane's rows: every live binding, and the record fields of one the files hold" [

    testCase "a REPL value equal to the file's literal is in source, one that differs says so, one no file declares says it is only in the REPL" <| fun _ ->
      let view = buildRows tuningIndex (walked [ "gravity", box 9.8; "maxHealth", box 250; "speed", box 3 ]) Memory.empty
      stateOf view (RowKey.top "gravity") |> Expect.equal "equal" (PersistenceState.InSource gravityRef)
      match stateOf view (RowKey.top "maxHealth") with
      | PersistenceState.DiffersFromFile(live, place) ->
        live |> Expect.equal "the REPL's value" "250"
        place.Text |> Expect.equal "the file's literal" "100"
      | other -> failtestf "maxHealth: %A" other
      stateOf view (RowKey.top "speed") |> Expect.equal "repl only" (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt)

    testCase "a record the file holds gets a row for itself and one per field, each with its own state and control" <| fun _ ->
      let record = { JumpVelocity = 13.2; MaxHealth = 100; Scaled = 13.5 }
      let view = buildRows tuningIndex (walked [ "tuning", box record ]) Memory.empty
      match stateOf view (RowKey.top "tuning") with
      | PersistenceState.Derived(_, DerivedWhy.AContainer) -> ()
      | other -> failtestf "the record itself: %A" other
      let field name = RowKey.field (RowKey.top "tuning") name
      match stateOf view (field "JumpVelocity") with
      | PersistenceState.InSource place -> place.Text |> Expect.equal "13.2 is in the file" "13.2"
      | other -> failtestf "JumpVelocity: %A" other
      (TweakView.tryRow view (field "MaxHealth")).Value.Control |> Expect.equal "an int knob" (Control.IntegerStepper 100L)
      match stateOf view (field "Scaled") with
      | PersistenceState.Derived(place, DerivedWhy.AFormula) -> place.Text |> Expect.equal "a call is a formula" "scale 3 4.5"
      | other -> failtestf "Scaled: %A" other

    testCase "a binding with no file gets one row, and its parts get none: the binding says it" <| fun _ ->
      let record = { JumpVelocity = 1.0; MaxHealth = 2; Scaled = 3.0 }
      let view = buildRows tuningIndex (walked [ "other", box record ]) Memory.empty
      view.Rows |> Map.toList |> List.map fst |> Expect.equal "just the binding" [ RowKey.top "other" ]

    testCase "a list gets its binding's row only: list items are not mapped yet, and no row pretends they are" <| fun _ ->
      let index = indexOf [ inspectedFile "l.fs" "module L\nlet scores = [ 1; 2; 3 ]\n" [ "scores" ] ]
      let view = buildRows index (walked [ "scores", box [ 1; 2; 3 ] ]) Memory.empty
      view.Rows |> Map.toList |> List.map fst |> Expect.equal "just the binding" [ RowKey.top "scores" ]

    testCase "two files declaring a name give an ambiguous row whose control is none" <| fun _ ->
      let index = indexOf [ inspectedFile "a.fs" "module A\nlet gravity = 9.8\n" [ "gravity" ]; inspectedFile "b.fs" "module B\nlet gravity = 9.8\n" [ "gravity" ] ]
      let view = buildRows index (walked [ "gravity", box 9.8 ]) Memory.empty
      match TweakView.tryRow view gravityKey with
      | Some row ->
        PersistenceState.token row.State |> Expect.equal "named, not picked" "Ambiguous"
        row.Control |> Expect.equal "no knob on a guess" Control.NoControl
      | None -> failtestf "no row"

    testCase "a held value is held whatever the files say" <| fun _ ->
      let held : LiveValueTree.LiveBindingValue =
        { Name = "gravity"
          TypeSignature = "t"
          Root =
            { Label = "gravity"; TypeName = "t"; Preview = "runs code"
              Kind = LiveValueTree.NodeKind.NotEvaluated LiveValueTree.NotEvaluatedReason.GetterRunsCode
              Children = []; BestEffort = false; Assign = LiveValueTree.Assignment.NotKnown; Depth = 0 } }
      let snapshot = { walked [] with Bindings = [ held ] }
      stateOf (buildRows tuningIndex snapshot Memory.empty) gravityKey |> Expect.equal "held" PersistenceState.Held

    testPropertyWithConfig propConfig "every row is a walked binding or a record field under one, and the build never throws"
      (Prop.forAll (arbitrary (Gen.subListOf [ "gravity"; "tuning"; "speed"; "jump"; "maxHealth" ])) (fun names ->
        let value (name: string) : obj =
          match name with
          | "tuning" -> box { JumpVelocity = 13.2; MaxHealth = 100; Scaled = 13.5 }
          | "maxHealth" -> box 100
          | _ -> box 9.8
        let snapshot = walked (names |> List.map (fun n -> n, value n))
        let view = buildRows tuningIndex snapshot Memory.empty
        view.Rows |> Map.forall (fun key _ -> List.contains key.Binding names)))
  ]

type Body = { Mass: float; mutable Hp: int }

[<Tests>]
let storageRowTests =
  let stamped (index: SourceIndex) : SourceIndex =
    { index with Files = index.Files |> List.map (fun file -> { file with Revision = FileRevision.Revised "r1" }) }
  let rowOf (view: TweakView) (key: RowKey) : RowView =
    match TweakView.tryRow view key with
    | Some row -> row
    | None -> failtestf "no row for %A" key
  testList "whether the program can have changed a row's value, and what the row offers to do about it" [

    testCase "a record field the walk saw a setter on is a mutable place and is assigned in place; one without is fixed and copied" <| fun _ ->
      let index = indexOf [ inspectedFile "B.fs" "module B\nlet body = { Mass = 80.0; Hp = 100 }\n" [ "body" ] ]
      let view = buildRows index (walked [ "body", box { Mass = 80.0; Hp = 100 } ]) Memory.empty
      let hp = rowOf view (RowKey.field (RowKey.top "body") "Hp")
      let mass = rowOf view (RowKey.field (RowKey.top "body") "Mass")
      hp.Storage |> Expect.equal "a mutable field" Storage.Mutable
      hp.Via |> Expect.equal "assigned on the instance the program holds" PushVia.AssignField
      mass.Storage |> Expect.equal "a field without `mutable`" Storage.Fixed
      mass.Via |> Expect.equal "copied, because it cannot be assigned" PushVia.RebindWithCopy

    testCase "a binding the file declares `let mutable` that the program then changed is evolved, and the same change to a plain `let` is only a difference" <| fun _ ->
      let mutableIndex = stamped (indexOf [ inspectedFile "T.fs" "module T\nlet mutable counter = 5\n" [ "counter" ] ])
      let inStep = buildRows mutableIndex (walked [ "counter", box 5 ]) Memory.empty
      let seen = Memory.observe mutableIndex inStep Memory.empty
      let moved = buildRows mutableIndex (walked [ "counter", box 7 ]) seen
      match stateOf moved (RowKey.top "counter") with
      | PersistenceState.Evolved(live, EvolvedBy.MovedWhileRunning, _) -> live |> Expect.equal "what the program holds" "7"
      | other -> failtestf "expected Evolved, got %A" other
      (rowOf moved (RowKey.top "counter")).Via |> Expect.equal "a mutable binding is assigned" PushVia.AssignBinding
      let plainIndex = stamped (indexOf [ inspectedFile "T.fs" "module T\nlet counter = 5\n" [ "counter" ] ])
      let plainSeen = Memory.observe plainIndex (buildRows plainIndex (walked [ "counter", box 5 ]) Memory.empty) Memory.empty
      let plainMoved = buildRows plainIndex (walked [ "counter", box 7 ]) plainSeen
      PersistenceState.token (stateOf plainMoved (RowKey.top "counter")) |> Expect.equal "a plain let does not move by itself" "DiffersFromFile"
      (rowOf plainMoved (RowKey.top "counter")).Storage |> Expect.equal "fixed" Storage.Fixed

    testCase "a file edited since the pane last saw the row in step is never blamed on the program, even for a mutable binding" <| fun _ ->
      let first = stamped (indexOf [ inspectedFile "T.fs" "module T\nlet mutable counter = 5\n" [ "counter" ] ])
      let seen = Memory.observe first (buildRows first (walked [ "counter", box 5 ]) Memory.empty) Memory.empty
      let edited =
        { first with Files = first.Files |> List.map (fun file -> { file with Revision = FileRevision.Revised "r2" }) }
      PersistenceState.token (stateOf (buildRows edited (walked [ "counter", box 7 ]) seen) (RowKey.top "counter"))
      |> Expect.equal "either side could have moved it" "DiffersFromFile"

    testCase "a number and a text field offer to try a value; a switch, a formula and a part nothing spells do not" <| fun _ ->
      let view = buildRows tuningIndex (walked [ "gravity", box 9.8; "hard", box true; "jump", box 19.6; "title", box "Nudge"; "speed", box 3 ]) Memory.empty
      (rowOf view (RowKey.top "gravity")).Push |> Expect.equal "a number" PushOffer.Offered
      (rowOf view (RowKey.top "title")).Push |> Expect.equal "a string field" PushOffer.Offered
      (rowOf view (RowKey.top "hard")).Push |> Expect.equal "a switch writes the moment it is flipped" PushOffer.NotOffered
      (rowOf view (RowKey.top "jump")).Push |> Expect.equal "a formula is not a value" PushOffer.NotOffered
      (rowOf view (RowKey.top "speed")).Push |> Expect.equal "no file holds it" PushOffer.NotOffered

    testCase "a row being written offers nothing until the door answers" <| fun _ ->
      let memory = Memory.started gravityKey (WriteKind.SetTo "10.0") gravityRef Memory.empty
      (rowOf (buildRows tuningIndex (walked [ "gravity", box 9.8 ]) memory) gravityKey).Push |> Expect.equal "writing" PushOffer.NotOffered
  ]

[<Tests>]
let memoryTests =
  testList "what the dashboard's own actions did to a row, folded" [

    testCase "a write in flight shows at once as Writing, with the expression the row showed" <| fun _ ->
      let memory = Memory.started gravityKey (WriteKind.SetTo "10.0") gravityRef Memory.empty
      stateOf (buildRows tuningIndex (walked [ "gravity", box 9.8 ]) memory) gravityKey
      |> Expect.equal "writing" (PersistenceState.Writing(WriteKind.SetTo "10.0", gravityRef))

    testCase "a refusal stays on the row until the row is acted on again, with the rule the door gave" <| fun _ ->
      let refusal = NudgeRefusal.LiteralNotReadable(LiteralKindName.Real, "abc")
      let memory = Memory.finished gravityKey gravityRef (Error refusal) SessionReload.NoReloadYet 0L Memory.empty
      match stateOf (buildRows tuningIndex (walked [ "gravity", box 9.8 ]) memory) gravityKey with
      | PersistenceState.Refused(carried, _) -> carried |> Expect.equal "the door's refusal, whole" refusal
      | other -> failtestf "expected Refused, got %A" other
      let again = Memory.started gravityKey (WriteKind.SetTo "10.0") gravityRef memory
      PersistenceState.token (stateOf (buildRows tuningIndex (walked [ "gravity", box 9.8 ]) again) gravityKey)
      |> Expect.equal "acting again replaces it" "Writing"

    testCase "a stale hash is a stale address with both versions: what the row showed and what the file holds now" <| fun _ ->
      let edited = { gravityRef with Text = "9.9"; Hash = String('9', 64) }
      let index = indexOf [ { (inspectedFile "Tuning.fs" tuningFile [ "gravity" ]) with Items = [ edited ] } ]
      let refusal = NudgeRefusal.SourceMoved(gravityRef.Hash, edited.Hash, "9.9")
      let memory = Memory.finished gravityKey gravityRef (Error refusal) SessionReload.NoReloadYet 0L Memory.empty
      match stateOf (buildRows index (walked [ "gravity", box 9.8 ]) memory) gravityKey with
      | PersistenceState.StaleAddress stale ->
        stale.Seen |> Expect.equal "what the row showed" gravityRef
        stale.Now |> Expect.equal "what the file holds" (StaleNow.Edited("9.9", edited.Hash))
      | other -> failtestf "expected StaleAddress, got %A" other

    testCase "a landed write clears the row's outcome, extends the file's trail, and starts watching the reload" <| fun _ ->
      let started = Memory.started gravityKey (WriteKind.SetTo "10.0") gravityRef Memory.empty
      let landed = Memory.finished gravityKey gravityRef (ranOf (NudgeOutcome.Written(receiptFor gravityRef.Address))) SessionReload.NoReloadYet 100L started
      Memory.outcomeOf landed gravityKey |> Expect.equal "nothing pending" RowOutcome.NoOutcome
      (Memory.trailOf landed "Tuning.fs").Applied |> Expect.equal "the trail" [ "Game.Tuning.gravity" ]
      (Map.find gravityKey landed.Writes).At |> Expect.equal "when" 100L

    testCase "a value pushed into the app makes its row tweaked, and the newest push is the one that counts" <| fun _ ->
      // A pushed value is kept as the walk prints it, and the walk prints a float the way F# spells it.
      let pushed = Memory.empty |> Memory.pushed gravityKey (FSharpFloat.text 11.0) |> Memory.pushed gravityKey (FSharpFloat.text 12.0)
      let view = buildRows tuningIndex (walked [ "gravity", box 12.0 ]) pushed
      match stateOf view gravityKey with
      | PersistenceState.Tweaked(live, _) -> live |> Expect.equal "what is live" (FSharpFloat.text 12.0)
      | other -> failtestf "expected Tweaked, got %A" other

    testCase "a pushed value the program has moved off makes its row evolved, and a revert makes it differ again without attribution" <| fun _ ->
      let pushed = Memory.pushed gravityKey "11" Memory.empty
      match stateOf (buildRows tuningIndex (walked [ "gravity", box 12.0 ]) pushed) gravityKey with
      | PersistenceState.Evolved(_, EvolvedBy.OvertookTweak overtook, _) -> overtook |> Expect.equal "what it overtook" "11"
      | other -> failtestf "expected Evolved, got %A" other
      match stateOf (buildRows tuningIndex (walked [ "gravity", box 12.0 ]) (Memory.unpushed gravityKey pushed)) gravityKey with
      | PersistenceState.DiffersFromFile _ -> ()
      | other -> failtestf "expected DiffersFromFile, got %A" other

    testCase "a landed write, an undo and a redo each end the row's push: from then on the file is the record" <| fun _ ->
      let pushed = Memory.pushed gravityKey "12" Memory.empty
      for outcome in [ NudgeOutcome.Written(receiptFor gravityRef.Address)
                       NudgeOutcome.Undone(receiptFor gravityRef.Address)
                       NudgeOutcome.Redone(receiptFor gravityRef.Address) ] do
        let after = Memory.finished gravityKey gravityRef (ranOf outcome) SessionReload.NoReloadYet 0L pushed
        Memory.pushOf after gravityKey |> Expect.equal "nothing pushed any more" PushedValue.NoPush

    testCase "observing a view remembers the file's expression for rows in step, and keeps it for rows that fell out of step" <| fun _ ->
      let stamped = { tuningIndex with Files = tuningIndex.Files |> List.map (fun file -> { file with Revision = FileRevision.Revised "r1" }) }
      let inStep = buildRows stamped (walked [ "gravity", box 9.8 ]) Memory.empty
      let seen = Memory.observe stamped inStep Memory.empty
      Memory.baselineOf seen gravityKey |> Expect.equal "the revision the file was at" (Baseline.InStepAt "r1")
      Memory.baselineOf Memory.empty gravityKey |> Expect.equal "never seen" Baseline.NeverInStep
      let drifted = buildRows stamped (walked [ "gravity", box 12.0 ]) seen
      Memory.observe stamped drifted seen |> Expect.equal "a row out of step keeps its baseline" seen
      let unstamped = Memory.observe tuningIndex inStep seen
      Memory.baselineOf unstamped gravityKey |> Expect.equal "a file that cannot be stamped gives no baseline" Baseline.NeverInStep

    testCase "an undo takes the write off the trail and offers the redo; the redo puts it back" <| fun _ ->
      let wrote = Memory.finished gravityKey gravityRef (ranOf (NudgeOutcome.Written(receiptFor gravityRef.Address))) SessionReload.NoReloadYet 0L Memory.empty
      let undone = Memory.finished gravityKey gravityRef (ranOf (NudgeOutcome.Undone(receiptFor gravityRef.Address))) SessionReload.NoReloadYet 0L wrote
      let view = buildRows tuningIndex (walked [ "gravity", box 9.8 ]) undone
      let row = (TweakView.tryRow view gravityKey).Value
      row.Undo |> Expect.equal "nothing left to undo" HistoryStep.NothingToStep
      row.Redo |> Expect.equal "the redo is this row's" HistoryStep.StepAvailable
      let redone = Memory.finished gravityKey gravityRef (ranOf (NudgeOutcome.Redone(receiptFor gravityRef.Address))) SessionReload.NoReloadYet 0L undone
      (Memory.trailOf redone "Tuning.fs").Applied |> Expect.equal "back" [ "Game.Tuning.gravity" ]

    testCase "an undo reached from another row is recorded against the address it really changed" <| fun _ ->
      let otherKey = RowKey.top "maxHealth"
      let maxAddress = addressOf "maxHealth" []
      let wrote = Memory.finished otherKey gravityRef (ranOf (NudgeOutcome.Written(receiptFor maxAddress))) SessionReload.NoReloadYet 0L Memory.empty
      let undone = Memory.finished gravityKey gravityRef (ranOf (NudgeOutcome.Undone(receiptFor maxAddress))) SessionReload.NoReloadYet 5L wrote
      (Map.find otherKey undone.Writes).At |> Expect.equal "the reload watched is maxHealth's" 5L

    testCase "while a later write is on top, an earlier row's undo says whose turn it is instead of undoing the wrong value" <| fun _ ->
      let g = Memory.finished gravityKey gravityRef (ranOf (NudgeOutcome.Written(receiptFor gravityRef.Address))) SessionReload.NoReloadYet 0L Memory.empty
      let both = Memory.finished (RowKey.top "maxHealth") gravityRef (ranOf (NudgeOutcome.Written(receiptFor (addressOf "maxHealth" [])))) SessionReload.NoReloadYet 1L g
      let view = buildRows tuningIndex (walked [ "gravity", box 9.8; "maxHealth", box 100 ]) both
      (TweakView.tryRow view gravityKey).Value.Undo |> Expect.equal "gravity waits" (HistoryStep.OtherRowFirst "Game.Tuning.maxHealth")
      (TweakView.tryRow view (RowKey.top "maxHealth")).Value.Undo |> Expect.equal "maxHealth is next" HistoryStep.StepAvailable

    testCase "the row says what the app did with its write: the first new verdict, with the declarations it named" <| fun _ ->
      let wrote = Memory.finished gravityKey gravityRef (ranOf (NudgeOutcome.Written(receiptFor gravityRef.Address))) SessionReload.NoReloadYet 0L Memory.empty
      let patched : ReloadFacts =
        { Case = ReloadCase.Patched
          Patched = 1
          Considered = 1
          Message = "patched"
          SuggestedAction = ""
          Mechanism = ReloadOutcome.PatchMechanism.NoPatch
          Declarations = [ "Game.Tuning.gravity" ]
          Callers = CallerState.CallersState.CallersNotReported }
      let view = Rows.build tuningIndex (walked [ "gravity", box 9.8 ]) wrote (SessionReload.Finished patched) 10L patience SageFs.Features.Tweak.LimitsBook.empty
      match (TweakView.tryRow view gravityKey).Value.Reload with
      | RowReload.Watching(file, ReloadWatch.Reported facts) ->
        file |> Expect.equal "the file" "Tuning.fs"
        facts.Declarations |> Expect.equal "the declaration named" [ "Game.Tuning.gravity" ]
      | other -> failtestf "expected the verdict, got %A" other
      let quiet = Rows.build tuningIndex (walked [ "gravity", box 9.8 ]) wrote SessionReload.NoReloadYet (patience.Ticks / 4L) patience SageFs.Features.Tweak.LimitsBook.empty
      (TweakView.tryRow quiet gravityKey).Value.Reload |> Expect.equal "still waiting" (RowReload.Watching("Tuning.fs", ReloadWatch.AwaitingReload))
      let silent = Rows.build tuningIndex (walked [ "gravity", box 9.8 ]) wrote SessionReload.NoReloadYet (patience.Ticks * 2L) patience SageFs.Features.Tweak.LimitsBook.empty
      (TweakView.tryRow silent gravityKey).Value.Reload |> Expect.equal "honest silence" (RowReload.Watching("Tuning.fs", ReloadWatch.NoNewReport))

    testPropertyWithConfig propConfig "write then undo then redo then undo leaves the trail where the door's history fold would: empty, with one redo"
      (Prop.forAll (arbitrary (Gen.choose (1, 4))) (fun n ->
        let wrote memory = Memory.finished gravityKey gravityRef (ranOf (NudgeOutcome.Written(receiptFor gravityRef.Address))) SessionReload.NoReloadYet 0L memory
        let step outcome memory = Memory.finished gravityKey gravityRef (ranOf (outcome (receiptFor gravityRef.Address))) SessionReload.NoReloadYet 0L memory
        let memory =
          [ for _ in 1..n -> () ]
          |> List.fold (fun m () -> wrote m) Memory.empty
          |> step NudgeOutcome.Undone
          |> step NudgeOutcome.Redone
          |> step NudgeOutcome.Undone
        let trail = Memory.trailOf memory "Tuning.fs"
        trail.Applied.Length = n - 1 && trail.Undone.Length = 1))
  ]
