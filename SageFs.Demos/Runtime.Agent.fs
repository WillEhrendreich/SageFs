/// Compiling stub for the Agent/MCP actor's runtime extension (demo-actors-
/// plan.md §1.2/§2.4): the Agent island will contribute any extra binds and
/// the bundled viz-page asset here, through `Runtime.fs`'s `actorBinds`/
/// `actorPrologue` extension points — never editing `Runtime.fs`'s core.
/// Island F only reserves this file's fsproj slot.
module SageFs.Demos.Runtime.Agent
