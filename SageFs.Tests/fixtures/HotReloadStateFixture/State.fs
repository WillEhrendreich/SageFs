/// The hot-reload STATE fixture: live values a save has to leave alone.
///
/// The shape matrix (WebAppFixture/Shapes.fs) asks "does the edited code reach
/// the running app". This file asks the other half: when it does, did the
/// app's live data survive? Every binding here is read and written through a
/// route the app captured once at startup, so a test can push real state into
/// the running process over HTTP, save an edit, and read the state back out of
/// the SAME process.
///
/// The test copies this file into a scratch project per run (one per target
/// framework), so the edits a test makes never touch this checked-in copy.
module StateFixture.State

// ── rule 1, public: an unedited binding keeps its live value ────────────────

let mutable count = 0

let bump () : string =
  count <- count + 1
  string count

let readCount () : string = string count

/// The function a test edits while `count` holds live data.
let label () : string = "A"

// ── rule 1, private: same, but a patch can't see it by name ─────────────────

let mutable private hidden = 0

let bumpHidden () : string =
  hidden <- hidden + 1
  string hidden

/// Reads the private state, so editing it is a patch that needs `hidden`.
let hiddenLabel () : string = "A" + string hidden

// ── rule 3: live state whose initializer gets edited ────────────────────────

let mutable tuned = 10

let bumpTuned () : string =
  tuned <- tuned + 1
  string tuned

let readTuned () : string = string tuned

// ── rule 2: an immutable value nothing captured at startup ──────────────────

let greeting = "hello"

/// Reads `greeting` through its getter on every request.
let greet () : string = greeting

// ── the route table, captured BY VALUE at startup like a Falco route list ───

let handlers : (string * (unit -> string)) list =
  [ "bump", bump
    "count", readCount
    "label", label
    "bumpHidden", bumpHidden
    "hiddenLabel", hiddenLabel
    "bumpTuned", bumpTuned
    "tuned", readTuned
    "greet", greet ]
