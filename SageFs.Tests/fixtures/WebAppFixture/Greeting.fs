namespace WebAppFixture

open System.Runtime.CompilerServices

/// The hot-reloaded greeting. The hot-reload verification edits this function's
/// body on disk and asserts the running app serves the new value.
///
/// WHY a FUNCTION: hot reload works by Harmony detouring the JIT-compiled
/// method, so a route that CALLS a function at request time goes through that
/// function's entry point, and detouring `greeting` changes what the running
/// app serves without a restart.
///
/// This comment used to add "…not a mutable field, because a route that reads a
/// mutable field compiles to a direct field load (ldfld) that no method detour
/// can rewire." That is true of C# and FALSE of F#, and it misdirected real
/// work. MEASURED: F# compiles a module-level `let mutable x` read to
/// `call get_x()` and a write to `call set_x()`; the only `ldsfld` is INSIDE the
/// compiler-generated accessor, including in a closure captured into a startup
/// route table. Both accessors detour, cold and after 400k warming iterations.
/// (The one real bypass is address-of — `Interlocked.Increment(&x)` emits
/// `ldsflda` — and F# confines that to the declaring assembly via FS1188.)
///
/// What SageFs does with that reach is a separate, deliberate decision: a
/// re-evaluated module gets NEW backing fields, so redirecting the accessors
/// moves reads and writes to a field whose initialiser just re-ran. Carrying the
/// old value forward ignores the user's edit and resetting it destroys live
/// state, so a changed `let mutable` is refused by name
/// (`RestartReason.MutableModuleState`) rather than guessed at — the position
/// Flutter and Erlang both take. What is NOT optional is that the getter and the
/// setter move together: MEASURED, redirecting one leg alone makes every
/// subsequent write vanish silently, so `HotReloadCore.planDetourUnits` moves
/// both or neither.
///
/// NoInlining stays, and it is worth being accurate about what it suppresses:
/// primarily the F# COMPILER's own optimizer, which in a Release build inlines
/// `greeting`'s body straight into the route closure at compile time so there is
/// no call site left to detour. It also blocks JIT inlining. Both effects
/// disappear under `Optimize=false`, which is how SageFs builds user projects.
module Greeting =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  let greeting () = "hello from sagefs"
