/// The run_app metadata-delta fixture: what a handler does, and what an edit to it has to leave alone.
///
/// Every route below serves a string that carries `:A`. A row saves an edit that turns it into `:B` and
/// reads the SAME running process again, so the answer to "did the app restart" is in the process id and
/// in the state that a restart would have thrown away.
///
/// The test copies this file into a scratch project per run and per runtime, so the edits a row makes
/// never touch this checked-in copy.
module RunAppDeltaFixture.Handlers

open System
open System.Threading.Tasks

/// A record, so a generic function is called with a value of a type the project declares.
type Point =
  { X: int
    Y: int }
  override p.ToString() = "(" + string p.X + "," + string p.Y + ")"

// -- (a) a generic function ---------------------------------------------------------------------------------

/// Called with an int, a string and a record before the save. `genericLate` calls it with a type that has not
/// run yet, which is the call a detour cannot reach.
let describe<'T> (x: 'T) : string = "generic:A:" + string x

let generic () : string = String.Join("|", describe 7, describe "s", describe { X = 1; Y = 2 })

let genericLate () : string = describe 1.5

// -- (b) a closure the app keeps -----------------------------------------------------------------------------

/// The closure captures `tag`, which is computed from the argument, so it is a real closure object.
let makeHeld (suffix: string) : unit -> string =
  let tag = suffix + "?"
  fun () -> "closure:A" + tag

let held = makeHeld "!"

let closure () : string = held ()

// -- (c) an instance member with state -----------------------------------------------------------------------

/// The count lives in the object. A restart builds a new object and the count starts again.
type Counter() =
  let mutable calls = 0
  member _.Next() : string =
    calls <- calls + 1
    "instance:A#" + string calls

let counter = Counter()

let instance () : string = counter.Next()

// -- a task body ---------------------------------------------------------------------------------------------

let taskBody () : Task<string> =
  task {
    do! Task.Yield()
    return "taskBody:A"
  }

// -- a method an edit adds -----------------------------------------------------------------------------------

let addedCaller () : string = "addedMethod:A"

// -- what being editable costs the process --------------------------------------------------------------------

[<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
let step (x: int) : int = x + 1

/// How many calls `spin` makes.
let spinCalls = 50000000

/// A call-heavy loop, the work a process that can be edited pays for most (a call into an editable method cannot be
/// bound directly). Returns the CPU milliseconds the process spent on it, the wall milliseconds it took and the count,
/// so nothing can drop the loop. The CPU figure is what a cost comparison reads: on a loaded machine the wall figure
/// carries the time the loop spent waiting for a core, which says nothing about what the loop costs. While the loop
/// runs the rest of the app is idle, so the process's CPU time is the loop's.
let spin () : string =
  let self = System.Diagnostics.Process.GetCurrentProcess()
  let cpuBefore = self.TotalProcessorTime
  let watch = System.Diagnostics.Stopwatch.StartNew()
  let mutable acc = 0
  for _ in 1 .. spinCalls do
    acc <- step acc
  let wall = watch.Elapsed.TotalMilliseconds
  self.Refresh()
  let cpu = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds
  string cpu + ":" + string wall + ":" + string acc

// -- the edits a metadata delta cannot take ------------------------------------------------------------------

/// A virtual member. An edit that changes its signature changes what every override has to be.
[<AbstractClass>]
type Shape() =
  abstract Name: unit -> string

type Square() =
  inherit Shape()
  override _.Name() : string = "rudeVirtual:A"

let shape : Shape = Square()

let rudeVirtual () : string = shape.Name()

/// A struct. An edit that adds a field changes its size, and every value of it already in memory.
[<Struct>]
type Dim = { W: int }

let rudeStruct () : string =
  let d = { W = 3 }
  "rudeStruct:A" + string d.W
