/// The contract for what a save reports about the running app.
///
/// These pin the three rules every mature hot-reload implementation converged
/// on (JVMTI, the Dart VM, Erlang, Vite, React Fast Refresh), because SageFs
/// shipped a violation of the third: a save that re-pointed a few incidental
/// helpers but none of the handlers the user edited still broadcast a browser
/// reload, so the page refreshed into the old code and the tool reported
/// success.
module SageFs.Tests.ReloadOutcomeTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.ReloadOutcome
// The type and its companion module share a name, so the module's functions
// need their own open to be callable unqualified.
open SageFs.Features.ReloadOutcome.ReloadOutcome

let private allReasons =
  [ RestartReason.StartupComputedValue "routes"
    RestartReason.MutableModuleState "counter"
    RestartReason.SignatureChanged "Program.handle"
    RestartReason.TypeShapeChanged "TodoItem"
    RestartReason.NewDeclaration "Program.newThing"
    RestartReason.NotYetSupported "a static member"
    RestartReason.UnverifiedCopy "Program.greeting" ]

let private allOutcomes =
  [ ReloadOutcome.Patched(1, 3)
    ReloadOutcome.NoEffect(3, [ RestartReason.StartupComputedValue "routes" ])
    ReloadOutcome.NoEffect(3, [])
    ReloadOutcome.Restarted [ RestartReason.SignatureChanged "Program.handle" ]
    ReloadOutcome.RestartRequired [ RestartReason.MutableModuleState "counter" ]
    ReloadOutcome.CompileFailed "FS0039: not defined" ]

[<Tests>]
let reloadOutcomeTests =
  testList "ReloadOutcome — what a save reports about the running app" [

    // WHY — this is the bug, in one assertion. A save that patched nothing is
    // not a success with a zero in it; it is a different outcome, and the smart
    // constructor is what makes the lie unrepresentable rather than merely
    // discouraged.
    test "WHY — patching nothing can never be reported as a patch" {
      ofPatchCounts 0 7 []
      |> function
        | ReloadOutcome.NoEffect(considered, _) ->
          considered |> Expect.equal "the count survives, so '0 of 7' stays visible" 7
        | other -> failtestf "0 patched must not be a Patched outcome, got %A" other
    }

    test "WHY — patching something is a patch, and keeps both numbers" {
      ofPatchCounts 2 7 []
      |> Expect.equal "a partial reload must read as partial, not as success" (ReloadOutcome.Patched(2, 7))
    }

    // WHY — the shipped bug was a browser reload broadcast for a save that
    // changed nothing in the process. Whether to refresh must be derived from
    // one place, not re-decided by each surface.
    test "WHY — the browser is only told to refresh when the running code actually changed" {
      ReloadOutcome.Patched(1, 1) |> shouldRefreshBrowser |> Expect.isTrue "patched code is new code"
      ReloadOutcome.Restarted [] |> shouldRefreshBrowser |> Expect.isTrue "a restarted app is current"
      ReloadOutcome.NoEffect(4, []) |> shouldRefreshBrowser |> Expect.isFalse "refreshing into identical code is the bug"
      ReloadOutcome.RestartRequired [] |> shouldRefreshBrowser |> Expect.isFalse "nothing changed yet"
      ReloadOutcome.CompileFailed "x" |> shouldRefreshBrowser |> Expect.isFalse "the old code is still the live code"
    }

    test "WHY — an outcome that changed nothing always says so with its count" {
      ReloadOutcome.NoEffect(12, [])
      |> describe
      |> Expect.stringContains "a no-op must be visible as '0 of N', never as silence" "0 of 12"
    }

    // WHY — "the detour planner found no matching parameter types" is true and
    // useless. The reason must name the shape of the user's own change.
    test "WHY — every restart reason names the user's code, not SageFs's internals" {
      for reason in allReasons do
        let described = describe (ReloadOutcome.RestartRequired [ reason ])
        described |> Expect.isNotEmpty (sprintf "%A must describe itself" reason)
        described.ToLowerInvariant()
        |> fun d ->
          [ "detour"; "harmony"; "planner"; "reloadedmethods" ]
          |> List.iter (fun internalWord ->
            d.Contains internalWord
            |> Expect.isFalse (sprintf "%A must not leak the internal term '%s'" reason internalWord))
    }

    // WHY — Dart prefixes exactly this distinction with "Limitation: " because
    // "we haven't built it" and "this is impossible" call for different user
    // responses.
    test "WHY — a not-yet-supported shape is marked as unimplemented, not impossible" {
      RestartReason.describe (RestartReason.NotYetSupported "a static member")
      |> Expect.stringContains "must distinguish unimplemented from impossible" "Limitation:"
    }

    // WHY — a refusal a user cannot act on is a dead end. Every mature
    // implementation carries the remedy in the message itself.
    test "WHY — every reason carries something the user can actually do" {
      for reason in allReasons do
        RestartReason.remedy reason
        |> Expect.isNotEmpty (sprintf "%A must carry a remedy" reason)
    }

    test "WHY — the startup-capture remedy names the refactor that fixes it" {
      let r = RestartReason.remedy (RestartReason.StartupComputedValue "getHome")
      r |> Expect.stringContains "must show the reloadable shape" "let getHome (ctx"
    }

    // WHY — SageFs restarting the app itself and the user having to do it are
    // different facts. Conflating them either takes credit for work not done or
    // asks for work already done.
    test "WHY — an outcome SageFs already resolved asks the user for nothing" {
      remedy (ReloadOutcome.Restarted [ RestartReason.SignatureChanged "f" ])
      |> Expect.isNone "SageFs restarted it; there is nothing to instruct"
      remedy (ReloadOutcome.Patched(1, 1))
      |> Expect.isNone "the running app is current"
    }

    test "WHY — an outcome the user must resolve always instructs them" {
      for outcome in [ ReloadOutcome.NoEffect(1, allReasons); ReloadOutcome.RestartRequired allReasons
                       ReloadOutcome.CompileFailed "boom" ] do
        remedy outcome |> Expect.isSome (sprintf "%A leaves work for the user" outcome)
    }

    test "WHY — a user-facing message is never a bare status with no next step" {
      for outcome in allOutcomes do
        let msg = describeForUser outcome
        msg |> Expect.isNotEmpty "must say something"
        match remedy outcome with
        | Some _ -> msg |> Expect.stringContains "an actionable outcome shows its action" "→"
        | None -> ()
    }

    testProperty "WHY — describe and remedy are total over every outcome shape" <| fun (patched: int) (considered: int) ->
      // Generated counts, including negative and zero, must never throw.
      let outcome = ofPatchCounts patched considered []
      let described = describe outcome
      let _ = remedy outcome
      let _ = describeForUser outcome
      described.Length > 0

    testProperty "WHY — zero patched is NoEffect for any considered count" <| fun (considered: int) ->
      match ofPatchCounts 0 considered [] with
      | ReloadOutcome.NoEffect _ -> true
      | _ -> false
  ]
