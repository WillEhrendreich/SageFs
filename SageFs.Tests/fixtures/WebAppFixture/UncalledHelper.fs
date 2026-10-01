/// The never-entered journey fixture (HotReloadInlinedCalleeJourneyTests).
///
/// `uncalledHelper` is compiled into the app and nothing calls it, so a save of
/// its body lands a patch that no request ever enters. That is the state
/// `NeverEntered` exists to name: the detour is in place, and the new body has
/// not been seen running.
///
/// It is not an inlined callee on purpose. SageFs builds a session without
/// optimizations, so the F# compiler leaves `inline` calls as calls and the JIT
/// is told not to inline, which means an inlined copy cannot hide a patch in a
/// session. (Checked by decompiling the fixture's Debug build: a caller of an
/// `inline` helper in another file compiles to a plain call.)
///
/// Its own file, apart from CalledCallee.fs, so each journey saves one file and
/// reads one verdict.
module WebAppFixture.UncalledHelper

let uncalledHelper () : string = "A"
