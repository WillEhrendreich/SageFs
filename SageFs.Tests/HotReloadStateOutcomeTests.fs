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

[<Tests>]
let hotReloadStateOutcomeTests =
  Integration.hostList "hot reload keeps live state across a save" [
    for runtime in HostRuntime.all do
      publicStateSurvives runtime
  ]
