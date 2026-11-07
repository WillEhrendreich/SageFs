/// The changes strip: what the session holds that the files do not, and what the program changed on its own. The two never mix. A
/// tweak is the person's decision and can be saved or reverted; a value the program changed would be recorded as the person's
/// decision if it were written, so the strip counts it and Save all and Revert all never touch it.
module SageFs.Tests.ChangesStripTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.Tweak
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Features.Tweak.ChangesStrip
open SageFs.Tests.SharedGenerators
open SageFs.Tests.BindingTweakTests

let private sourceOf (file: string) (binding: string) (text: string) : SourceRef = real file binding text

/// A row with this state, in a file, with whatever control and way the test needs.
let private rowIn (state: PersistenceState) (place: SourceRef) : RowView =
  { State = state
    Control = Control.NoControl
    Place = PlaceOf.Placed place
    Watching = WatchStatus.Watched
    Reload = RowReload.NoWriteYet
    Undo = HistoryStep.NothingToStep
    Redo = HistoryStep.NothingToStep
    Through = ThroughCall.NoCall
    Limits = NumberLimits.unlimited
    Storage = Storage.Mutable
    Push = PushOffer.NotOffered
    Via = PushVia.RebindBinding }

/// One row per state kind, in two files: the shapes a view can hold.
type Shape =
  | Tweaked
  | Evolved
  | InSource
  | Differs

let private stateOf (shape: Shape) (place: SourceRef) : PersistenceState =
  match shape with
  | Shape.Tweaked -> PersistenceState.Tweaked("9.5", place)
  | Shape.Evolved -> PersistenceState.Evolved("62", EvolvedBy.MovedWhileRunning, place)
  | Shape.InSource -> PersistenceState.InSource place
  | Shape.Differs -> PersistenceState.DiffersFromFile("12", place)

let private viewOf (rows: (string * string * Shape) list) : TweakView =
  { Rows =
      rows
      |> List.mapi (fun i (file, name, shape) ->
        let place = sourceOf file name "9.8"
        let key = RowKey.top (sprintf "%s%d" name i)
        key, rowIn (stateOf shape place) place)
      |> Map.ofList }

let genShape : Gen<Shape> = Gen.elements [ Shape.Tweaked; Shape.Evolved; Shape.InSource; Shape.Differs ]

let genRows : Gen<(string * string * Shape) list> =
  Gen.listOf (Gen.zip3 (Gen.elements [ "a.fs"; "b.fs"; "c.fs" ]) (Gen.elements [ "gravity"; "hp"; "speed" ]) genShape)

/// Save all built from every row that is not in step with its file: the twin that would write the program's change as a decision.
let saveAllFromEveryDifference (view: TweakView) : BatchItem list =
  let changes = Changes.ofView view
  let evolvedAsTweaks =
    changes.Evolved
    |> List.map (fun e -> { Row = e.Row; Place = e.Place; Before = e.Initial; After = e.Now; Via = PushVia.RebindBinding })
  Batch.saveAll { changes with Tweaks = changes.Tweaks @ evolvedAsTweaks }

[<Tests>]
let changesTests =
  testList "the changes strip: tweaks and values the program changed, apart" [

    testCase "a tweaked row is a tweak, an evolved row is an evolved value, and a row in step or merely different is neither" <| fun _ ->
      let view = viewOf [ "a.fs", "gravity", Shape.Tweaked; "a.fs", "hp", Shape.Evolved; "a.fs", "speed", Shape.InSource; "b.fs", "gravity", Shape.Differs ]
      let changes = Changes.ofView view
      changes.Tweaks |> List.map (fun t -> t.After) |> Expect.equal "one tweak, with what the session holds" [ "9.5" ]
      changes.Tweaks |> List.map (fun t -> t.Before) |> Expect.equal "and what the file says" [ "9.8" ]
      changes.Evolved |> List.map (fun e -> e.Now) |> Expect.equal "one evolved value" [ "62" ]
      Changes.isEmpty changes |> Expect.isFalse "there is something to say"
      Changes.isEmpty (Changes.ofView (viewOf [ "a.fs", "gravity", Shape.InSource; "a.fs", "hp", Shape.Differs ])) |> Expect.isTrue "nothing to say about rows in step or merely different"

    testCase "tweaks are grouped by file, in file order, so the strip does not reshuffle between pushes" <| fun _ ->
      let view = viewOf [ "b.fs", "gravity", Shape.Tweaked; "a.fs", "hp", Shape.Tweaked; "b.fs", "speed", Shape.Tweaked ]
      Changes.byFile (Changes.ofView view).Tweaks
      |> List.map (fun (file, tweaks) -> file, tweaks.Length)
      |> Expect.equal "a.fs first" [ "a.fs", 1; "b.fs", 2 ]

    testPropertyWithConfig propConfig "WHY — Save all and Revert all are built from the tweaks and nothing else, whatever mix of rows the view holds"
      (Prop.forAll (Arb.fromGen genRows) (fun rows ->
        let view = viewOf rows
        let changes = Changes.ofView view
        let tweakCount = rows |> List.filter (fun (_, _, shape) -> shape = Shape.Tweaked) |> List.length
        let evolvedCount = rows |> List.filter (fun (_, _, shape) -> shape = Shape.Evolved) |> List.length
        (Batch.saveAll changes).Length = tweakCount
        && (Batch.revertAll changes).Length = tweakCount
        && changes.Evolved.Length = evolvedCount
        && Batch.saveAll changes |> List.forall (fun item -> item.Value = "9.5")))

    testCase "the twin that saves every difference fails the law: it writes a value the program changed as if the person had chosen it" <| fun _ ->
      let view = viewOf [ "a.fs", "gravity", Shape.Tweaked; "a.fs", "hp", Shape.Evolved ]
      (Batch.saveAll (Changes.ofView view)).Length |> Expect.equal "the real one saves the tweak only" 1
      (saveAllFromEveryDifference view).Length |> Expect.equal "the twin saves both, which is the mistake the law catches" 2

    testCase "a save request carries what the door needs and the value as the door reads it; a revert carries no value and says how to put the file's back" <| fun _ ->
      let place = sourceOf "a.fs" "gravity" "9.8"
      let view = { Rows = Map.ofList [ RowKey.top "gravity", { rowIn (PersistenceState.Tweaked("9.5", place)) place with Via = PushVia.AssignBinding } ] }
      match Batch.saveAll (Changes.ofView view), Batch.revertAll (Changes.ofView view) with
      | [ save ], [ revert ] ->
        save.Kind |> Expect.equal "save" (BatchKind.token BatchKind.Save)
        save.Row |> Expect.equal "the row" "gravity"
        save.Seen |> Expect.equal "the hash the row showed" place.Hash
        save.SeenText |> Expect.equal "the text the row showed" "9.8"
        save.Value |> Expect.equal "the value" "9.5"
        revert.Kind |> Expect.equal "revert" (BatchKind.token BatchKind.Revert)
        revert.Value |> Expect.equal "no value" ""
        revert.Via |> Expect.equal "the way back into the session" (PushVia.token PushVia.AssignBinding)
      | other -> failtestf "expected one of each, got %A" other

    testCase "every batch kind has its own token and reads back; anything else is refused" <| fun _ ->
      for kind in [ BatchKind.Save; BatchKind.Revert ] do
        BatchKind.ofToken (BatchKind.token kind) |> Expect.equal "round trip" (Ok kind)
      match BatchKind.ofToken "burn" with
      | Error why -> SageFs.ConfigError.describe why |> Expect.stringContains "names the word" "burn"
      | Ok kind -> failtestf "'burn' read as %A" kind
  ]
