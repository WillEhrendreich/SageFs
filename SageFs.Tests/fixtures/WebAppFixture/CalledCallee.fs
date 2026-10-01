/// The called-helper journey fixture (HotReloadInlinedCalleeJourneyTests): `renderCalled`
/// enters `calledHelper` on every request, so a detour of it changes what the route serves and
/// the save ends as Patched. `NoInlining` keeps that true even in an optimized build.
module WebAppFixture.CalledCallee

open System.Runtime.CompilerServices

[<MethodImpl(MethodImplOptions.NoInlining)>]
let calledHelper () : string = "A"

[<MethodImpl(MethodImplOptions.NoInlining)>]
let renderCalled () : string = calledHelper ()
