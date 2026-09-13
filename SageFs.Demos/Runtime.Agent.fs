/// The Agent/MCP actor's runtime extension (demo-actors-plan.md
/// §1.2/§2.4). Every other actor island needs this file for extra cell
/// binds (a second browser/runtime, an editor build, a plugin checkout) —
/// the Agent actor needs none: it drives the daemon's MCP port that is
/// ALREADY bound into every cell (`cellSpec`'s base `RoBinds` already
/// expose `chromeDir`, and the daemon itself already listens on
/// `127.0.0.1:47749` inside `--unshare-net`, per `innerScript`), and its
/// viz page is written directly into the cell's own writable
/// `/home/demo` by `Actors/Agent.fs`'s `launch` rather than being a static
/// asset bound in from the host. So this file intentionally contributes
/// empty `actorBinds`/`actorPrologue` — reproducing exactly what Island F's
/// `record` already passes today (`[] []`) for any client that needs
/// nothing extra — documented here so a reader looking for "what does the
/// Agent island add to the cell" gets a real, honest answer: nothing, by
/// design, not an oversight.
module SageFs.Demos.Runtime.Agent

/// No extra read-only binds needed for the Agent actor.
let actorBinds: (string * string) list = []

/// No extra innerScript prologue needed for the Agent actor — it launches
/// entirely from the cell-agent process itself (`Actors/Agent.fs`'s
/// `launch`), after the daemon is already confirmed healthy, exactly like
/// every other actor's own launch step.
let actorPrologue: string list = []
