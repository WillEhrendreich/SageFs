/// A failing render push must be visible.
///
/// A failing `pushState` was logged at Debug and dropped while the heartbeat kept the page
/// "connected", so the content froze with no banner and nothing in a default log. The streak is
/// a pure value: how many pushes in a row failed decides what is logged and what the page says.
module SageFs.Tests.DashboardPushStreakTests

open Expecto
open Expecto.Flip
open SageFs.Server.Dashboard

let private fail (streak: PushStreak) (reason: string) = PushStreak.failed streak reason

[<Tests>]
let tests =
  testList "push failure streak" [
    testCase "WHY — the first failure is logged at Warn once and the page is not alarmed by a single blip" <| fun _ ->
      let streak, effect = fail PushStreak.healthy "boom"
      effect |> Expect.equal "logged" (PushEffect.LogStreakStart "boom")
      streak |> Expect.equal "one failure so far" (PushStreak.Failing (1, "boom"))

    testCase "WHY — the second failure in a row puts a visible notice on the page, naming the reason" <| fun _ ->
      let s1, _ = fail PushStreak.healthy "boom"
      let s2, effect = fail s1 "boom again"
      effect |> Expect.equal "the notice" (PushEffect.ShowNotice "boom again")
      s2 |> Expect.equal "two in a row" (PushStreak.Failing (2, "boom again"))

    testCase "WHY — failures after the notice neither re-log nor re-post, so a long outage is one line and one notice" <| fun _ ->
      let s1, _ = fail PushStreak.healthy "a"
      let s2, _ = fail s1 "b"
      let s3, effect3 = fail s2 "c"
      let _, effect4 = fail s3 "d"
      effect3 |> Expect.equal "third is quiet" PushEffect.NoEffect
      effect4 |> Expect.equal "fourth is quiet" PushEffect.NoEffect

    testCase "WHY — a push that works after a streak ends it and says the page is rendering again, but only if the notice was shown" <| fun _ ->
      let s1, _ = fail PushStreak.healthy "a"
      let s2, _ = fail s1 "b"
      let healthy2, effect2 = PushStreak.succeeded s2
      healthy2 |> Expect.equal "healthy again" PushStreak.healthy
      effect2 |> Expect.equal "the notice is retired" PushEffect.ClearNotice
      let healthy1, effect1 = PushStreak.succeeded s1
      healthy1 |> Expect.equal "healthy again" PushStreak.healthy
      effect1 |> Expect.equal "a single blip left nothing to clear" PushEffect.NoEffect

    testCase "WHY — a streak that ended starts fresh, so the next outage is logged again" <| fun _ ->
      let s1, _ = fail PushStreak.healthy "a"
      let recovered, _ = PushStreak.succeeded s1
      let _, effect = fail recovered "later"
      effect |> Expect.equal "logged again" (PushEffect.LogStreakStart "later")

    testCase "WHY — the notice text states that the dashboard failed to render and why" <| fun _ ->
      PushStreak.noticeText "no such column"
      |> Expect.equal "plain words" "dashboard failed to render: no such column"
  ]
