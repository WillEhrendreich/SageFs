/// How the worker reaches the agent of the session it drives: what the agent starts from, and a handle on whichever
/// session is active now.
module SageFs.SessionAgent

open SageFs.Features
open SageFs.ProjectLoading

/// What the session's agent starts from, derived from the solution: every project's output is loaded, and dependencies
/// resolve from the projects' outputs, their package directories and the reference assemblies FSI was given.
let agentInitOf (sln: Solution) (hotReload: bool) : HostAgent.AgentInit =
  let referenced (options: string list) =
    options
    |> List.filter (fun s -> s.StartsWith("-r:", System.StringComparison.Ordinal) && s.EndsWith(".dll", System.StringComparison.Ordinal))
    |> List.map (fun s -> s.Substring 3)
  { Projects = sln.Projects |> List.map (fun p -> p.TargetPath)
    ResolveFrom =
      sln.Projects
      |> List.collect (fun p -> (p.PackageReferences |> List.map (fun pr -> pr.FullPath)) @ referenced p.OtherOptions)
    // Only hot reload redefines values, so only hot reload pays for watching them.
    ValueReads =
      match hotReload with
      | true -> SageFs.Middleware.ValueReadTracking.ValueReadWatch.WatchValueReads
      | false -> SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads }

/// Reaches the agent of whichever session is active now, without a round-trip through the eval actor. A session that is
/// not active has no agent, and says so.
type SessionAgent =
  { DiscoverLoaded: unit -> HostAgent.AgentReply<HostAgent.Discovery>
    TakeCoverage: unit -> HostAgent.AgentReply<HostAgent.CoverageReading>
    RunTest: LiveTesting.TestCase -> Async<HostAgent.AgentReply<LiveTesting.TestResult>>
    ValueReads: string list -> HostAgent.AgentReply<SageFs.Middleware.ValueReads.ValueEvidence list> }

/// The agent accessors for whichever session `current` returns right now (null when none is active): the caller reads the
/// live session on every call, so a hard reset that swaps the session is followed without re-wiring.
let ofCurrentSession (current: unit -> FsiSession.IFsiSession) : SessionAgent =
  let inactive = HostAgent.AgentUnavailable "the session is not active"
  let withSession (ask: FsiSession.IFsiSession -> 'a) (whenInactive: 'a) : 'a =
    match current () with
    | null -> whenInactive
    | session -> ask session
  { DiscoverLoaded = fun () -> withSession (fun s -> s.DiscoverLoaded()) inactive
    TakeCoverage = fun () -> withSession (fun s -> s.TakeCoverage()) inactive
    RunTest = fun test -> withSession (fun s -> s.RunTest test) (async { return inactive })
    ValueReads = fun values -> withSession (fun s -> s.ValueReads values) (HostAgent.AgentUnavailable "the session is not active") }
