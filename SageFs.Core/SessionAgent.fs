/// How the worker reaches the agent of the session it drives: what the agent starts from, and a handle on whichever
/// session is active now.
module SageFs.SessionAgent

open SageFs.Features
open SageFs.ProjectLoading

/// What the session's agent starts from, derived from the solution: every project's output is loaded, and dependencies
/// resolve from the projects' outputs, their package directories and the reference assemblies FSI was given.
let agentInitOf (sln: Solution) : HostAgent.AgentInit =
  let referenced (options: string list) =
    options
    |> List.filter (fun s -> s.StartsWith("-r:", System.StringComparison.Ordinal) && s.EndsWith(".dll", System.StringComparison.Ordinal))
    |> List.map (fun s -> s.Substring 3)
  { Projects = sln.Projects |> List.map (fun p -> p.TargetPath)
    ResolveFrom =
      sln.Projects
      |> List.collect (fun p -> (p.PackageReferences |> List.map (fun pr -> pr.FullPath)) @ referenced p.OtherOptions) }

/// Reaches the agent of whichever session is active now, without a round-trip through the eval actor. A session that is
/// not active has no agent, and says so.
type SessionAgent =
  { DiscoverLoaded: unit -> HostAgent.AgentReply<HostAgent.Discovery>
    RunTest: LiveTesting.TestCase -> Async<HostAgent.AgentReply<LiveTesting.TestResult>> }
