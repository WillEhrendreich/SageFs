/// A live counter for the dashboard Reset journey (HotReloadBrowserTests).
///
/// `visits` is the app's live data. The journey bumps it over HTTP, edits its
/// initializer, and expects the save to KEEP the live value and say so in the
/// dashboard's Hot Reload panel (rule 3 of the state spec). Then it clicks
/// Reset and expects the app to serve the new initializer's value.
///
/// Its own file on purpose: the shape matrix edits Shapes.fs and the other
/// journeys edit Greeting.fs, so nothing else ever saves this one.
module WebAppFixture.Counter

let mutable visits = 0

let bump () : string =
  visits <- visits + 1
  string visits

let read () : string = string visits
