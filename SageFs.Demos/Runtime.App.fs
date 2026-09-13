/// Compiling stub for the App co-actor's runtime extension (demo-actors-
/// plan.md §1.2/§2.3): the App island will contribute the extra cell binds
/// for a second Chromium/Raylib runtime here, through `Runtime.fs`'s
/// `actorBinds`/`actorPrologue` extension points — never editing
/// `Runtime.fs`'s core. Island F only reserves this file's fsproj slot.
module SageFs.Demos.Runtime.App
