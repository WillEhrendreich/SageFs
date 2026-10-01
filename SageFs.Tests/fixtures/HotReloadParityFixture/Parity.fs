/// The hot-reload PARITY fixture: the edits .NET Hot Reload handles for C# that
/// the shape matrix never asked about.
///
/// Every route below serves a string that ends in `:A`. A test saves an edit
/// that turns one of them into `:B` and reads the SAME running process again.
/// The routes are captured once, at startup, the way a Falco, Giraffe or
/// Saturn route table is, so a save has to reach code the app already holds.
///
/// The test copies this file into a scratch project per run and per runtime,
/// so the edits a test makes never touch this checked-in copy.
module ParityFixture.Parity

open System.Threading.Tasks

// ── instance members ────────────────────────────────────────────────────────

/// An instance member that reads a constructor argument, built once at startup.
type Greeter(prefix: string) =
  member _.Hello() : string = prefix + "instance:A"

/// An instance member that keeps state in a field. The state has to survive.
type Ticker() =
  let mutable calls = 0
  member _.Next() : string =
    calls <- calls + 1
    "instanceState:A#" + string calls

/// An instance member whose edit starts reading a constructor argument, which
/// makes the compiler add a field. Live instances were laid out without it.
type Tagger(tag: string) =
  member _.Tag() : string = "instanceNewField:A"

let greeter = Greeter("")
let ticker = Ticker()
let tagger = Tagger("t")

// ── async and task, named ───────────────────────────────────────────────────

let taskNamed () : Task<string> =
  task {
    do! Task.Yield()
    return "taskNamed:A"
  }

let asyncNamed () : Task<string> =
  async {
    do! Async.Sleep 1
    return "asyncNamed:A"
  }
  |> Async.StartAsTask

// ── a closure a function hands back, held by the table ──────────────────────

/// The closure captures `tag`, which is computed from the argument, so it is a real
/// closure object and not a method the compiler folded the lambda into.
let makeHeld (suffix: string) : unit -> Task<string> =
  let tag = suffix + "?"
  fun () -> Task.FromResult("heldClosure:A" + tag)

let held = makeHeld "!"

// ── added, removed and re-signed functions ──────────────────────────────────

let addedFunctionCaller () : string = "addedFunction:A"

let addedTypeCaller () : string = "addedType:A"

let addedValueCaller () : string = "addedValue:A"

let removedHelper () : string = "removed:A"

let removedCaller () : string = removedHelper ()

let sigHandler (who: string) : string = "signature:A" + who

let sigCaller () : string = sigHandler "!"

// ── generics ────────────────────────────────────────────────────────────────

let genericTag<'T> (x: 'T) : string = "generic:A" + string x

let genericCaller () : string = genericTag "s" + "|" + genericTag 7

// ── the table, captured BY VALUE at startup, like a Falco route list ────────

let routes : (string * (unit -> Task<string>)) list =
  [ // Inline lambdas in the list itself: nothing here has a name to re-point.
    "inlineLambda", (fun () -> Task.FromResult "inlineLambda:A")
    "inlineCapture", (let tag = System.String('x', 1) in fun () -> Task.FromResult("inlineCapture:A" + tag))
    "inlineNewCapture", (let extra = System.String('y', 1) in fun () -> Task.FromResult "inlineNewCapture:A")
    "taskLambda", (fun () -> task {
      do! Task.Yield()
      return "taskLambda:A"
    })
    "asyncLambda", (fun () ->
      async {
        do! Async.Sleep 1
        return "asyncLambda:A"
      }
      |> Async.StartAsTask)
    "heldClosure", held
    // Named handlers.
    "taskNamed", (fun () -> taskNamed ())
    "asyncNamed", (fun () -> asyncNamed ())
    // Instance members.
    "instance", (fun () -> Task.FromResult(greeter.Hello()))
    "instanceState", (fun () -> Task.FromResult(ticker.Next()))
    "instanceNewField", (fun () -> Task.FromResult(tagger.Tag()))
    // Functions the saves add, remove and re-sign.
    "addedFunction", (fun () -> Task.FromResult(addedFunctionCaller ()))
    "addedType", (fun () -> Task.FromResult(addedTypeCaller ()))
    "addedValue", (fun () -> Task.FromResult(addedValueCaller ()))
    "removed", (fun () -> Task.FromResult(removedCaller ()))
    "signature", (fun () -> Task.FromResult(sigCaller ()))
    "generic", (fun () -> Task.FromResult(genericCaller ())) ]
