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
    Declarations = declarations }

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
