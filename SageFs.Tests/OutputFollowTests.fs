module SageFs.Tests.OutputFollowTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Server.DashboardTypes

// Chat-style scrolling in the dashboard output panel. The browser journeys in
// DashboardBrowserTests prove the scrolling itself; these pin the F# half: the
// server-side eval count the pill is built on, and the pill's words.

let private finished sid = TuiEvent.EvalCompleted (sid, "val it: int = 1", [])
let private failed sid = TuiEvent.EvalFailed (sid, "boom")
let private cancelled sid = TuiEvent.EvalCancelled sid
let private started sid = TuiEvent.EvalStarted (sid, "1 + 1;;")

let private apply (model: SageFsModel) (ev: TuiEvent) =
  SageFsUpdate.update (SageFsMsg.Event ev) model |> fst

[<Tests>]
let tests = testList "Output panel chat-style following" [
  testList "finished-eval count" [
    testCase "completed, failed and cancelled evals each count once for their session" <| fun _ ->
      let model =
        [ finished "aaaa0001"; failed "aaaa0001"; cancelled "aaaa0001"; finished "bbbb0002" ]
        |> List.fold apply (SageFsModel.initial ())
      EvalTally.count "aaaa0001" model.EvalsFinished |> Expect.equal "three finished in a" 3
      EvalTally.count "bbbb0002" model.EvalsFinished |> Expect.equal "one finished in b" 1
      EvalTally.count "cccc0003" model.EvalsFinished |> Expect.equal "none in a session that never ran" 0

    testCase "a started eval hasn't finished yet" <| fun _ ->
      let model = apply (SageFsModel.initial ()) (started "aaaa0001")
      EvalTally.count "aaaa0001" model.EvalsFinished |> Expect.equal "started isn't finished" 0

    testCase "a failure with no session (like a failed create) isn't anybody's eval" <| fun _ ->
      let model = apply (SageFsModel.initial ()) (failed "")
      EvalTally.toList model.EvalsFinished |> Expect.isEmpty "no session, no count"

    testCase "clearing the output doesn't reset the count, so the pill can't go negative" <| fun _ ->
      let model = apply (SageFsModel.initial ()) (finished "aaaa0001")
      let cleared =
        SageFsUpdate.update (SageFsMsg.Editor EditorAction.ClearOutput) model |> fst
      EvalTally.count "aaaa0001" cleared.EvalsFinished |> Expect.equal "count survives a clear" 1

    testProperty "the count only goes up, and by exactly the number of finished evals" <| fun (kinds: bool list) ->
      let events = kinds |> List.map (fun ok -> match ok with | true -> finished "aaaa0001" | false -> started "aaaa0001")
      let counts =
        events
        |> List.scan apply (SageFsModel.initial ())
        |> List.map (fun m -> EvalTally.count "aaaa0001" m.EvalsFinished)
      let monotonic = counts |> List.pairwise |> List.forall (fun (a, b) -> b >= a)
      monotonic && List.last counts = (kinds |> List.filter id |> List.length)
  ]

  testList "pill" [
    testProperty "unseen is the difference, never negative" <| fun (finishedN: int) (seen: int) ->
      let unseen = OutputFollow.unseenEvals finishedN seen
      unseen >= 0 && (finishedN <= seen || unseen = finishedN - seen)

    testCase "one unseen eval reads singular" <| fun _ ->
      OutputFollow.pillLabel 1 |> Expect.equal "singular" "1 new eval ↓"

    testProperty "any other count reads plural" <| fun (n: PositiveInt) ->
      let n = n.Get + 1
      OutputFollow.pillLabel n = sprintf "%d new evals ↓" n

    testCase "the browser's label expression uses the same words as pillLabel" <| fun _ ->
      // The browser computes the label from the signals; both spellings come
      // from the same constants, so check they both show up.
      OutputFollow.pillLabelExpr |> Expect.stringContains "singular words" "' new eval'"
      OutputFollow.pillLabelExpr |> Expect.stringContains "plural words" "' new evals'"
  ]

  testList "content revision" [
    testProperty "same lines, same revision" <| fun (texts: NonNull<string> list) ->
      let lines = texts |> List.map (fun t -> { Timestamp = None; Kind = ResultLine; Text = t.Get })
      OutputFollow.contentRev lines = OutputFollow.contentRev lines

    testCase "a new line changes the revision" <| fun _ ->
      let one = [ { Timestamp = Some "10:00:00"; Kind = ResultLine; Text = "val x: int = 1" } ]
      let two = one @ [ { Timestamp = Some "10:00:01"; Kind = ResultLine; Text = "val y: int = 2" } ]
      OutputFollow.contentRev two |> Expect.notEqual "appending output moves the revision" (OutputFollow.contentRev one)

    testCase "the same text arriving again still moves the revision" <| fun _ ->
      let line = { Timestamp = Some "10:00:00"; Kind = ResultLine; Text = "val it: unit = ()" }
      OutputFollow.contentRev [ line; line ] |> Expect.notEqual "a repeated line is still new output" (OutputFollow.contentRev [ line ])
  ]
]
