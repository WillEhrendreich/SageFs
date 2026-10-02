/// The dashboard's trunk panel: what the trunk did with each landing, in the cohort block, rendered from the machine alone.
module SageFs.Tests.TrunkPanelTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.Features
open SageFs.Features.TrunkFollow
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private landing (n: int) : LandedLanding = { Landing = LandingId (sprintf "l-%d" n); Commit = sprintf "c%d00000000" n }

let private facts (case: ReloadCase) (mechanism: ReloadOutcome.PatchMechanism) : ReloadFacts =
  { Case = case; Patched = 0; Considered = 1; Message = "m"; SuggestedAction = ""; Mechanism = mechanism; Declarations = [] }

let private followed (n: int) (verdicts: FileVerdict list) (machine: TrunkMachine) : TrunkMachine =
  let moving, _ = step machine (TrunkEvent.Landed (landing n))
  let delivering, _ =
    step moving (TrunkEvent.Moved ((landing n).Landing, TrunkMove.Moved ([], [ { Session = "s1"; State = TrunkSessionState.Serving } ])))
  let closed, _ = step delivering (TrunkEvent.Answered ((landing n).Landing, "s1", SessionOutcome.Delivered verdicts))
  closed

[<Tests>]
let trunkPanelTests =
  testList "Trunk panel" [
    testCase "before any landing lands nothing is drawn" <| fun _ ->
      (renderNode (renderTrunkPanel initial)).Contains DomIds.CohortTrunk |> Expect.isFalse "no panel"

    testCase "each landing shows the file, the outcome and the mechanism the worker reported" <| fun _ ->
      let patch : FileVerdict =
        { File = "/trunk/Alice.fs"; Outcome = FileOutcome.Reloaded (facts ReloadCase.PatchPending ReloadOutcome.PatchMechanism.MetadataDelta, []) }
      let restart : FileVerdict =
        { File = "/trunk/Rude.fs"
          Outcome =
            FileOutcome.Reloaded
              (facts ReloadCase.Restarted ReloadOutcome.PatchMechanism.NoPatch, [ { Case = "TypeShapeChanged"; Message = "the shape of type Shape changed" } ]) }
      let machine = initial |> followed 1 [ patch ] |> followed 2 [ restart ]
      let html = renderNode (renderTrunkPanel machine)
      html |> Expect.stringContains "the panel" DomIds.CohortTrunk
      html |> Expect.stringContains "the count" "Trunk: 2 landings followed"
      html |> Expect.stringContains "the patch and its mechanism" "Alice.fs PatchPending by metadata-delta"
      html |> Expect.stringContains "the restart and its cause" "Rude.fs Restarted (TypeShapeChanged: the shape of type Shape changed)"

    testCase "a landing being followed and one queued behind it are listed as such" <| fun _ ->
      let moving, _ = step initial (TrunkEvent.Landed (landing 1))
      let queued, _ = step moving (TrunkEvent.Landed (landing 2))
      let html = renderNode (renderTrunkPanel queued)
      html |> Expect.stringContains "the one being followed" "following: moving the trunk checkout"
      html |> Expect.stringContains "the one waiting" "queued behind the landing in flight"

    testCase "a runtime string in a verdict is escaped, never markup" <| fun _ ->
      let hostile : FileVerdict = { File = "/trunk/<script>.fs"; Outcome = FileOutcome.NeedsRebuild "<img src=x onerror=alert(1)>" }
      let html = renderNode (renderTrunkPanel (initial |> followed 1 [ hostile ]))
      html.Contains "<img src=x" |> Expect.isFalse "the reason is text"
  ]
