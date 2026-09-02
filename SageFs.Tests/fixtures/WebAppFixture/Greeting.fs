namespace WebAppFixture

open System.Runtime.CompilerServices

/// The hot-reloaded greeting. The hot-reload verification edits this function's
/// body on disk and asserts the running app serves the new value.
///
/// WHY a FUNCTION with NoInlining, not a mutable field: hot reload works by
/// Harmony detouring the JIT-compiled method. A route that reads a mutable
/// field compiles to a direct field load (ldfld) in the route closure, which
/// no method detour can rewire. A route that CALLS a function at request time
/// goes through the function's method entry point, so detouring `greeting`
/// changes what the running app serves — without restart. NoInlining prevents
/// the JIT from inlining `greeting` into the route handler at startup (an
/// inlined copy would bypass the detoured entry point forever).
module Greeting =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  let greeting () = "hello from sagefs"
