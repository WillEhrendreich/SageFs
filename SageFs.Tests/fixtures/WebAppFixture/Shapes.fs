/// The hot-reload SHAPE MATRIX fixture.
///
/// WHY this file exists: the older fixture (Greeting.fs) is a `namespace` file
/// holding one `[<MethodImpl(NoInlining)>] let greeting () = ...` that the route
/// CALLS at request time — the one shape a method detour has always been able to
/// rewire. It was the exact complement of the bug users hit, so a green outcome
/// gate coexisted with a broken feature. This file is the other side: a dotted
/// MODULE-declared file (`module WebAppFixture.Shapes`) whose handlers are
/// captured BY VALUE into a table at startup — the Falco/Giraffe/Saturn/Oxpecker
/// shape — including the shapes the mechanism CANNOT rewire.
///
/// Every shape below is driven by an assertion: the ones that must reload, and
/// the ones that must be reported as needing a restart instead of quietly
/// serving stale code. None carries [<MethodImpl(NoInlining)>] — a real user
/// does not write it, so the gate must hold without it.
module WebAppFixture.Shapes

/// A type DECLARED IN THIS FILE and used in a handler's signature. This is the
/// shape that broke every real web app: re-evaluating the WHOLE file re-declares
/// this type, so the re-evaluated handler's parameter type is the FSI assembly's
/// copy while the compiled handler's is the project assembly's, the detour
/// matcher's parameter comparison rejects the pair, and the running app keeps its
/// old body. Patching only the CHANGED functions against the compiled module
/// keeps this type identical on both sides.
type Reply = { Body: string }

// ── shapes a detour can rewire ────────────────────────────────────────────────

/// FUNCTION binding whose parameter type is declared in this file.
let localTypeHandler (reply: Reply) : string = "A" + reply.Body

/// FUNCTION binding with a BCL-only signature.
let plainHandler (who: string) : string = "A" + who

/// FUNCTION binding small enough that the JIT would like to inline it into the
/// caller. Here so the gate says whether inlining defeats the detour.
let tinyHandler () : string = "A"

/// VALUE binding holding a lambda: F# emits a static method plus a closure whose
/// Invoke calls it, so detouring the static method reaches a captured copy.
let lambdaHandler : string -> string = fun who -> "A" + who

/// A TYPE MEMBER rather than a module function.
type Renderer() =
  static member Render() : string = "A"

// ── shapes a detour can NOT rewire (known limitations) ───────────────────────

/// The value is computed ONCE at module initialisation and baked into the
/// closure the table captures. This is `let getHome : HttpHandler =
/// Response.ofHtml (pageLayout [])` in Falco terms: nothing is called at request
/// time, so there is no method entry point to re-point. Changing it needs a
/// restart, and the tool must say so rather than refresh the browser on stale
/// output.
let private computeEager () = "A"
let eagerHandler : unit -> string =
  let computedAtStartup = computeEager ()
  fun () -> computedAtStartup

/// A MUTABLE module-level field. It's the app's live data, so editing its
/// initializer keeps the live value (rule 3 of the state spec) and the save
/// says it kept it. The new initializer runs when you reset it.
let mutable mutableField = "A"

// ── the table, captured BY VALUE at startup, exactly like a Falco route list ──

let handlers : (string * (unit -> string)) list =
  [ "localType", fun () -> localTypeHandler { Body = "" }
    "plain", fun () -> plainHandler ""
    "tiny", fun () -> tinyHandler ()
    "lambda", fun () -> lambdaHandler ""
    "member", fun () -> Renderer.Render()
    "eager", eagerHandler
    "mutable", fun () -> mutableField ]

let lookup (name: string) : unit -> string =
  match handlers |> List.tryFind (fun (n, _) -> n = name) with
  | Some (_, handler) -> handler
  | None -> fun () -> "unknown:" + name
