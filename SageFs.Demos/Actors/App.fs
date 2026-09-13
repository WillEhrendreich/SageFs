/// Compiling stub for the App co-actor (demo-actors-plan.md §2.3). Island F
/// only reserves this file's fsproj slot and compile position (before
/// `CellAgent.fs`, after `Actors/Actor.fs`) so the App island can land its
/// `LiveActor` implementation here without touching the fsproj again. No
/// actor logic — that is entirely the App island's own work.
module SageFs.Demos.Actors.App
