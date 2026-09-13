/// Compiling stub for the VS Code actor (demo-actors-plan.md §2.1). Island F
/// only reserves this file's fsproj slot and compile position (before
/// `CellAgent.fs`, after `Actors/Actor.fs`) so the VsCode island can land its
/// `LiveActor` implementation here without touching the fsproj again. No
/// actor logic — that is entirely the VsCode island's own work.
module SageFs.Demos.Actors.VsCode
