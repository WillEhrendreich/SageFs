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

[<Tests>]
let hotReloadStateOutcomeTests =
  Integration.hostList "hot reload keeps live state across a save" [
    for runtime in HostRuntime.all do
      publicStateSurvives runtime
      privateStateSurvives runtime
      editedInitializerIsKept runtime
      resetRunsTheNewInitializer runtime
      retypedStateRestarts runtime
  ]
