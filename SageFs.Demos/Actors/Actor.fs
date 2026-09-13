/// The actor-dispatch seam (Island F, demo-actors-plan.md §1.2, §1.3). Today
/// the cell-agent hard-calls exactly one actor by name — `Dashboard.launch`
/// (`CellAgent.fs`, pre-seam) — which means every new actor (VS Code, Neovim,
/// the App co-actor, the Agent/MCP viz) would have to edit the same file to
/// plug in, colliding with every other actor island. `LiveActor` is the fix:
/// a plain record of async functions (mirrors `Domain.DemoRuntime`'s
/// injected-edge doctrine — every side effect this project performs is a
/// swappable function value, never a class hierarchy) so the cell-agent can
/// hold a `Map<ActorId, LiveActor>` and route each `WireStep` to the one
/// actor it targets, instead of a fixed handle. The reference shape below is
/// exactly what the existing Dashboard actor already does
/// (`Actors/Dashboard.fs`'s `launch`/`resolve`/`observe`/`close`) — Island F
/// only names the shape once so every future actor implements the same one.
module SageFs.Demos.Actors.Actor

open SageFs.Demos.Domain

/// One live actor inside a cell.
type LiveActor =
  { Id: ActorId
    /// Resolve a wire selector/target to the screen rect this actor measured
    /// live (never a guess — §3 ResolvedTarget doctrine).
    ResolveRect: string -> Async<ScreenRect option>
    /// Observe a wire expectation selector through this actor's own semantic
    /// surface (DOM for dashboard, ext-host for VS Code, RPC for nvim).
    Observe: string -> float -> Async<bool>
    /// Run a non-input client command (OpenFile/RunApp/...) if this actor
    /// supports it; used by Action.Setup steps.
    Command: string -> Async<unit>
    Close: unit -> Async<unit> }
