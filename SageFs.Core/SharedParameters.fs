namespace SageFs

/// The sentences for the parameters that many tools take.
///
/// `working_directory` was described 18 different ways across 50 tools, `session_id` six ways across 19 and the agent name
/// six ways across 17, and every copy travels in `tools/list` on every session. A parameter that several tools share now has
/// a few named sentences, one per role it plays, and a tool picks the one that fits. `ToolListProfileTests` holds every
/// registered tool to them: a private variant is a failing case. What a tool needs to say beyond the role belongs in its
/// manual, one call away through `discover_features` with `tool`.
///
/// The names are the wire names, which differ for the agent name (`agentName` on most tools, `agent_name` on the leases):
/// renaming a wire name is a different change and is not made here.
module SharedParameters =
  module WorkingDirectory =
    /// A tool that acts on a session: the one it names, or the one this connection is working in.
    [<Literal>]
    let Route = "The client's working directory; picks the session that uses it."

    /// A tool that makes a session.
    [<Literal>]
    let Create = "Directory the new session works in."

    /// A tool about a repository and its cohort: any path inside the repository will do.
    [<Literal>]
    let Repository = "A path inside the repository; picks the cohort. Use the same one every time."

    /// A lease: part of what names it.
    [<Literal>]
    let Lease = "Your worktree. Part of the lease's identity."

    /// A tool that reads the disk and routes to no session.
    [<Literal>]
    let Scan = "Directory to scan; defaults to the active session's."

    /// A minted token's checkout.
    [<Literal>]
    let Checkout = "Absolute path of the checkout this run works in. The token acts only on sessions inside it. Name this or session_id."

  module SessionId =
    /// A tool that acts on a session.
    [<Literal>]
    let Route = "Session id; wins over working_directory."

    /// A tool that is about one named session and nothing else.
    [<Literal>]
    let Target = "Session id from list_sessions."

    /// A minted token's one session.
    [<Literal>]
    let Mint = "The one session the token may act on, instead of a checkout. Name this or working_directory."

  module AgentName =
    /// A cohort tool: the name the caller joined with, which is how its authority is looked up.
    [<Literal>]
    let Member = "Your agent name, the one you joined the cohort with."

    /// A tool that records who called: a short, stable id.
    [<Literal>]
    let Identify = "Your agent name, e.g. 'claude'."

    /// A lease: sub-agents that share one connection each need their own.
    [<Literal>]
    let Lease = "Your own agent name, distinct from sibling sub-agents'. Names your lease."

  /// The wire names of the parameters that have named sentences.
  [<Literal>]
  let WorkingDirectoryName = "working_directory"

  [<Literal>]
  let SessionIdName = "session_id"

  [<Literal>]
  let AgentNameName = "agentName"

  [<Literal>]
  let LeaseAgentNameName = "agent_name"

  /// The parameter names that have named sentences.
  let names : string list = [ WorkingDirectoryName; SessionIdName; AgentNameName; LeaseAgentNameName ]

  /// The sentences a parameter may carry, or None for a parameter that is not shared.
  let sentencesFor (parameter: string) : string list option =
    match parameter with
    | WorkingDirectoryName ->
      Some
        [ WorkingDirectory.Route
          WorkingDirectory.Create
          WorkingDirectory.Repository
          WorkingDirectory.Lease
          WorkingDirectory.Scan
          WorkingDirectory.Checkout ]
    | SessionIdName -> Some [ SessionId.Route; SessionId.Target; SessionId.Mint ]
    | AgentNameName -> Some [ AgentName.Member; AgentName.Identify ]
    | LeaseAgentNameName -> Some [ AgentName.Lease ]
    | _ -> None
