/// How a row's live value relates to the file: `source`, `tweaked`, `evolved`, and the two that need no provenance. The relation is
/// decided from four facts (the live value, the file's literal, the value last pushed, and whether the storage can be assigned), and
/// the laws here are about that decision: a program's change is never taken for a tweak, an evolved value never offers a plain Save,
/// and a value the pane cannot attribute stays what it always was, "differs from the file".
///
/// The domain is small enough to enumerate (three live values, three push states, three storages, three baselines, two file
/// revisions), so each law is checked over
/// every combination. Two twins, each a derivation with one deliberate mistake, must fail the laws: if they pass, the laws prove nothing.
module SageFs.Tests.BindingRelationTests

open Expecto
open Expecto.Flip
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Tests.BindingTweakTests

let literal = real "a.fs" "gravity" "9.8"
let facts = SourceFacts.OneSource literal

let lives = [ "9.8"; "12"; "11" ]
let pushes = [ PushedValue.NoPush; PushedValue.Pushed "12"; PushedValue.Pushed "11" ]
let storages = [ Storage.Mutable; Storage.Fixed; Storage.NotKnown ]

/// Never seen in step, in step with the expression the file holds now, and in step with some other expression (the file was edited since).
let baselines = [ Baseline.NeverInStep; Baseline.InStepAt "r1"; Baseline.InStepAt "r0" ]

/// The file as it is now: written as revision r1, or a file the shell could not stamp.
let fileNows = [ FileRevision.Revised "r1"; FileRevision.Unrevised ]

type Case = { Live: string; Provenance: Provenance }

let everyCase : Case list =
  [ for live in lives do
      for pushed in pushes do
        for storage in storages do
          for baseline in baselines do
            for fileNow in fileNows do
              { Live = live; Provenance = { Pushed = pushed; Storage = storage; Baseline = baseline; FileNow = fileNow } } ]

type Derivation = Case -> PersistenceState

let derivation : Derivation =
  fun case -> PersistenceState.deriveWith case.Provenance (LiveShape.Scalar case.Live) facts RowOutcome.NoOutcome

/// A twin that treats every difference from the file as evolved, the way a derivation from `DiffersFromFile` alone would.
let evolvedFromDifferenceAlone : Derivation =
  fun case ->
    match derivation case with
    | PersistenceState.DiffersFromFile(shown, place) -> PersistenceState.Evolved(shown, EvolvedBy.MovedWhileRunning, place)
    | other -> other

/// A twin that lets the program's change count as a tweak: a row with any pushed value is tweaked, whatever is live.
let programChangeCountsAsTweak : Derivation =
  fun case ->
    match derivation case with
    | PersistenceState.Evolved(shown, EvolvedBy.OvertookTweak _, place) -> PersistenceState.Tweaked(shown, place)
    | other -> other

let isInSource (state: PersistenceState) : bool =
  match state with
  | PersistenceState.InSource _ -> true
  | _ -> false

/// A law holds for a case when it says so; a case it does not speak about holds trivially.
type Law = { Name: string; Holds: Derivation -> Case -> bool }

let notLiteral (case: Case) : bool = case.Live <> "9.8"

let laws : Law list =
  [ { Name = "a live value equal to the file's literal is in source, whatever else is known"
      Holds = fun derive case -> notLiteral case || isInSource (derive case) }
    { Name = "a pushed value that is live and not in the file is tweaked"
      Holds =
        fun derive case ->
          match case.Provenance.Pushed with
          | PushedValue.Pushed pushed when pushed = case.Live && notLiteral case ->
            (match derive case with
             | PersistenceState.Tweaked(shown, _) -> shown = case.Live
             | _ -> false)
          | _ -> true }
    { Name = "a live value that moved off what was pushed is evolved, and names what it overtook"
      Holds =
        fun derive case ->
          match case.Provenance.Pushed with
          | PushedValue.Pushed pushed when pushed <> case.Live && notLiteral case ->
            (match derive case with
             | PersistenceState.Evolved(_, EvolvedBy.OvertookTweak overtook, _) -> overtook = pushed
             | _ -> false)
          | _ -> true }
    { Name = "with nothing pushed, assignable storage, and the file as it was when they last agreed, a different live value is evolved"
      Holds =
        fun derive case ->
          match case.Provenance with
          | { Pushed = PushedValue.NoPush; Storage = Storage.Mutable; Baseline = Baseline.InStepAt seen; FileNow = FileRevision.Revised now } when
              notLiteral case && seen = now
            ->
            (match derive case with
             | PersistenceState.Evolved(_, EvolvedBy.MovedWhileRunning, _) -> true
             | _ -> false)
          | _ -> true }
    { Name = "with nothing pushed, the pane cannot attribute a difference unless the storage is assignable and the file has not moved"
      Holds =
        fun derive case ->
          let attributable =
            match case.Provenance with
            | { Storage = Storage.Mutable; Baseline = Baseline.InStepAt seen; FileNow = FileRevision.Revised now } -> seen = now
            | _ -> false
          match case.Provenance.Pushed with
          | PushedValue.NoPush when notLiteral case && not attributable ->
            (match derive case with
             | PersistenceState.DiffersFromFile(shown, _) -> shown = case.Live
             | _ -> false)
          | _ -> true }
    { Name = "an evolved row never offers a plain save, and a tweaked row offers to write what is live"
      Holds =
        fun derive case ->
          match derive case with
          | PersistenceState.Evolved _ as state ->
            (match SaveOffer.ofState state with
             | SaveOffer.ResetOrAdopt _ -> true
             | _ -> false)
          | PersistenceState.Tweaked _ as state ->
            (match SaveOffer.ofState state with
             | SaveOffer.WriteTweak(value, _) -> value = case.Live
             | _ -> false)
          | _ -> true } ]

/// The names of the laws a derivation breaks on some case.
let broken (derive: Derivation) : string list =
  laws |> List.filter (fun law -> everyCase |> List.exists (fun case -> not (law.Holds derive case))) |> List.map (fun law -> law.Name)

[<Tests>]
let tests =
  testList "binding relations" [
    testCase "the derivation keeps every law over every combination of live value, push and storage" <| fun _ ->
      broken derivation |> Expect.isEmpty "no law is broken"

    testCase "a derivation from the difference alone breaks the law about what the pane cannot attribute" <| fun _ ->
      broken evolvedFromDifferenceAlone
      |> Expect.contains
           "the unattributable difference is broken"
           "with nothing pushed, the pane cannot attribute a difference unless the storage is assignable and the file has not moved"

    testCase "a derivation that lets the program's change count as a tweak breaks the law about what was overtaken" <| fun _ ->
      broken programChangeCountsAsTweak
      |> Expect.contains "the overtaken tweak is broken" "a live value that moved off what was pushed is evolved, and names what it overtook"

    testCase "a row with nothing known derives exactly as it did before provenance existed" <| fun _ ->
      for live in lives do
        PersistenceState.derive (LiveShape.Scalar live) facts RowOutcome.NoOutcome
        |> Expect.equal "the same state" (derivation { Live = live; Provenance = Provenance.unknown })

    testCase "a formula, a missing file and a held value are decided by the file and the value, never by what was pushed" <| fun _ ->
      let formula = sourceAt "a.fs" "jump" "gravity * 2.0" ItemKind.Formula
      for pushed in pushes do
        for storage in storages do
          let provenance = { Pushed = pushed; Storage = storage; Baseline = Baseline.NeverInStep; FileNow = FileRevision.Unrevised }
          PersistenceState.deriveWith provenance (LiveShape.Scalar "19.6") (SourceFacts.OneSource formula) RowOutcome.NoOutcome
          |> Expect.equal "a formula" (PersistenceState.Derived(formula, DerivedWhy.AFormula))
          PersistenceState.deriveWith provenance (LiveShape.Scalar "12") SourceFacts.NoFileBindsIt RowOutcome.NoOutcome
          |> Expect.equal "no file" (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt)
          PersistenceState.deriveWith provenance LiveShape.NotRead facts RowOutcome.NoOutcome
          |> Expect.equal "held" PersistenceState.Held
  ]
