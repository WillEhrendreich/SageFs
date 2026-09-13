/// Compiling stub for the VS Code actor's runtime extension (demo-actors-
/// plan.md §1.2/§2.1): the VsCode island will contribute its own cell binds
/// (a resolved VS Code build dir + node + the built extension dir) and
/// innerScript launch prologue here, through `Runtime.fs`'s `actorBinds`/
/// `actorPrologue` extension points — never editing `Runtime.fs`'s core.
/// Island F only reserves this file's fsproj slot.
module SageFs.Demos.Runtime.VsCode
