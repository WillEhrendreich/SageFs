/// Which session a tab views after a teardown (stop / dispose / purge).
///
/// The dashboard used to move every tab to "the next session in the list" whenever a teardown
/// succeeded, so stopping a stale BACKGROUND card changed what the viewer was reading. The two
/// session test that covers the handler could not show it: with two sessions the wrong branch
/// lands on the right answer by luck. These cases use four.
module SageFs.Tests.DashboardTeardownViewTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Server.Dashboard

let private sid (n: int) : WorkerProtocol.SessionId =
  WorkerProtocol.SessionId.validate (sprintf "%08x" (0x0a000000 + n))
  |> Result.defaultValue (WorkerProtocol.SessionId.newId ())

let private raw (id: WorkerProtocol.SessionId) = WorkerProtocol.SessionId.value id

let private a, b, c, d = sid 1, sid 2, sid 3, sid 4
let private four = [ a; b; c; d ]

[<Tests>]
let tests =
  testList "teardown view decision" [
    testCase "WHY — stopping a background session leaves the viewer on the session they are reading" <| fun _ ->
      ViewAfterTeardown.decide TeardownOutcome.TornDown c (raw a) four four
      |> Expect.equal "viewing a, stopped c: stay on a" (ViewAfterTeardown.StayOn a)

    testCase "WHY — stopping the viewed session moves to the one after it in registry order" <| fun _ ->
      ViewAfterTeardown.decide TeardownOutcome.TornDown c (raw c) four four
      |> Expect.equal "viewing c, stopped c: the next one is d" (ViewAfterTeardown.MoveTo d)

    testCase "WHY — stopping the viewed last session wraps to the first rather than to nothing" <| fun _ ->
      ViewAfterTeardown.decide TeardownOutcome.TornDown d (raw d) four four
      |> Expect.equal "viewing d, stopped d: wrap to a" (ViewAfterTeardown.MoveTo a)

    testCase "WHY — stopping the only session leaves none, so the picker shows" <| fun _ ->
      ViewAfterTeardown.decide TeardownOutcome.TornDown a (raw a) [ a ] [ a ]
      |> Expect.equal "nothing left" ViewAfterTeardown.NoSessionLeft

    testCase "WHY — a stop that failed never moves the viewer, whichever session it was" <| fun _ ->
      ViewAfterTeardown.decide TeardownOutcome.TeardownFailed c (raw c) four four
      |> Expect.equal "viewing c, stop of c failed: stay on c" (ViewAfterTeardown.StayOn c)
      ViewAfterTeardown.decide TeardownOutcome.TeardownFailed c (raw a) four four
      |> Expect.equal "viewing a, stop of c failed: stay on a" (ViewAfterTeardown.StayOn a)

    testCase "WHY — a tab on the picker stays on the picker when a card is stopped" <| fun _ ->
      ViewAfterTeardown.decide TeardownOutcome.TornDown c "" four four
      |> Expect.equal "no viewing session: stay on the picker" ViewAfterTeardown.StayOnPicker

    testCase "WHY — the registry no longer listing the stopped session does not change the answer, because the order is read from before" <| fun _ ->
      ViewAfterTeardown.decide TeardownOutcome.TornDown c (raw c) four [ a; b; d ]
      |> Expect.equal "c already gone from the list: still d" (ViewAfterTeardown.MoveTo d)

    testCase "WHY — a session created while the stop ran is a fallback, never a reason to land nowhere" <| fun _ ->
      let fresh = sid 9
      ViewAfterTeardown.decide TeardownOutcome.TornDown a (raw a) [ a ] [ fresh ]
      |> Expect.equal "only the new session remains" (ViewAfterTeardown.MoveTo fresh)

    testProperty "WHY — the viewer only ever moves when the session they are viewing is the one stopped" <| fun (count: byte) (tornAt: byte) (viewAt: byte) ->
      let n = 3 + int count % 6
      let ids = [ for i in 1 .. n -> sid i ]
      let torn = ids.[int tornAt % n]
      let viewing = ids.[int viewAt % n]
      match ViewAfterTeardown.decide TeardownOutcome.TornDown torn (raw viewing) ids ids with
      | ViewAfterTeardown.MoveTo target -> viewing = torn && target <> torn
      | ViewAfterTeardown.StayOn kept -> kept = viewing && viewing <> torn
      | ViewAfterTeardown.StayOnPicker -> false
      | ViewAfterTeardown.NoSessionLeft -> false
  ]
