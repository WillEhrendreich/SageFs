/// The caller of `State.stamp`, in a file of its own.
///
/// A save that re-signs `stamp` in State.fs is a new method to the running app, and the callers saved WITH it move onto it.
/// This one is not saved with it: it is another file, so it keeps calling the old method until it is saved too. The
/// hot-reload callers case re-signs `stamp`, reads what the app serves (still the old behavior) and what the reload report
/// says about this file, then saves this file and reads both again.
///
/// The test copies this file into a scratch project per run, so its edits never touch this checked-in copy.
module StateFixture.Pages

/// Calls `State.stamp` with the signature it has before the save.
let stamped () : string = State.stamp 7

/// The route table, captured BY VALUE at startup like State's.
let handlers : (string * (unit -> string)) list =
  [ "stamped", stamped ]
