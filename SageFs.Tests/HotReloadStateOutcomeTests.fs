/// Hot reload phase 2: does the app's live state survive a save?
///
/// Outcome tests only. Each one pushes real state into a real running app over
/// HTTP, saves a real edit, and reads the state back out of the SAME process,
/// on both runtimes the host ships for. The rules come from
/// hot-reload-state-spec.md: code changes take effect, state stays, nothing
/// happens silently.
module SageFs.Tests.HotReloadStateOutcomeTests

open Expecto
open Expecto.Flip
open SageFs.Tests.HotReloadStateHarness

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private withApp (runtime: HostRuntime) (body: RunningApp -> System.Threading.Tasks.Task<unit>) = task {
  let! app = start runtime
  try
    do! body app
  finally
    stop app
}

let private bumpTimes (app: RunningApp) (route: string) (n: int) = task {
  let mutable last = ""
  for _ in 1 .. n do
    let! served = get app route
    last <- served
  return last
}

/// Rule 1, public state: a save that edits a DIFFERENT function in the same
/// file must not re-run `count`'s initializer.
let private publicStateSurvives (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 1: a public let mutable keeps its live value when another function in its file is edited" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      let! bumped = bumpTimes app "bump" 3
      bumped |> Expect.equal "three bumps over HTTP should leave count at 3" "3"
      let! label = get app "label"
      label |> Expect.equal "the label serves its original body" "A"

      let! verdict = save app "let label () : string = \"A\"" "let label () : string = \"B\""

      let! relabelled = settle app "label" "B"
      relabelled
      |> Expect.equal (sprintf "the edited function should serve its new body.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "B"
      verdict |> Expect.stringContains "the save should report a real patch" "\"type\":\"reload\""
      let! count = get app "count"
      count
      |> Expect.equal (sprintf "count is live data the save never touched, so it has to still be 3.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "3"
      let! next = get app "bump"
      next |> Expect.equal "the next bump still lands in the same storage" "4"
    })
  }

/// Rule 1, private state: the edited function reads `hidden`, which is
/// private, so the patch can't name it from FSI. It still has to land, and it
/// has to read and write the app's OWN `hidden`, not a fresh copy.
let private privateStateSurvives (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 1: a patch that reads a let mutable private lands and keeps using the app's live storage" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      let! bumped = bumpTimes app "bumpHidden" 3
      bumped |> Expect.equal "three bumps over HTTP should leave hidden at 3" "3"
      let! before = get app "hiddenLabel"
      before |> Expect.equal "the label reads the live private value" "A3"

      let! verdict = save app "let hiddenLabel () : string = \"A\" + string hidden" "let hiddenLabel () : string = \"B\" + string hidden"

      let! relabelled = settle app "hiddenLabel" "B3"
      relabelled
      |> Expect.equal (sprintf "the patched function should serve its new body AND the live private value.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "B3"
      verdict |> Expect.stringContains "the save should report a real patch" "\"type\":\"reload\""
      let! next = get app "bumpHidden"
      next |> Expect.equal "the app's own bump still lands in the same storage" "4"
      let! after = get app "hiddenLabel"
      after |> Expect.equal "and the patched reader sees that write, so reads and writes agree" "B4"
    })
  }

let private json (payload: string) = System.Text.Json.JsonDocument.Parse(payload).RootElement

let private str (e: System.Text.Json.JsonElement) (name: string) =
  match e.TryGetProperty name with
  | true, v -> v.ToString()
  | false, _ -> failtestf "no '%s' in %s" name (e.GetRawText())

/// The kept-state entries a payload reports, as (binding, keptValue, newInitializer).
let private keptIn (e: System.Text.Json.JsonElement) =
  match e.TryGetProperty "kept" with
  | true, kept -> [ for k in kept.EnumerateArray() -> str k "binding", str k "keptValue", str k "newInitializer" ]
  | false, _ -> []

let private tunedBinding = "StateFixture.State.tuned"

/// Save the edited initializer for `tuned` after pushing it to 13, and check
/// the app kept 13 and said so. Shared by the keep and reset tests.
let private keepTuned (app: RunningApp) = task {
  let! bumped = bumpTimes app "bumpTuned" 3
  bumped |> Expect.equal "three bumps from 10 leave tuned at 13" "13"
  let! verdict = save app "let mutable tuned = 10" "let mutable tuned = 25"
  let v = json verdict
  str v "outcome"
  |> Expect.equal (sprintf "an edited initializer on live state is kept, not restarted.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "KeptLiveState"
  keptIn v
  |> Expect.equal "the notice names the binding, the value it kept and the initializer waiting for a reset" [ tunedBinding, "13", "25" ]
  str v "type" |> Expect.equal "nothing the page shows changed, so the page isn't refreshed" "noeffect"
  let! served = get app "tuned"
  served |> Expect.equal "the app still has its live value" "13"
  let! next = get app "bumpTuned"
  next |> Expect.equal "and keeps counting from it" "14"
}

/// Rule 3: editing a live mutable's initializer keeps the live value and says so.
let private editedInitializerIsKept (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 3: editing a live let mutable's initializer keeps its value and reports what it kept" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      do! keepTuned app
      let! listed = get app "tuned"
      listed |> Expect.equal "still live" "14"
      let! hotReload = HotReloadStateHarness.getWorker app "/hotreload"
      keptIn (json hotReload)
      |> Expect.equal "the worker's hot-reload state lists the pending initializer (with the value it kept at the save), so the dashboard can show it" [ tunedBinding, "13", "25" ]
    })
  }

/// Rule 3's reset: re-run ONLY that initializer.
let private resetRunsTheNewInitializer (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 3: resetting kept state re-runs only that binding's new initializer" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      do! keepTuned app
      let! _ = bumpTimes app "bump" 2
      let! status, body = post app "/hotreload/reset-state" (sprintf """{"binding":"%s"}""" tunedBinding)
      status |> Expect.equal (sprintf "the reset should succeed: %s\nHost log:\n%s" body (RunningApp.log app)) 200
      let! served = get app "tuned"
      served |> Expect.equal "tuned now holds the new initializer's value" "25"
      let! next = get app "bumpTuned"
      next |> Expect.equal "and counts on from there" "26"
      let! count = get app "count"
      count |> Expect.equal "the reset touched nothing else" "2"
      let! hotReload = HotReloadStateHarness.getWorker app "/hotreload"
      keptIn (json hotReload) |> Expect.isEmpty "nothing is pending once it's been reset"
    })
  }

/// Rule 4: the initializer's TYPE changed, so the live value can't carry over.
let private retypedStateRestarts (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 4: changing a live let mutable's type needs a restart, and the app is left alone" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      let! verdict = save app "let mutable shape = 1" "let mutable shape = \"one\""
      let v = json verdict
      str v "outcome"
      |> Expect.equal (sprintf "a retyped mutable can't keep its value.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "RestartRequired"
      [ for r in v.GetProperty("reasons").EnumerateArray() -> str r "case" ]
      |> Expect.equal "the reason says the type changed, not just that it's mutable" [ "MutableStateTypeChanged" ]
      let! served = get app "shape"
      served |> Expect.equal "the running app is exactly as it was" "1"
    })
  }

/// Rule 2: an immutable value nobody captured gets its new value, honestly
/// reported as a patch, and nothing else in the file is disturbed.
///
/// Patching `greeting`'s getter is the easy part. The hard part is knowing
/// nobody kept a copy of the old value, because a wrong answer is a fake
/// Patched. On this fixture (Optimize=false) the file's static initializer
/// reads `greeting` into a local it never uses, exactly like it reads
/// `banner`, except that `banner` goes on into a closure. `greet` returns it,
/// but `greet` hasn't run yet when this test saves. So the running app has to
/// say who read it and what they did with it (see ValueReads.fs and
/// ValueReadTracking.fs), and that's what makes the Patched honest.
let private uncapturedValueIsPatched (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 2: a redefined immutable value nobody captured serves its new value, reported as a patch" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      let! _ = bumpTimes app "bump" 2
      let! verdict = save app "let greeting = \"hello\"" "let greeting = \"howdy\""
      let! served = settle app "greet" "howdy"
      served
      |> Expect.equal (sprintf "the running app should serve the new value.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "howdy"
      str (json verdict) "outcome" |> Expect.equal "and the wire has to say it was patched" "Patched"
      let! count = get app "count"
      count |> Expect.equal "and redefining a value must not reset the file's live state" "2"
    })
  }

/// The first restart reason in a verdict, as (case, message).
let private firstReason (verdict: string) =
  match [ for r in (json verdict).GetProperty("reasons").EnumerateArray() -> str r "case", str r "message" ] with
  | first :: _ -> first
  | [] -> failtestf "the verdict gives no reason for the restart: %s" verdict

/// Rule 2's guard: a value startup DID capture is never reported as patched,
/// and the reason says who kept it.
let private capturedValueIsNeverPatched (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 2: a redefined value startup captured is never reported as patched, and the reason names where it went" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      let! verdict = save app "let banner = \"A\"" "let banner = \"B\""
      let! served = get app "banner"
      served |> Expect.equal "the route copied banner at startup, so it still serves the old value" "A"
      let outcome = str (json verdict) "outcome"
      outcome
      |> Expect.notEqual (sprintf "reporting a patch here would be a lie.\nVerdict: %s" verdict) "Patched"
      str (json verdict) "type"
      |> Expect.notEqual "and the page must not be told to refresh into the same bytes" "reload"
      let case, message = firstReason verdict
      case |> Expect.equal (sprintf "the reason is that the app kept a copy.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "ValueCopiedByApp"
      message
      |> Expect.stringContains "and it names the closure the startup code built around the value" "handlers@"
    })
  }

/// Rule 2's trap: `motto` is read inside a `lazy`, and the lazy is forced by
/// the first request, long after startup. The Lazy caches what it read, so a
/// patch of `motto`'s getter can't reach it. A rule that only looked at
/// startup would call this a patch.
let private lazyForcedAfterStartupIsNeverPatched (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 2: a value a lazy read after startup is never reported as patched, and the reason names the lazy" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      let! forced = get app "motto"
      forced |> Expect.equal "the first request forces the lazy" "CARPE DIEM"
      let! verdict = save app "let motto = \"carpe diem\"" "let motto = \"seize the day\""
      str (json verdict) "outcome"
      |> Expect.notEqual (sprintf "the lazy holds the old value, so a patch would be a lie.\nVerdict: %s" verdict) "Patched"
      let case, message = firstReason verdict
      case |> Expect.equal (sprintf "the reason is that the app kept a copy.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "ValueCopiedByApp"
      message |> Expect.stringContains "and it names the lazy's thunk" "lazyMotto@"
      let! served = get app "motto"
      served |> Expect.equal "the app still serves what the lazy cached" "CARPE DIEM"
    })
  }

/// The same lazy, not forced before the save. Nothing has read `motto` and
/// kept it, so the patch is honest: the first request forces the lazy, which
/// reads through the patched getter.
let private lazyNotYetForcedGetsTheNewValue (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 2: a value only an unforced lazy reads is patched, and the lazy picks up the new value" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      let! verdict = save app "let motto = \"carpe diem\"" "let motto = \"seize the day\""
      str (json verdict) "outcome"
      |> Expect.equal (sprintf "nothing kept motto yet, so this is a patch.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "Patched"
      let! served = settle app "motto" "SEIZE THE DAY"
      served |> Expect.equal "the lazy reads the new value when it's first forced" "SEIZE THE DAY"
    })
  }

// ── rule 2 through reflection ────────────────────────────────────────────────
//
// `reflected` is only ever read by PropertyInfo.GetValue, in a hot loop, after
// the app started. Nothing in anyone's IL reads it, so only the reflection
// watch can see those reads. What a save does next depends on the mode.

let private reflectedBinding = "StateFixture.State.reflected"

let private switchMode (app: RunningApp) (mode: SageFs.Middleware.ValueReads.ReflectionReadMode) = task {
  let! status, body = post app "/hotreload/reflection-mode" (sprintf """{"mode":"%s"}""" (SageFs.Middleware.ValueReads.ReflectionReadMode.name mode))
  status |> Expect.equal (sprintf "switching to %A should succeed: %s\nHost log:\n%s" mode body (RunningApp.log app)) 200
}

/// The reflection report the worker serves, parsed.
let private reflectionReport (app: RunningApp) = task {
  let! hotReload = HotReloadStateHarness.getWorker app "/hotreload"
  match (json hotReload).TryGetProperty "reflectionReads" with
  | true, el ->
    match SageFs.Features.KeptState.ReflectionReadsJson.parse el with
    | Result.Ok report -> return report
    | Result.Error why -> return failtestf "the worker's reflection report didn't parse (%s): %s" (SageFs.Features.KeptState.ReflectionReadsError.describe why) hotReload
  | false, _ -> return failtestf "the worker didn't report reflection reads: %s" hotReload
}

/// probe-callers (the default): the hot loop throws each read away, so the
/// caller holds nothing and the value is patched. The loop also crosses the
/// hot-loop threshold, and the app asks about it exactly once.
let private probeCallersPatchesAThrownAwayReflectiveRead (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 2, reflection, probe-callers: a hot loop that throws reflective reads away is patched, and asks once" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      let! first = reflectionReport app
      first.Mode |> Expect.equal "probe-callers by default" SageFs.Middleware.ValueReads.ReflectionReadMode.ProbeCallers
      first.Watch |> Expect.equal (sprintf "the entry watch is on (tiering is off in the host).\nHost log:\n%s" (RunningApp.log app)) SageFs.Middleware.ValueReads.ReflectionWatchStatus.Watching
      let! _ = get app "reflectDrop"
      let! _ = get app "reflectDrop"
      let! report = reflectionReport app
      match report.Notices with
      | [ { Value = value; State = SageFs.Middleware.ValueReads.NoticeState.Asked notice } ] ->
        value |> Expect.equal "the notice names the value" reflectedBinding
        notice.Caller |> Expect.stringContains "and the caller" "reflectDrop"
        (notice.ReadsPerSecond, 1000) |> Expect.isGreaterThanOrEqual "and a hot rate"
      | other -> failtestf "one hot loop, asked about exactly once, got %A" other
      (report.SiteHits, 0L) |> Expect.isGreaterThan "the second call's reads were named by the rewired caller, not walked"
      let! verdict = save app "let reflected = \"mirror\"" "let reflected = \"glass\""
      str (json verdict) "outcome"
      |> Expect.equal (sprintf "every reflective read was thrown away, so this is a patch.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "Patched"
      let! served = settle app "reflectPeek" "glass"
      served |> Expect.equal "and reflection reads the new value" "glass"
    })
  }

/// mark-on-reflect: the same loop marks the value, so its edit restarts. Same
/// app, same reads: switching the mode is what changes the outcome.
let private markOnReflectRestarts (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 2, reflection, mark-on-reflect: the same hot loop marks the value, and its edit restarts" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      do! switchMode app SageFs.Middleware.ValueReads.ReflectionReadMode.MarkOnReflect
      let! _ = get app "reflectDrop"
      let! verdict = save app "let reflected = \"mirror\"" "let reflected = \"glass\""
      str (json verdict) "outcome"
      |> Expect.notEqual (sprintf "a marked value is never patched.\nVerdict: %s" verdict) "Patched"
      let case, message = firstReason verdict
      case |> Expect.equal (sprintf "the app may hold a copy.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "ValueCopiedByApp"
      message |> Expect.stringContains "and the reason says which mode marked it" "mark-on-reflect"
    })
  }

/// exact-every-read: every read walks, and the walk finds the same thrown-away
/// call site the probe does, so this patches too. Switching to it after the
/// app started puts the getter watch back on.
let private exactEveryReadPatches (runtime: HostRuntime) =
  testTask (sprintf "[%s] rule 2, reflection, exact-every-read: switched to after startup, it names the caller and patches a thrown-away read" (HostRuntime.moniker runtime)) {
    do! withApp runtime (fun app -> task {
      do! switchMode app SageFs.Middleware.ValueReads.ReflectionReadMode.ExactEveryRead
      let! _ = get app "reflectDrop"
      let! report = reflectionReport app
      report.Mode |> Expect.equal "switched" SageFs.Middleware.ValueReads.ReflectionReadMode.ExactEveryRead
      (report.Walks, 1000L) |> Expect.isGreaterThanOrEqual "every one of the 2000 reads walked"
      let! verdict = save app "let reflected = \"mirror\"" "let reflected = \"glass\""
      str (json verdict) "outcome"
      |> Expect.equal (sprintf "the walk found the thrown-away site, so this is a patch.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "Patched"
    })
  }

[<Tests>]
let hotReloadStateOutcomeTests =
  Integration.hostList "hot reload keeps live state across a save" [
    for runtime in HostRuntime.all do
      publicStateSurvives runtime
      privateStateSurvives runtime
      editedInitializerIsKept runtime
      resetRunsTheNewInitializer runtime
      retypedStateRestarts runtime
      uncapturedValueIsPatched runtime
      capturedValueIsNeverPatched runtime
      lazyForcedAfterStartupIsNeverPatched runtime
      lazyNotYetForcedGetsTheNewValue runtime
      probeCallersPatchesAThrownAwayReflectiveRead runtime
      markOnReflectRestarts runtime
      exactEveryReadPatches runtime
  ]
