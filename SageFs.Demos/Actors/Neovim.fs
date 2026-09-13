/// Compiling stub for the Neovim actor (demo-actors-plan.md §2.2). Island F
/// only reserves this file's fsproj slot and compile position (before
/// `CellAgent.fs`, after `Actors/Actor.fs`) so the Neovim island can land its
/// `LiveActor` implementation here without touching the fsproj again. No
/// actor logic — that is entirely the Neovim island's own work.
module SageFs.Demos.Actors.Neovim
