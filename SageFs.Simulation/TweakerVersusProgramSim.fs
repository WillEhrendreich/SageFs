namespace SageFs.Simulation

open SageFs
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows

/// Deterministic Simulation Testing for the claim "the pane never mistakes the program's change for a tweak, never writes a
/// value the program moved without a confirmation, and only calls a value evolved when it can say what moved it".
///
/// One mutable field. A tweaker pushes values into the running app, saves them, resets them and adopts the current value as the
/// initial one; the program assigns the field on its own; someone edits the file with nothing reloading; the dashboard looks
/// (builds the rows and takes note of the rows in step). The question after every op is what the row says and what Save would do.
///
/// The states are the REAL `PersistenceState.deriveWith`, the Save offer is the REAL `SaveOffer.ofState`, and the pane's
/// memory is the REAL `Memory` (`pushed`, `unpushed`, `finished`, `observe`, `baselineOf`). The truth is an independent
/// bookkeeping that never calls any of them: what was last pushed, and the file value the live value last equalled.
///
/// Same rules as the other simulations: chaos is data (a `Scenario` is an op list, a seed replays it), and twins put back the
/// bugs the invariants exist to catch.
module TweakerVersusProgramSim =

  [<RequireQualifiedAccess>]
  type Op =
    /// The dashboard builds the rows and takes note of the ones in step.
    | Looks
    /// The tweaker pushes `value` into the running app. The file does not change.
    | Pushes of value: int
    /// The program assigns the field. Nothing was pushed.
    | ProgramSets of value: int
    /// The tweaker presses Save on the row.
    | Saves
    /// The tweaker resets the row to the value the file holds.
    | Resets
    /// Someone edits the file's literal. Nothing reloads, so the live value stays.
    | EditsFile of value: int
    /// The tweaker uses the current value as the initial one. `confirmed` is the answer to the confirmation.
    | Adopts of confirmed: bool

  type Scenario = { Seed: int; Ops: Op list }

  [<RequireQualifiedAccess>]
  type Policy =
    /// The real derivation and the real memory.
    | Real
    /// TWIN: the program's assignment is recorded as a push.
    | ProgramChangeCountsAsPush
    /// TWIN: Save writes whatever is live, whatever the row says.
    | SavesWhateverIsLive
    /// TWIN: adopting the current value does not wait for the answer.
    | AdoptsWithoutConfirmation
    /// TWIN: the file the live value was last in step with is read as the file as it is now, so an edited file looks unmoved.
    | IgnoresFileEdits

  /// One op's end: what the row said before and after, the world on both sides, and the truth about how the value got here.
  type Snapshot =
    { Op: Op
      Before: PersistenceState
      After: PersistenceState
      LiveBefore: int
      LiveAfter: int
      FileBefore: int
      FileAfter: int
      /// Truth: the value last pushed and not since saved, reset or adopted.
      PushedAfter: int option
      /// Truth: the file value the live value last equalled, when it has.
      AgreedAfter: int option }

  type Trace =
    { Scenario: Scenario
      Snapshots: Snapshot list
      Reducer: string }

  type World =
    { Live: int
      File: int
      /// Which write of the file this is. Every write bumps it, to the same text or another.
      Revision: int
      Memory: Memory
      Pushed: int option
      Agreed: int option }

  let key : RowKey = RowKey.top "speed"

  let hashOf (value: int) : string = sprintf "h%d" value

  let placeOf (value: int) : SourceRef =
    { File = "Speed.fs"
      Address = { ModulePath = [ "Sim" ]; BindingName = "speed"; Path = [] }
      Text = string value
      Hash = hashOf value
      Span = { Line = 1; Column = 0; EndLine = 1; EndColumn = 1 }
      Kind = ItemKind.Knob(LiteralValue.Integer(int64 value)) }

  let receiptFor (world: World) (written: int) : Receipt =
    { File = "Speed.fs"
      Address = (placeOf written).Address
      Before = string world.File
      After = string written
      HashAfter = hashOf written
      FileHashBefore = hashOf world.File
      FileHashAfter = hashOf written
      EventId = 0 }

  let revisionOf (world: World) : FileRevision = FileRevision.Revised(string world.Revision)

  /// The index the dashboard reads: one file, at the revision it is now.
  let indexOf (world: World) : SourceIndex =
    { Files =
        [ { File = "Speed.fs"
            Watching = WatchStatus.Watched
            Items = [ placeOf world.File ]
            Wholes = []
            MutableBindings = Set.empty
            UndoSteps = 0
            RedoSteps = 0
            Revision = revisionOf world } ]
      Unreadable = []
      Applied = ApplicationCounts.NotCounted "the simulation does not count calls" }

  /// What the row says now: the real derivation over the memory the pane holds. The twin that ignores file edits reads the
  /// baseline against the file as it is now.
  let stateOf (policy: Policy) (world: World) : PersistenceState =
    let baseline =
      match policy, Memory.baselineOf world.Memory key with
      | Policy.IgnoresFileEdits, Baseline.InStepAt _ -> Baseline.InStepAt(string world.Revision)
      | _, other -> other
    let provenance =
      { Pushed = Memory.pushOf world.Memory key
        Storage = Storage.Mutable
        Baseline = baseline
        FileNow = revisionOf world }
    PersistenceState.deriveWith provenance (LiveShape.Scalar(string world.Live)) (SourceFacts.OneSource(placeOf world.File)) RowOutcome.NoOutcome

  let viewOfRow (state: PersistenceState) (world: World) : TweakView =
    { Rows =
        Map.ofList
          [ key,
            { State = state
              Control = Control.NoControl
              Place = PlaceOf.Placed(placeOf world.File)
              Watching = WatchStatus.Watched
              Reload = RowReload.NoWriteYet
              Undo = HistoryStep.NothingToStep
              Redo = HistoryStep.NothingToStep
              Through = ThroughCall.NoCall
              Limits = SageFs.Features.Tweak.NumberLimits.unlimited
              Storage = Storage.Mutable
              Push = PushOffer.NotOffered
              Via = PushVia.AssignBinding } ] }

  /// The door took a write of `value` into the file.
  let written (world: World) (value: int) : World =
    let result = Ok { Outcome = NudgeOutcome.Written(receiptFor world value); Notes = [] }
    { world with
        File = value
        Revision = world.Revision + 1
        Memory = Memory.finished key (placeOf world.File) result SessionReload.NoReloadYet 0L world.Memory
        Pushed = None }

  let apply (policy: Policy) (world: World) (op: Op) : World =
    let state = stateOf policy world
    match op with
    | Op.Looks -> { world with Memory = Memory.observe (indexOf world) (viewOfRow state world) world.Memory }
    | Op.Pushes value -> { world with Live = value; Memory = Memory.pushed key (string value) world.Memory; Pushed = Some value }
    | Op.ProgramSets value ->
      match policy with
      | Policy.ProgramChangeCountsAsPush -> { world with Live = value; Memory = Memory.pushed key (string value) world.Memory }
      | Policy.Real
      | Policy.SavesWhateverIsLive
      | Policy.AdoptsWithoutConfirmation
      | Policy.IgnoresFileEdits -> { world with Live = value }
    | Op.EditsFile value -> { world with File = value; Revision = world.Revision + 1 }
    | Op.Saves ->
      match policy with
      | Policy.SavesWhateverIsLive -> written world world.Live
      | Policy.Real
      | Policy.ProgramChangeCountsAsPush
      | Policy.AdoptsWithoutConfirmation
      | Policy.IgnoresFileEdits ->
        match SaveOffer.ofState state with
        | SaveOffer.WriteTweak _ -> written world world.Live
        | SaveOffer.NothingToSave
        | SaveOffer.EditFormula _
        | SaveOffer.ResetOrAdopt _
        | SaveOffer.NoSave -> world
    | Op.Resets ->
      match state with
      | PersistenceState.Tweaked _
      | PersistenceState.Evolved _ -> { world with Live = world.File; Memory = Memory.unpushed key world.Memory; Pushed = None }
      | _ -> world
    | Op.Adopts confirmed ->
      match SaveOffer.ofState state with
      | SaveOffer.ResetOrAdopt _ ->
        match confirmed, policy with
        | true, _
        | false, Policy.AdoptsWithoutConfirmation -> written world world.Live
        | false, _ -> world
      | SaveOffer.NothingToSave
      | SaveOffer.WriteTweak _
      | SaveOffer.EditFormula _
      | SaveOffer.NoSave -> world

  let initial : World =
    { Live = 1; File = 1; Revision = 0; Memory = Memory.empty; Pushed = None; Agreed = Some 1 }

  let runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let step (world: World, acc) (op: Op) =
      let before = stateOf policy world
      let moved = apply policy world op
      let agreed =
        match moved.Live = moved.File with
        | true -> Some moved.File
        | false -> moved.Agreed
      let next = { moved with Agreed = agreed }
      let snapshot =
        { Op = op
          Before = before
          After = stateOf policy next
          LiveBefore = world.Live
          LiveAfter = next.Live
          FileBefore = world.File
          FileAfter = next.File
          PushedAfter = next.Pushed
          AgreedAfter = next.Agreed }
      next, snapshot :: acc
    let _, snapshots = scenario.Ops |> List.fold step (initial, [])
    { Scenario = scenario; Snapshots = List.rev snapshots; Reducer = name }

  let run (scenario: Scenario) : Trace = runWith "real (deriveWith, SaveOffer, Memory)" Policy.Real scenario

  let runProgramChangeCountsAsPush (scenario: Scenario) : Trace =
    runWith "twin-program-change-counts-as-push" Policy.ProgramChangeCountsAsPush scenario

  let runSavesWhateverIsLive (scenario: Scenario) : Trace = runWith "twin-saves-whatever-is-live" Policy.SavesWhateverIsLive scenario

  let runAdoptsWithoutConfirmation (scenario: Scenario) : Trace =
    runWith "twin-adopts-without-confirmation" Policy.AdoptsWithoutConfirmation scenario

  let runIgnoresFileEdits (scenario: Scenario) : Trace = runWith "twin-ignores-file-edits" Policy.IgnoresFileEdits scenario

/// Named invariants over a `TweakerVersusProgramSim.Trace`.
module TweakerVersusProgramInvariants =
  open TweakerVersusProgramSim

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let firstViolation (t: Trace) (violates: int -> Snapshot -> string option) : Outcome =
    t.Snapshots
    |> List.mapi (fun i s -> i, s)
    |> List.tryPick (fun (i, s) -> violates i s)
    |> function
       | Some message -> Outcome.Violated(sprintf "%s (seed=%d, ops=%A)" message t.Scenario.Seed t.Scenario.Ops)
       | None -> Outcome.Holds

  /// tweaked-exactly-when-the-tweaker-moved-it: a row reads tweaked when the live value differs from the file and is the value
  /// last pushed, and in no other case. A program's change to some other value is never a tweak.
  let tweakedExactlyWhenPushed : Invariant =
    { Id = "tweaked-exactly-when-pushed"
      Description = "A row is tweaked exactly when its live value is not the file's and is the value last pushed."
      Check = fun t ->
        firstViolation t (fun i s ->
          let isTweaked =
            match s.After with
            | PersistenceState.Tweaked _ -> true
            | _ -> false
          let shouldBe = s.LiveAfter <> s.FileAfter && s.PushedAfter = Some s.LiveAfter
          match isTweaked = shouldBe with
          | true -> None
          | false -> Some(sprintf "op #%d (%A): tweaked=%b but the truth is %b (live %d, file %d, pushed %A)" i s.Op isTweaked shouldBe s.LiveAfter s.FileAfter s.PushedAfter)) }

  /// evolved-only-when-it-can-say-what-moved-it: overtaking a tweak names the value pushed, and moving without a push needs the
  /// file to be the one the live value last equalled (the same value, which the revision makes certain: an edit that is edited
  /// back is still an edit).
  let evolvedOnlyWithAReason : Invariant =
    { Id = "evolved-only-with-a-reason"
      Description = "An evolved row names a real push it overtook, or has no push and a file unchanged since the value last matched it."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.After with
          | PersistenceState.Evolved(_, EvolvedBy.OvertookTweak pushed, _) ->
            match s.PushedAfter |> Option.map string = Some pushed && pushed <> string s.LiveAfter && s.LiveAfter <> s.FileAfter with
            | true -> None
            | false -> Some(sprintf "op #%d (%A): evolved by overtaking %s, but the truth is pushed=%A, live=%d, file=%d" i s.Op pushed s.PushedAfter s.LiveAfter s.FileAfter)
          | PersistenceState.Evolved(_, EvolvedBy.MovedWhileRunning, _) ->
            match s.PushedAfter = None && s.AgreedAfter = Some s.FileAfter && s.LiveAfter <> s.FileAfter with
            | true -> None
            | false -> Some(sprintf "op #%d (%A): evolved by moving, but the truth is pushed=%A, last agreed file=%A, file=%d, live=%d" i s.Op s.PushedAfter s.AgreedAfter s.FileAfter s.LiveAfter)
          | _ -> None) }

  /// a-save-writes-only-a-tweak: a Save that changed the file changed it from a tweaked row, to the live value.
  let aSaveWritesOnlyATweak : Invariant =
    { Id = "a-save-writes-only-a-tweak"
      Description = "A Save changes the file only from a tweaked row, and writes the live value, so a saved tweak equals what the file holds."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.Op, s.FileAfter <> s.FileBefore with
          | Op.Saves, true ->
            let fromTweak =
              match s.Before with
              | PersistenceState.Tweaked _ -> true
              | _ -> false
            match fromTweak && s.FileAfter = s.LiveBefore with
            | true -> None
            | false -> Some(sprintf "op #%d: Save wrote %d into the file from the state %A with live %d" i s.FileAfter (PersistenceState.token s.Before) s.LiveBefore)
          | _ -> None) }

  /// an-evolved-value-is-written-only-when-confirmed: the only way a value the program moved reaches the file is an adopt that was confirmed.
  let anEvolvedValueIsWrittenOnlyWhenConfirmed : Invariant =
    { Id = "an-evolved-value-is-written-only-when-confirmed"
      Description = "A file write from an evolved row happens only on a confirmed adopt."
      Check = fun t ->
        firstViolation t (fun i s ->
          let wasEvolved =
            match s.Before with
            | PersistenceState.Evolved _ -> true
            | _ -> false
          // Only the pane's own writes count: an edit by a person (`EditsFile`) is theirs to make.
          let paneWrite =
            match s.Op with
            | Op.Saves
            | Op.Adopts _ -> true
            | Op.Looks
            | Op.Pushes _
            | Op.ProgramSets _
            | Op.Resets
            | Op.EditsFile _ -> false
          match paneWrite && wasEvolved && s.FileAfter <> s.FileBefore with
          | false -> None
          | true ->
            match s.Op with
            | Op.Adopts true -> None
            | other -> Some(sprintf "op #%d (%A) wrote the file from an evolved row without a confirmed adopt" i other)) }

  let all : Invariant list =
    [ tweakedExactlyWhenPushed; evolvedOnlyWithAReason; aSaveWritesOnlyATweak; anEvolvedValueIsWrittenOnlyWhenConfirmed ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))

/// Seeded, dependency-free generators for tweaker-versus-program scenarios. Same seed, identical scenario.
module TweakerVersusProgramGenerators =
  open TweakerVersusProgramSim

  /// Values 1 to 3 on purpose: the program and the tweaker land on the same number often enough to be ambiguous.
  let fromSeed (seed: int) : Scenario =
    let rnd = System.Random(seed)
    let n = rnd.Next(8, 40)
    let ops =
      Op.Looks
      :: [ for _ in 1 .. n ->
             match rnd.Next(0, 12) with
             | 0 | 1 -> Op.Looks
             | 2 | 3 -> Op.Pushes(rnd.Next(1, 4))
             | 4 | 5 -> Op.ProgramSets(rnd.Next(1, 4))
             | 6 -> Op.Saves
             | 7 -> Op.Resets
             | 8 -> Op.EditsFile(rnd.Next(1, 4))
             | 9 -> Op.Adopts true
             | 10 -> Op.Adopts false
             | _ -> Op.Saves ]
    { Seed = seed; Ops = ops }

  /// The tweaker pushes 2 and saves it.
  let tweakThenSave : Scenario = { Seed = 910; Ops = [ Op.Looks; Op.Pushes 2; Op.Saves ] }

  /// The tweaker pushes 2 and the program then moves the value to 3.
  let programOvertakesTweak : Scenario = { Seed = 911; Ops = [ Op.Looks; Op.Pushes 2; Op.ProgramSets 3 ] }

  /// The program moves the value with nothing pushed.
  let programChangeAlone : Scenario = { Seed = 912; Ops = [ Op.Looks; Op.ProgramSets 2 ] }

  /// Someone edits the file and nothing reloads: the file moved, the program did not.
  let fileEditedWithNoReload : Scenario = { Seed = 913; Ops = [ Op.Looks; Op.EditsFile 2 ] }

  /// The program moves the value and the tweaker presses Save.
  let saveOnAnEvolvedRow : Scenario = { Seed = 914; Ops = [ Op.Looks; Op.ProgramSets 2; Op.Saves ] }

  /// The program moves the value and the tweaker declines the confirmation.
  let adoptDeclined : Scenario = { Seed = 915; Ops = [ Op.Looks; Op.ProgramSets 2; Op.Adopts false ] }

  /// The program moves the value and the tweaker confirms: the file now holds it.
  let adoptConfirmed : Scenario = { Seed = 916; Ops = [ Op.Looks; Op.ProgramSets 2; Op.Adopts true ] }
