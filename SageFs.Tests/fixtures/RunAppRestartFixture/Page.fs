/// The text the route serves. The hot-reload latency tier (HotReloadLatency.measureRestartedSaves)
/// saves this file with a new tag in the string, on its own copy, to time a save to an app `run_app`
/// runs: SageFs rebuilds and relaunches such an app, so the save ends with the new text served by a new process.
module RunAppRestartFixture.Page

let body () : string = "served from a run_app app"
