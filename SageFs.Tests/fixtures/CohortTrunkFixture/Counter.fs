/// The state the trunk app holds in memory. A landing that restarts the app throws it away, so the count going on is
/// the proof that a landing was patched into the running process.
module CohortTrunkFixture.Counter

/// The count lives in the object, not in a module-level mutable, so a landing that edits a handler never touches the file
/// that owns it.
type Hits() =
  let mutable count = 0
  member _.Next() : int =
    count <- count + 1
    count

let hits = Hits()
