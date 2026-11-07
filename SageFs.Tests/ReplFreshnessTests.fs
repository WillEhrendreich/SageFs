/// After a metadata delta lands in a run_app worker, the app runs the new code and the REPL (and live tests) run the
/// build from before it. That gap is a closed state the session carries, not a note in a doc: `ReplFreshness`.
///
/// This is the pure part: how the state moves (a delta that landed makes it BehindApp, nothing else does, a replaced worker
/// clears it), what it says, and how it travels on the wire. The surfaces that show it have their own tests.
module SageFs.Tests.ReplFreshnessTests

open System.Text.Json
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features.ReloadOutcome

let private facts (case: ReloadCase) (mechanism: PatchMechanism) (declarations: string list) : ReloadFacts =
  { Case = case
    Patched = 1
    Considered = 1
    Message = "m"
    SuggestedAction = ""
    Mechanism = mechanism
    Declarations = declarations
    Callers = SageFs.Features.CallerState.CallersState.CallersCurrent }

let private finished (case: ReloadCase) (mechanism: PatchMechanism) (declarations: string list) : SessionReload =
  SessionReload.Finished (facts case mechanism declarations)

/// A save a delta took: pending first, then confirmed. Both say the same declarations.
let private deltaPending (declarations: string list) = finished ReloadCase.PatchPending PatchMechanism.MetadataDelta declarations
let private deltaPatched (declarations: string list) = finished ReloadCase.Patched PatchMechanism.MetadataDelta declarations

let private observeAll (reloads: SessionReload list) : ReplFreshness =
  reloads |> List.fold ReplFreshness.observe ReplFreshness.InSync

[<Tests>]
let tests =
  testList "ReplFreshness" [

    testCase "WHY - a session nobody has saved to is in sync: the REPL and the app run the same build" <| fun _ ->
      observeAll [] |> Expect.equal "nothing happened" ReplFreshness.InSync
      observeAll [ SessionReload.NoReloadYet; SessionReload.Compiling (Some "A.fs") ]
      |> Expect.equal "compiling is not a landed save" ReplFreshness.InSync

    testCase "WHY - a delta that landed in the worker puts the REPL behind the app, counting the save and naming what changed" <| fun _ ->
      observeAll [ deltaPending [ "Handlers.describe" ] ]
      |> Expect.equal "one save" (ReplFreshness.BehindApp (1, [ "Handlers.describe" ]))

    testCase "WHY - a save is counted once: its pending report and its confirmation are the same save" <| fun _ ->
      observeAll [ deltaPending [ "Handlers.describe" ]; deltaPatched [ "Handlers.describe" ] ]
      |> Expect.equal "still one save" (ReplFreshness.BehindApp (1, [ "Handlers.describe" ]))

    testCase "WHY - a confirmation seen without its pending report (the daemon joined late) still puts the REPL behind, because silence is the one thing this state must not be" <| fun _ ->
      observeAll [ deltaPatched [ "Handlers.describe" ] ]
      |> Expect.equal "behind, counted as one save" (ReplFreshness.BehindApp (1, [ "Handlers.describe" ]))
      observeAll [ finished ReloadCase.NeverEntered PatchMechanism.MetadataDelta [ "Handlers.describe" ] ]
      |> Expect.equal "never-entered is applied too" (ReplFreshness.BehindApp (1, [ "Handlers.describe" ]))

    testCase "WHY - every further save that lands adds to the count and to the list, each declaration named once, in the order they were first saved" <| fun _ ->
      observeAll
        [ deltaPending [ "Handlers.describe" ]
          deltaPatched [ "Handlers.describe" ]
          deltaPending [ "Handlers.describe"; "Handlers.makeHeld" ]
          deltaPatched [ "Handlers.describe"; "Handlers.makeHeld" ]
          deltaPending [ "Handlers.taskBody" ] ]
      |> Expect.equal "three saves, three declarations" (ReplFreshness.BehindApp (3, [ "Handlers.describe"; "Handlers.makeHeld"; "Handlers.taskBody" ]))

    testCase "WHY - nothing else a worker says puts the REPL behind: a restart, a failed compile, a detour patch or a save that changed nothing" <| fun _ ->
      for reload in
        [ finished ReloadCase.Restarted PatchMechanism.NoPatch []
          finished ReloadCase.RestartRequired PatchMechanism.NoPatch []
          finished ReloadCase.CompileFailed PatchMechanism.NoPatch []
          finished ReloadCase.NoEffect PatchMechanism.NoPatch []
          // A detour patches the FSI host's own copy, so the REPL sees it.
          finished ReloadCase.PatchPending PatchMechanism.Detour [ "Handlers.describe" ]
          finished ReloadCase.Patched PatchMechanism.Detour [ "Handlers.describe" ]
          // A delta that was applied and never entered is still applied: that one is a different test.
          SessionReload.NoReloadYet ] do
        observeAll [ reload ] |> Expect.equal (sprintf "%A leaves it in sync" reload) ReplFreshness.InSync

    testCase "WHY - a delta that was applied and whose new body never ran is still applied, so it still puts the REPL behind" <| fun _ ->
      // The save was counted when it was pending. The bound passing does not take it back.
      observeAll [ deltaPending [ "Handlers.describe" ]; finished ReloadCase.NeverEntered PatchMechanism.MetadataDelta [ "Handlers.describe" ] ]
      |> Expect.equal "one save, applied" (ReplFreshness.BehindApp (1, [ "Handlers.describe" ]))

    testCase "WHY - later saves that are not deltas do not bring it level: only a rebuilt or restarted FSI host does" <| fun _ ->
      observeAll
        [ deltaPending [ "Handlers.describe" ]
          finished ReloadCase.Restarted PatchMechanism.NoPatch []
          finished ReloadCase.NoEffect PatchMechanism.NoPatch [] ]
      |> Expect.equal "still behind" (ReplFreshness.BehindApp (1, [ "Handlers.describe" ]))

    testCase "WHY - a replaced worker starts from a fresh build, so it is in sync again" <| fun _ ->
      ReplFreshness.afterWorkerSwap (ReplFreshness.BehindApp (4, [ "A.f" ])) |> Expect.equal "cleared" ReplFreshness.InSync
      ReplFreshness.afterWorkerSwap ReplFreshness.InSync |> Expect.equal "and stays so" ReplFreshness.InSync

    testCase "WHY - the warning says what happened and what to do, so a reader never has to know the history" <| fun _ ->
      let text = ReplFreshness.describe (ReplFreshness.BehindApp (2, [ "Handlers.describe"; "Handlers.makeHeld" ]))
      text |> Expect.stringContains "says the app was patched in place" "patched in place"
      text |> Expect.stringContains "counts the saves" "2 saves"
      text |> Expect.stringContains "names the declarations" "Handlers.describe, Handlers.makeHeld"
      text |> Expect.stringContains "says the REPL and live tests run the build from before" "REPL and live tests"
      text |> Expect.stringContains "says what to do" "hard_reset_fsi_session"
      text |> Expect.stringContains "with the argument that does it" "rebuild=true"
      // The remedy is honest about its price: it replaces the worker, and an app that was patched in place to keep its state dies with it.
      text |> Expect.stringContains "says the running app stops with it" "running app stops"
      text |> Expect.stringContains "and that its state goes" "state is lost"
      text |> Expect.stringContains "and how to start it again" "run_app"
      ReplFreshness.describe (ReplFreshness.BehindApp (1, [ "A.f" ])) |> Expect.stringContains "one save is spelled as one" "1 save "

    testCase "WHY - a long list of declarations is cut with a count, never dropped silently, so the warning stays readable" <| fun _ ->
      let many = [ for i in 1 .. 40 -> sprintf "Handlers.f%d" i ]
      let text = ReplFreshness.describe (ReplFreshness.BehindApp (40, many))
      text |> Expect.stringContains "the first is there" "Handlers.f1"
      text |> Expect.stringContains "the cut says how many are left" "more"
      (text.Length < 900) |> Expect.isTrue (sprintf "readable: %d chars" text.Length)

    testCase "WHY - in sync says nothing in a banner, so a tool result is not cluttered when there is nothing to know" <| fun _ ->
      ReplFreshness.banner ReplFreshness.InSync |> Expect.equal "no banner" ""
      ReplFreshness.annotate ReplFreshness.InSync "Result: 1" |> Expect.equal "the result is untouched" "Result: 1"

    testCase "WHY - behind puts the warning on the result an agent reads, after the result and not instead of it" <| fun _ ->
      let annotated = ReplFreshness.annotate (ReplFreshness.BehindApp (1, [ "A.f" ])) "Result: 1"
      annotated |> Expect.stringStarts "the result is first" "Result: 1"
      annotated |> Expect.stringContains "the warning follows" "hard_reset_fsi_session"
      annotated |> Expect.stringContains "and is loud" "BEHIND"

    testCase "WHY - the wire says the state either way, so a client reads a field and never infers it from the absence of one" <| fun _ ->
      let wire (f: ReplFreshness) = JsonSerializer.Serialize(ReplFreshness.toWire f)
      use inSync = JsonDocument.Parse(wire ReplFreshness.InSync)
      inSync.RootElement.GetProperty("state").GetString() |> Expect.equal "in sync" "InSync"
      use behind = JsonDocument.Parse(wire (ReplFreshness.BehindApp (2, [ "A.f"; "B.g" ])))
      let root = behind.RootElement
      root.GetProperty("state").GetString() |> Expect.equal "behind" "BehindApp"
      root.GetProperty("savesSince").GetInt32() |> Expect.equal "the count" 2
      [ for d in root.GetProperty("declarations").EnumerateArray() -> d.GetString() ] |> Expect.equal "the declarations" [ "A.f"; "B.g" ]
      root.GetProperty("message").GetString() |> Expect.stringContains "and what to do about it" "hard_reset_fsi_session"

    testCase "WHY - a freshness nobody could read is never worded as level: it warns, says it could not be read, and says why" <| fun _ ->
      for why, reasonWords in
        [ FreshnessUnknown.SessionGone, "no longer registered"
          FreshnessUnknown.LookupFailed "registry timed out", "registry timed out"
          FreshnessUnknown.NotRead, "without looking" ] do
        let unknown = ReplFreshness.Unknown why
        let banner = ReplFreshness.banner unknown
        banner |> Expect.stringStarts "it is a warning" "WARNING"
        banner |> Expect.stringContains "it says it could not be read" "could not be read"
        banner |> Expect.stringContains "and why" reasonWords
        ReplFreshness.describe unknown
        |> Expect.notEqual "never the in-sync sentence" (ReplFreshness.describe ReplFreshness.InSync)
        ReplFreshness.annotate unknown "Result: 1"
        |> Expect.stringStarts "the result stays first and whole" "Result: 1"

    testCase "WHY - the wire says Unknown with a kind and a detail, so a client branches on a field and never reads silence as level" <| fun _ ->
      for why, kind in
        [ FreshnessUnknown.SessionGone, "SessionGone"
          FreshnessUnknown.LookupFailed "boom", "LookupFailed"
          FreshnessUnknown.NotRead, "NotRead" ] do
        use doc = JsonDocument.Parse(JsonSerializer.Serialize(ReplFreshness.toWire (ReplFreshness.Unknown why)))
        let root = doc.RootElement
        root.GetProperty("state").GetString() |> Expect.equal "the state" "Unknown"
        root.GetProperty("reason").GetProperty("kind").GetString() |> Expect.equal "the kind" kind
        root.GetProperty("reason").GetProperty("detail").GetString() |> Expect.isNotEmpty "the detail"
        root.GetProperty("message").GetString() |> Expect.stringContains "and the sentence" "could not be read"

    testCase "WHY - a failed lookup carries its reason onto the wire, so the reader can tell a dead registry from a gone session" <| fun _ ->
      use doc = JsonDocument.Parse(JsonSerializer.Serialize(ReplFreshness.toWire (ReplFreshness.Unknown (FreshnessUnknown.LookupFailed "registry timed out"))))
      doc.RootElement.GetProperty("reason").GetProperty("detail").GetString() |> Expect.stringContains "the exception's words" "registry timed out"

    testCase "WHY - a save the worker reports settles an Unknown state: the build is known to be behind, whatever could not be read before" <| fun _ ->
      ReplFreshness.observe (ReplFreshness.Unknown FreshnessUnknown.NotRead) (deltaPatched [ "A.f" ])
      |> Expect.equal "behind" (ReplFreshness.BehindApp (1, [ "A.f" ]))
      ReplFreshness.observe (ReplFreshness.Unknown FreshnessUnknown.NotRead) (finished ReloadCase.Restarted PatchMechanism.NoPatch [])
      |> Expect.equal "a report that changes nothing leaves it Unknown" (ReplFreshness.Unknown FreshnessUnknown.NotRead)

    testCase "WHY - the declarations a worker names travel on its reload payload" <| fun _ ->
      let payload = """{"type":"pending","outcome":"PatchPending","mechanism":"metadata-delta","declarations":["Handlers.describe","Handlers.makeHeld"]}"""
      match SessionReload.ofPayloadJson payload with
      | Result.Ok (SessionReload.Finished parsed) -> parsed.Declarations |> Expect.equal "both" [ "Handlers.describe"; "Handlers.makeHeld" ]
      | other -> failtestf "should parse: %A" other
      match SessionReload.ofPayloadJson """{"type":"patched","outcome":"Patched"}""" with
      | Result.Ok (SessionReload.Finished parsed) -> parsed.Declarations |> Expect.isEmpty "a payload with none has none"
      | other -> failtestf "should parse: %A" other

    testProperty "WHY - however saves and other reports interleave, the count is the number of delta saves that landed and the list has no repeats" <| fun (seed: int) ->
      let rnd = System.Random seed
      let names = [| "A.f"; "B.g"; "C.h"; "D.i" |]
      let reports =
        [ for _ in 1 .. rnd.Next(0, 30) ->
            match rnd.Next(0, 6) with
            | 0 | 1 -> Choice1Of2 [ names[rnd.Next names.Length] ]            // a delta save lands: pending, then confirmed
            | 2 -> Choice2Of2 (finished ReloadCase.Restarted PatchMechanism.NoPatch [])
            | 3 -> Choice2Of2 (finished ReloadCase.NoEffect PatchMechanism.NoPatch [])
            | 4 -> Choice2Of2 (finished ReloadCase.Patched PatchMechanism.Detour [ "X.y" ])
            | _ -> Choice2Of2 SessionReload.NoReloadYet ]
      let stream =
        reports
        |> List.collect (function
          | Choice1Of2 decls -> [ deltaPending decls; deltaPatched decls ]
          | Choice2Of2 other -> [ other ])
      let landed = reports |> List.choose (function Choice1Of2 d -> Some d | Choice2Of2 _ -> None)
      match observeAll stream, landed with
      | ReplFreshness.InSync, [] -> true
      | ReplFreshness.BehindApp (count, declarations), _ :: _ ->
        count = landed.Length && declarations = List.distinct (List.concat landed)
      | _ -> false
  ]

// -- leveling: the state a session is in while it brings the host up, and when it cannot -----------------------------------------

let private servable = LevelStanding.Servable 4242
let private noLevel = LevelFlight.NoLevelInFlight
let private wireOf (f: ReplFreshness) : JsonElement = JsonDocument.Parse(JsonSerializer.Serialize(ReplFreshness.toWire f)).RootElement

[<Tests>]
let levelingTests =
  testList "ReplFreshness leveling" [

    testCase "WHY - a session that has just gone behind and can be leveled starts, and says so while it does: Leveling, not InSync" <| fun _ ->
      ReplFreshness.beginLeveling (ReplFreshness.BehindApp (1, [ "A.f" ])) servable noLevel
      |> Expect.equal "asks the worker it asked about" (ReplFreshness.Leveling (1, [ "A.f" ]), LevelStart.StartLevel 4242)

    testCase "WHY - a level already in flight is not asked for twice, and the state stays what it was" <| fun _ ->
      ReplFreshness.beginLeveling (ReplFreshness.BehindApp (2, [ "A.f" ])) servable LevelFlight.LevelInFlight
      |> Expect.equal "no second ask" (ReplFreshness.BehindApp (2, [ "A.f" ]), LevelStart.NoStart)

    testCase "WHY - a session that cannot be leveled right now says which of the things stopped it, and does not ask" <| fun _ ->
      for blocker in [ LevelBlocker.GateDriven; LevelBlocker.SwapInProgress; LevelBlocker.NotServing ] do
        ReplFreshness.beginLeveling (ReplFreshness.BehindApp (1, [ "A.f" ])) (LevelStanding.Unservable blocker) noLevel
        |> Expect.equal (sprintf "%A is named" blocker) (ReplFreshness.CannotLevel (1, [ "A.f" ], [ blocker ]), LevelStart.NoStart)

    testCase "WHY - only a session that is behind starts a level: in sync, unknown, leveling and blocked states are left alone" <| fun _ ->
      for state in
        [ ReplFreshness.InSync
          ReplFreshness.Unknown FreshnessUnknown.NotRead
          ReplFreshness.Leveling (1, [ "A.f" ])
          ReplFreshness.CannotLevel (1, [ "A.f" ], [ LevelBlocker.GateDriven ]) ] do
        ReplFreshness.beginLeveling state servable noLevel |> Expect.equal (sprintf "%A is left" state) (state, LevelStart.NoStart)

    testCase "WHY - a worker that answers Leveled and whose refetch is done brings the session level, and only a Leveling one" <| fun _ ->
      ReplFreshness.afterLevelAnswer (ReplFreshness.Leveling (2, [ "A.f" ])) LevelAnswer.Leveled |> Expect.equal "level" ReplFreshness.InSync
      ReplFreshness.afterLevelAnswer ReplFreshness.InSync LevelAnswer.Leveled |> Expect.equal "still level" ReplFreshness.InSync

    testCase "WHY - a save that lands while the level runs puts the session behind again, because the host was built before that save" <| fun _ ->
      let reopened = ReplFreshness.observe (ReplFreshness.Leveling (1, [ "A.f" ])) (deltaPending [ "B.g" ])
      reopened |> Expect.equal "behind by two saves" (ReplFreshness.BehindApp (2, [ "A.f"; "B.g" ]))
      ReplFreshness.afterLevelAnswer reopened LevelAnswer.Leveled
      |> Expect.equal "the answer was about the older build, so the gap stays open" (ReplFreshness.BehindApp (2, [ "A.f"; "B.g" ]))

    testCase "WHY - the confirmation of a save the level already covers does not reopen the gap" <| fun _ ->
      ReplFreshness.observe (ReplFreshness.Leveling (1, [ "A.f" ])) (deltaPatched [ "A.f" ])
      |> Expect.equal "still leveling, same save" (ReplFreshness.Leveling (1, [ "A.f" ]))

    testCase "WHY - a worker that defers names the blockers, and the session keeps them with the saves it is behind on" <| fun _ ->
      let blockers = [ LevelBlocker.ReplHoldsDefinitions 3; LevelBlocker.InitScriptDefined "init.fsx" ]
      ReplFreshness.afterLevelAnswer (ReplFreshness.Leveling (1, [ "A.f" ])) (LevelAnswer.LevelDeferred blockers)
      |> Expect.equal "cannot level" (ReplFreshness.CannotLevel (1, [ "A.f" ], blockers))
      ReplFreshness.afterLevelAnswer (ReplFreshness.BehindApp (2, [ "A.f"; "B.g" ])) (LevelAnswer.LevelDeferred blockers)
      |> Expect.equal "a reopened gap is blocked at its newer count" (ReplFreshness.CannotLevel (2, [ "A.f"; "B.g" ], blockers))

    testCase "WHY - a level that failed makes the state Unknown with the reason, never in sync" <| fun _ ->
      ReplFreshness.afterLevelAnswer (ReplFreshness.Leveling (1, [ "A.f" ])) (LevelAnswer.LevelFailed "the host would not start")
      |> Expect.equal "unknown" (ReplFreshness.Unknown (FreshnessUnknown.LevelFailed "the host would not start"))
      FreshnessUnknown.detail (FreshnessUnknown.LevelFailed "the host would not start") |> Expect.stringContains "says why" "the host would not start"
      FreshnessUnknown.kind (FreshnessUnknown.LevelFailed "x") |> Expect.equal "its own kind" "LevelFailed"

    testCase "WHY - a save after a blocked level re-opens it as an ordinary gap, so the next save asks again" <| fun _ ->
      ReplFreshness.observe (ReplFreshness.CannotLevel (1, [ "A.f" ], [ LevelBlocker.GateDriven ])) (deltaPending [ "B.g" ])
      |> Expect.equal "behind by two" (ReplFreshness.BehindApp (2, [ "A.f"; "B.g" ]))

    testCase "WHY - a hard reset of the host that resolved Ok brings a session that was behind to Leveling until the refetch finishes, and leaves a level session alone" <| fun _ ->
      ReplFreshness.afterHostRebuilt (ReplFreshness.BehindApp (4, [ "A.f" ])) |> Expect.equal "behind" (ReplFreshness.Leveling (4, [ "A.f" ]))
      ReplFreshness.afterHostRebuilt (ReplFreshness.CannotLevel (1, [ "A.f" ], [ LevelBlocker.GateDriven ])) |> Expect.equal "blocked" (ReplFreshness.Leveling (1, [ "A.f" ]))
      ReplFreshness.afterHostRebuilt (ReplFreshness.Leveling (1, [ "A.f" ])) |> Expect.equal "leveling" (ReplFreshness.Leveling (1, [ "A.f" ]))
      ReplFreshness.afterHostRebuilt (ReplFreshness.Unknown FreshnessUnknown.NotRead) |> Expect.equal "unknown" (ReplFreshness.Leveling (0, []))
      ReplFreshness.afterHostRebuilt ReplFreshness.InSync |> Expect.equal "in sync" ReplFreshness.InSync

    testCase "WHY - while leveling the state is loud and says it is being brought level, with the time it takes and that the app is left alone" <| fun _ ->
      let text = ReplFreshness.describe (ReplFreshness.Leveling (2, [ "A.f" ]))
      text |> Expect.stringContains "still behind" "BEHIND"
      text |> Expect.stringContains "says it is being brought level" "bringing the REPL and live tests level"
      text |> Expect.stringContains "says how long, and that it grows with the project" "tens of seconds on a large one"
      text |> Expect.stringContains "says the app is not touched" "leaves the app"
      ReplFreshness.banner (ReplFreshness.Leveling (1, [])) |> Expect.stringStarts "a warning" "WARNING"

    testCase "WHY - a session that cannot level says which things stopped it, in words, and still says how to level by hand" <| fun _ ->
      let text = ReplFreshness.describe (ReplFreshness.CannotLevel (1, [ "A.f" ], [ LevelBlocker.ReplHoldsDefinitions 3; LevelBlocker.TestsRunning 1 ]))
      text |> Expect.stringContains "behind" "BEHIND"
      text |> Expect.stringContains "names the first blocker" "3 evals"
      text |> Expect.stringContains "names the second" "test run is in flight"
      text |> Expect.stringContains "still says the manual way" "hard_reset_fsi_session"

    testCase "WHY - a REPL that SageFs left alone because a level would take too long says how long, what the bound is, and the one action that does it now" <| fun _ ->
      let slow = FixtureDurations.largeProjectReset
      let text = ReplFreshness.describe (ReplFreshness.CannotLevel (2, [ "A.f" ], [ LevelBlocker.LevelWouldTakeTooLong slow ]))
      text |> Expect.stringContains "still behind" "BEHIND"
      text |> Expect.stringContains "how long the last reset took, so how long a level is expected to" "26 s"
      text |> Expect.stringContains "the bound it is held to" (sprintf "%.0f s" (Timeouts.replLevelCostBudget.TotalSeconds))
      text |> Expect.stringContains "the action that does it now" "hard_reset_fsi_session"
      let tokens = (wireOf (ReplFreshness.CannotLevel (2, [ "A.f" ], [ LevelBlocker.LevelWouldTakeTooLong slow ]))).GetProperty("blockedBy")
      [ for token in tokens.EnumerateArray() -> token.GetString() ] |> Expect.equal "its own wire token" [ "LevelWouldTakeTooLong" ]

    testCase "WHY - on the wire both new states are BehindApp, so no client has to learn a fourth state, and blockedBy lists the tokens" <| fun _ ->
      let leveling = wireOf (ReplFreshness.Leveling (2, [ "A.f" ]))
      leveling.GetProperty("state").GetString() |> Expect.equal "same state name" "BehindApp"
      leveling.GetProperty("savesSince").GetInt32() |> Expect.equal "the count" 2
      leveling.GetProperty("blockedBy").GetArrayLength() |> Expect.equal "nothing blocks it" 0
      leveling.GetProperty("message").GetString() |> Expect.stringContains "its own message" "bringing the REPL and live tests level"
      let blocked = wireOf (ReplFreshness.CannotLevel (1, [ "A.f" ], [ LevelBlocker.InitScriptDefined "init.fsx"; LevelBlocker.GateDriven ]))
      blocked.GetProperty("state").GetString() |> Expect.equal "same state name" "BehindApp"
      [ for b in blocked.GetProperty("blockedBy").EnumerateArray() -> b.GetString() ] |> Expect.equal "the tokens" [ "InitScriptDefined"; "GateDriven" ]
      let plain = wireOf (ReplFreshness.BehindApp (1, [ "A.f" ]))
      plain.GetProperty("blockedBy").GetArrayLength() |> Expect.equal "a plain gap lists none, and the field is always there" 0

    testProperty "WHY - however saves, level asks and answers, and host rebuilds interleave, the state is InSync only when the host was built at or after the latest save" <| fun (seed: int) ->
      let rnd = System.Random seed
      // The model: the app is at save number `saves`, the host was built when the app was at `hostBuiltAt`, and the ask in flight
      // (if any) was sent when the app was at `asked`.
      let mutable freshness = ReplFreshness.InSync
      let mutable saves = 0
      let mutable hostBuiltAt = 0
      let mutable asked = 0
      let mutable ok = true
      for _ in 1 .. 50 do
        match rnd.Next 4 with
        | 0 ->
          saves <- saves + 1
          freshness <- ReplFreshness.observe freshness (deltaPending [ sprintf "D.f%d" saves ])
        | 1 ->
          let next, start = ReplFreshness.beginLeveling freshness servable noLevel
          freshness <- next
          match start with
          | LevelStart.StartLevel _ -> asked <- saves
          | LevelStart.NoStart -> ()
        | 2 ->
          // The worker answers the ask it was sent: its host is as new as the app was when it was asked.
          match freshness with
          | ReplFreshness.Leveling _ ->
            hostBuiltAt <- max hostBuiltAt asked
            freshness <- ReplFreshness.afterLevelAnswer freshness LevelAnswer.Leveled
          | _ -> ()
        | _ ->
          hostBuiltAt <- saves
          freshness <- ReplFreshness.afterHostRebuilt freshness
        match freshness with
        | ReplFreshness.InSync when hostBuiltAt < saves -> ok <- false
        | _ -> ()
      ok
  ]
