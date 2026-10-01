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

open System
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

/// A record (a reference type) and a struct (a value type) with a ToString that says what they are, so a row
/// can tell which body ran for which type.
type GenRec =
  { N: int }
  override this.ToString() = "r" + string this.N

[<Struct>]
type GenPoint =
  { PX: int }
  override this.ToString() = "p" + string this.PX

let genericTag<'T> (x: 'T) : string = "generic:A" + string x

let genericCaller () : string = genericTag "s" + "|" + genericTag 7

/// An int (a value type), a string and a record (reference types) through one generic function.
let genericRefTag<'T> (x: 'T) : string = "genericRef:A" + string x

let genericRefCaller () : string = genericRefTag 1 + "|" + genericRefTag "s" + "|" + genericRefTag { N = 2 }

/// The body reads its own type argument, so a body that ran with the WRONG type argument cannot hide.
let genericKindTag<'T> (x: 'T) : string = "genericKind:A" + typeof<'T>.Name

let genericKindCaller () : string =
  genericKindTag "s" + "|" + genericKindTag { N = 2 } + "|" + genericKindTag 1 + "|" + genericKindTag { PX = 3 }

/// The second call of the route is the first to use a float and a struct, so those instantiations are
/// first compiled AFTER the save.
let lateCalls = ref 0

let genericLateTag<'T> (x: 'T) : string = "genericLate:A" + string x

let genericLateCaller () : string =
  lateCalls.Value <- lateCalls.Value + 1
  match lateCalls.Value with
  | 1 -> genericLateTag 7
  | _ -> genericLateTag 7 + "|" + genericLateTag 2.5 + "|" + genericLateTag { PX = 3 }

/// A generic function called from another generic function.
let genericInnerTag<'T> (x: 'T) : string = "genericNested:A" + string x

let genericOuterTag<'T> (x: 'T) : string = genericInnerTag x + "!"

let genericNestedCaller () : string = genericOuterTag "s" + "|" + genericOuterTag 7 + "|" + genericOuterTag { N = 2 }

/// A generic function used as a first-class value: the compiler wraps each use in a closure.
let genericClosureTag<'T> (x: 'T) : string = "genericClosure:A" + string x

let genericClosureString : string -> string = genericClosureTag

let genericClosureInt : int -> string = genericClosureTag

let genericClosureCaller () : string = genericClosureString "s" + "|" + genericClosureInt 7

/// Generic methods and members of types.
type GenTools() =
  member _.Tag<'T>(x: 'T) : string = "genericInstanceMethod:A" + string x
  static member StaticTag<'T>(x: 'T) : string = "genericStaticMethod:A" + string x

let genTools = GenTools()

let genericInstanceMethodCaller () : string = genTools.Tag "s" + "|" + genTools.Tag 7 + "|" + genTools.Tag { N = 2 }

let genericStaticMethodCaller () : string =
  GenTools.StaticTag "s" + "|" + GenTools.StaticTag 7 + "|" + GenTools.StaticTag { N = 2 }

type GenHolder<'T>(v: 'T) =
  member _.Show() : string = "genericTypeInstance:A" + string v
  static member Make(x: 'T) : string = "genericTypeStatic:A" + string x
  member _.Pair<'U>(u: 'U) : string = "genericMethodOnType:A" + string v + string u

let genHolderString = GenHolder("s")

let genHolderRec = GenHolder({ N = 2 })

let genHolderInt = GenHolder(7)

let genericTypeInstanceCaller () : string = genHolderString.Show() + "|" + genHolderRec.Show() + "|" + genHolderInt.Show()

let genericTypeStaticCaller () : string =
  GenHolder<string>.Make "s" + "|" + GenHolder<GenRec>.Make { N = 2 } + "|" + GenHolder<int>.Make 7

let genericMethodOnTypeCaller () : string =
  genHolderString.Pair 1 + "|" + genHolderRec.Pair "u" + "|" + genHolderInt.Pair { N = 3 }

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
    "generic", (fun () -> Task.FromResult(genericCaller ()))
    "genericRef", (fun () -> Task.FromResult(genericRefCaller ()))
    "genericKind", (fun () -> Task.FromResult(genericKindCaller ()))
    "genericLate", (fun () -> Task.FromResult(genericLateCaller ()))
    "genericNested", (fun () -> Task.FromResult(genericNestedCaller ()))
    "genericClosure", (fun () -> Task.FromResult(genericClosureCaller ()))
    "genericInstanceMethod", (fun () -> Task.FromResult(genericInstanceMethodCaller ()))
    "genericStaticMethod", (fun () -> Task.FromResult(genericStaticMethodCaller ()))
    "genericTypeInstance", (fun () -> Task.FromResult(genericTypeInstanceCaller ()))
    "genericTypeStatic", (fun () -> Task.FromResult(genericTypeStaticCaller ()))
    "genericMethodOnType", (fun () -> Task.FromResult(genericMethodOnTypeCaller ())) ]
