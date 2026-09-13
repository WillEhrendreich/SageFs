/// Compiling stub for the Agent/MCP actor (demo-actors-plan.md §2.4). Island
/// F only reserves this file's fsproj slot and compile position (before
/// `CellAgent.fs`, after `Actors/Actor.fs`) so the Agent island can land its
/// `LiveActor` implementation here without touching the fsproj again. No
/// actor logic — that is entirely the Agent island's own work.
module SageFs.Demos.Actors.Agent
