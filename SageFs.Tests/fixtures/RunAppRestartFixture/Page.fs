/// The text the route serves. The hot-reload latency tier (HotReloadLatency.measureRunAppSaves)
/// saves this file with a new tag in the string, on its own copy, to time a save to an app `run_app`
/// runs. A string literal is a method body edit, which the metadata-delta route takes: on a daemon with
/// SAGEFS_METADATA_DELTA=off SageFs rebuilds and relaunches the app and the save ends with the new text
/// served by a new process (the restart series), and on the default route the running process takes the
/// delta and serves it (the delta series).
module RunAppRestartFixture.Page

let body () : string = "served from a run_app app"
