/// Rule 2's reflection reads: the pure half.
///
/// A module value read through reflection after startup has no read in anyone's
/// IL, so the startup window's getter watch is the only thing that ever saw it.
/// These pin what the ledger does with a reflective read once the runtime has
/// caught one (in whichever `ReflectionReadMode` the session runs), and the
/// hot-loop notice that asks the user which mode they want.
module SageFs.Tests.ReflectionReadsTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Middleware.ValueReads

let private value = "App.State.greeting"

let private ledgerWith (events: LedgerEvent list) =
  (LedgerEvent.ValueTracked value :: events) |> List.fold Ledger.step Ledger.empty

let private verdict (ledger: Ledger) = Ledger.evidence ledger value |> verdictOf

let private afterStartup (events: LedgerEvent list) = ledgerWith (LedgerEvent.StartupEnded :: events)

[<Tests>]
let ledgerTests =
  testList "reflection reads: what the ledger makes of one" [
    testCase "WHY — a reflective read whose call site throws the value away holds nothing, so the value can still be patched" <| fun _ ->
      afterStartup [ LedgerEvent.ReflectiveRead(value, ReflectiveCaller.AtSite("App.Log.tick", 0x12, ReadFate.Discarded)) ]
      |> verdict
      |> Expect.equal "thrown away at the site" ValueVerdict.SafeToPatch

    testCase "WHY — a reflective read whose call site keeps the value is a copy, and the verdict names the caller and the site" <| fun _ ->
      let kept = ReadFate.Escaped(Escape.StoredInField "App.Log.last")
      match afterStartup [ LedgerEvent.ReflectiveRead(value, ReflectiveCaller.AtSite("App.Log.tick", 0x12, kept)) ] |> verdict with
      | ValueVerdict.HeldBy(site, seen) ->
        site.Reader |> Expect.equal "names the caller" "App.Log.tick"
        site.Where |> Expect.equal "and the reflection call in its code" (SiteLocation.ILOffset 0x12)
        site.Fate |> Expect.equal "and what it did with it" kept
        seen |> Expect.equal "after the app started" ReadSeen.AfterStartup
      | other -> failtestf "a kept reflective read should hold the value, got %A" other

    testCase "WHY — a reflective read SageFs didn't attribute (MarkOnReflect, or a caller it couldn't read) is a copy, and says why" <| fun _ ->
      match afterStartup [ LedgerEvent.ReflectiveRead(value, ReflectiveCaller.Unattributed "the session marks reflective reads without looking for the caller") ] |> verdict with
      | ValueVerdict.HeldBy(site, ReadSeen.AfterStartup) ->
        site.Where |> Expect.equal "no read of it in anyone's code" SiteLocation.NotInItsCode
        describeHolder site ReadSeen.AfterStartup
        |> Expect.stringContains "the restart reason carries the why" "without looking for the caller"
      | other -> failtestf "an unattributed reflective read should hold the value, got %A" other

    testCase "WHY — a reflective read during startup is filed as a startup read" <| fun _ ->
      match ledgerWith [ LedgerEvent.ReflectiveRead(value, ReflectiveCaller.Unattributed "x") ] |> verdict with
      | ValueVerdict.HeldBy(_, seen) -> seen |> Expect.equal "the window was open" ReadSeen.AtStartup
      | other -> failtestf "expected a hold, got %A" other

    testCase "WHY — the same reflective caller filed twice is one sighting, so a hot loop doesn't grow the ledger" <| fun _ ->
      let read = LedgerEvent.ReflectiveRead(value, ReflectiveCaller.AtSite("App.Log.tick", 0x12, ReadFate.Discarded))
      let once = afterStartup [ read ]
      let many = afterStartup (List.replicate 50 read)
      many |> Expect.equal "filing it again changes nothing" once

    testCase "WHY — one kept site among thrown-away ones still holds the value" <| fun _ ->
      afterStartup
        [ LedgerEvent.ReflectiveRead(value, ReflectiveCaller.AtSite("A.f", 1, ReadFate.Discarded))
          LedgerEvent.ReflectiveRead(value, ReflectiveCaller.AtSite("B.g", 2, ReadFate.Escaped Escape.Returned)) ]
      |> verdict
      |> function
        | ValueVerdict.HeldBy(site, _) -> site.Reader |> Expect.equal "B.g kept it" "B.g"
        | other -> failtestf "expected a hold, got %A" other
  ]

[<Tests>]
let modeTests =
  testList "reflection reads: the mode" [
    testCase "WHY — every mode's config name parses back to that mode, so a config file and the MCP tool can't drift" <| fun _ ->
      for mode in ReflectionReadMode.all do
        ReflectionReadMode.parse (ReflectionReadMode.name mode)
        |> Expect.equal (sprintf "%A round-trips" mode) (Result.Ok mode)

    testCase "WHY — an unknown mode name is refused, and the refusal lists the names that work" <| fun _ ->
      match ReflectionReadMode.parse "fastest" with
      | Result.Ok m -> failtestf "'fastest' isn't a mode, got %A" m
      | Result.Error unknown ->
        for mode in ReflectionReadMode.all do
          ReflectionReadMode.describeUnknown unknown |> Expect.stringContains "names every real mode" (ReflectionReadMode.name mode)

    testCase "WHY — the default is ProbeCallers: the spike measured its steady state at about 38 ns a read, against 9,000 for a cached walk" <| fun _ ->
      ReflectionReadMode.standard |> Expect.equal "probe callers by default" ReflectionReadMode.ProbeCallers

    testCase "WHY — every mode states what it costs and what it does to an edit, in one plain line each" <| fun _ ->
      for mode in ReflectionReadMode.all do
        let lines = [ ReflectionReadMode.cost mode; ReflectionReadMode.consequence mode ]
        for line in lines do
          line |> Expect.isNotEmpty (sprintf "%A says something" mode)
          line.Contains "\n" |> Expect.isFalse "one line"
          line.Contains "—" |> Expect.isFalse "no em dash"
  ]

let private threshold = { Count = 5; Within = TimeSpan.FromSeconds 1.0 }

let private ticks (ms: float) = int64 (TimeSpan.FromMilliseconds ms).Ticks

/// Feed `stamps` (ms) to a fresh rate and return every reading.
let private readings (stamps: float list) =
  let rate = ReadRate threshold
  stamps |> List.map (fun ms -> rate.Observe(ticks ms))

let private isHot (reading: RateReading) =
  match reading with
  | RateReading.HotLoop _ -> true
  | RateReading.Quiet -> false

[<Tests>]
let rateTests =
  testList "reflection reads: the hot-loop rate" [
    testCase "WHY — five reads inside a second cross a five-a-second threshold on the fifth" <| fun _ ->
      readings [ 0.0; 100.0; 200.0; 300.0; 400.0 ]
      |> List.map isHot
      |> Expect.equal "hot on the fifth read only" [ false; false; false; false; true ]

    testCase "WHY — the same five reads spread over more than a second never cross it" <| fun _ ->
      readings [ 0.0; 300.0; 600.0; 900.0; 1200.0 ]
      |> List.exists isHot
      |> Expect.isFalse "a slow trickle isn't a hot loop"

    testCase "WHY — the reading carries the rate it saw, in reads a second" <| fun _ ->
      match readings [ 0.0; 1.0; 2.0; 3.0; 4.0 ] |> List.last with
      | RateReading.HotLoop perSecond -> (perSecond, 1000) |> Expect.isGreaterThanOrEqual "a millisecond apart is a thousand a second or more"
      | RateReading.Quiet -> failtest "should be hot"

    testProperty "WHY — hot exactly when the last `Count` reads fit in `Within`, over any gaps (sliding window, not buckets)" <| fun (gaps: NonNegativeInt list) ->
      let stamps = gaps |> List.scan (fun at (NonNegativeInt gap) -> at + float (gap % 700)) 0.0
      let got = readings stamps |> List.map isHot
      let expected =
        stamps
        |> List.mapi (fun i at ->
          i + 1 >= threshold.Count
          && at - stamps.[i + 1 - threshold.Count] <= threshold.Within.TotalMilliseconds)
      got = expected
  ]

[<Tests>]
let tieringChoiceTests =
  testList "reflection reads: the tiering choice" [
    testProperty "WHY — every choice round-trips through its name, and nothing outside the closed set parses" <| fun (keepTiering: bool) ->
      let choice = if keepTiering then TieringChoice.KeepTiering else TieringChoice.TieringOffWhileWatching
      match TieringChoice.parse (TieringChoice.name choice) with
      | Result.Ok back -> back = choice
      | Result.Error e -> failtestf "%A should parse back: %A" choice e

    testCase "WHY — a name outside the closed set is refused, and it names what would have worked" <| fun _ ->
      match TieringChoice.parse "off" with
      | Result.Ok choice -> failtestf "'off' isn't a choice, got %A" choice
      | Result.Error unknown ->
        unknown.Known |> Expect.equal "the real choices" (TieringChoice.all |> List.map TieringChoice.name)
        TieringChoice.describeUnknown unknown |> Expect.stringContains "names them" "keep-tiering"
  ]

let private notice mode =
  { Value = value; Caller = "App.Log.tick"; ReadsPerSecond = 12000; Mode = mode }

[<Tests>]
let noticeTests =
  testList "reflection reads: asking the user, once" [
    testCase "WHY — the first hot loop on a value asks, and says so with one notice" <| fun _ ->
      let state, raised = ReflectionNotices.step NoticeState.Unasked (NoticeEvent.HotLoopSeen(notice ReflectionReadMode.MarkOnReflect))
      state |> Expect.equal "asked" (NoticeState.Asked(notice ReflectionReadMode.MarkOnReflect))
      raised |> Expect.equal "one notice" [ notice ReflectionReadMode.MarkOnReflect ]

    testCase "WHY — a value already asked about is never asked again, however hot it stays" <| fun _ ->
      let asked = NoticeState.Asked(notice ReflectionReadMode.MarkOnReflect)
      let state, raised = ReflectionNotices.step asked (NoticeEvent.HotLoopSeen(notice ReflectionReadMode.MarkOnReflect))
      state |> Expect.equal "still asked" asked
      raised |> Expect.isEmpty "no second notice"

    testCase "WHY — choosing a mode after being asked records the answer, and it stays answered" <| fun _ ->
      let asked = NoticeState.Asked(notice ReflectionReadMode.MarkOnReflect)
      let chosen, _ = ReflectionNotices.step asked (NoticeEvent.ModeChosen ReflectionReadMode.ProbeCallers)
      chosen |> Expect.equal "chosen" (NoticeState.Chosen(notice ReflectionReadMode.MarkOnReflect, ReflectionReadMode.ProbeCallers))
      let later, raised = ReflectionNotices.step chosen (NoticeEvent.HotLoopSeen(notice ReflectionReadMode.ProbeCallers))
      later |> Expect.equal "still chosen" chosen
      raised |> Expect.isEmpty "an answered question isn't asked again"

    testCase "WHY — choosing a mode before anything was asked answers nothing, so a later hot loop still asks" <| fun _ ->
      let state, _ = ReflectionNotices.step NoticeState.Unasked (NoticeEvent.ModeChosen ReflectionReadMode.ExactEveryRead)
      state |> Expect.equal "nothing was asked" NoticeState.Unasked

    testCase "WHY — the notice names the value, the caller and the rate, says what the current mode costs, and offers every mode with its consequence" <| fun _ ->
      for mode in ReflectionReadMode.all do
        let text = ReflectionNotices.describe (notice mode)
        text |> Expect.stringContains "names the value" value
        text |> Expect.stringContains "names the caller" "App.Log.tick"
        text |> Expect.stringContains "gives the rate" "12000"
        text |> Expect.stringContains "says what the current mode costs" (ReflectionReadMode.cost mode)
        for choice in ReflectionReadMode.all do
          text |> Expect.stringContains "offers each mode" (ReflectionReadMode.name choice)
          text |> Expect.stringContains "with its consequence" (ReflectionReadMode.consequence choice)
        text.Contains "—" |> Expect.isFalse "no em dash"
  ]
