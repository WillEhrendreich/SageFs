namespace SageFs.Simulation

open System
open SageFs.Features

/// Deterministic Simulation Testing for the claim `list_references` makes about how far the compiler looked.
///
/// A session has some projects. Each costs a number of simulated seconds to check, and may fail instead. One budget
/// covers all of them, in order. The decision folded is the REAL one: `ReferenceGather.run` over a simulated clock and a
/// simulated checker, then `ReferenceReply.ofAnswer`. The truth is an independent fold (`Truth`) that never reads the
/// reply: which projects the budget reaches, and the sites of those projects.
///
/// Same design rules as `CallerPendingSim`:
///   * Chaos is DATA: a `Scenario` is a budget and an ordered project list. Same scenario, same trace.
///   * Twins reintroduce the bugs the invariants exist to catch. The one that matters here counts what a partial look
///     found and calls it project-wide, which is the file-scoped-count-labelled-project-wide mistake the rung exists to
///     refuse.
module ReferenceScopeSim =

  [<RequireQualifiedAccess>]
  type Behavior =
    /// The check answers after `cost` seconds, when the budget leaves that long.
    | Answers
    /// The compiler raises after `cost` seconds.
    | Raises

  type ProjectSpec =
    { Name: string
      Cost: float
      Behavior: Behavior
      Sites: int }

  type Scenario =
    { Seed: int
      Budget: float
      Projects: ProjectSpec list }

  /// The truth for a scenario, from the specs alone.
  type Truth =
    { Reached: string list
      NotReached: string list
      Sites: ReferenceSite list
      /// Simulated seconds the whole run took.
      Elapsed: float }

  type Trace =
    { Scenario: Scenario
      Reply: ReferenceReply
      /// The scope line a reader sees.
      ScopeLine: string
      Truth: Truth
      /// Simulated seconds the real run took, read from the simulated clock.
      Elapsed: float
      Reducer: string }

  [<RequireQualifiedAccess>]
  type Policy =
    /// The real behaviour.
    | Real
    /// TWIN: any look that found something is called project-wide, whatever it skipped.
    | LabelsPartialAsProjectWide
    /// TWIN: each project gets the whole budget, so the cost of a question grows with the project count.
    | BudgetPerProject
    /// TWIN: a partial scope forgets which projects it did not reach.
    | ForgetsWhatItSkipped

  let siteOf (spec: ProjectSpec) (i: int) : ReferenceSite =
    { File = sprintf "/sim/%s/f%d.fs" spec.Name i; Line = i + 1; Column = 0; Kind = ReferenceKind.Use }

  let sitesOf (spec: ProjectSpec) : ReferenceSite list = [ for i in 0 .. spec.Sites - 1 -> siteOf spec i ]

  /// Truth: walk the projects in order against the budget. A project is reached when the budget has not run out when
  /// it starts, it answers, and its cost fits what is left. A project that raises costs its time and is not reached. A
  /// project whose cost does not fit costs the rest of the budget and is not reached, which skips everything after it.
  let truthOf (scenario: Scenario) : Truth =
    let step (elapsed, reached, notReached, sites) (spec: ProjectSpec) =
      let left = scenario.Budget - elapsed
      match left > 0.0 with
      | false -> elapsed, reached, notReached @ [ spec.Name ], sites
      | true ->
        match spec.Behavior with
        | Behavior.Raises -> elapsed + min spec.Cost left, reached, notReached @ [ spec.Name ], sites
        | Behavior.Answers ->
          match spec.Cost <= left with
          | true -> elapsed + spec.Cost, reached @ [ spec.Name ], notReached, sites @ sitesOf spec
          | false -> scenario.Budget, reached, notReached @ [ spec.Name ], sites
    let elapsed, reached, notReached, sites = scenario.Projects |> List.fold step (0.0, [], [], [])
    { Reached = reached; NotReached = notReached; Sites = sites |> List.distinct |> List.sortBy (fun s -> s.File, s.Line, s.Column); Elapsed = elapsed }

  let run (policy: Policy) (scenario: Scenario) : Trace =
    let clock = ref TimeSpan.Zero
    let specs = scenario.Projects |> List.map (fun p -> p.Name, p) |> Map.ofList
    let seconds (s: float) = TimeSpan.FromSeconds s
    let check (project: string) (left: TimeSpan) : Async<ProjectCheck> =
      async {
        let spec = specs.[project]
        let cost = seconds spec.Cost
        // The twin that gives every project the whole budget never cuts a check off short of it.
        let limit =
          match policy with
          | Policy.BudgetPerProject -> seconds scenario.Budget
          | Policy.Real
          | Policy.LabelsPartialAsProjectWide
          | Policy.ForgetsWhatItSkipped -> left
        match spec.Behavior with
        | Behavior.Raises ->
          clock.Value <- clock.Value + min cost limit
          return ProjectCheck.Raised "simulated compiler failure"
        | Behavior.Answers ->
          match cost <= limit with
          | true ->
            clock.Value <- clock.Value + cost
            return ProjectCheck.Answered(sitesOf spec, [])
          | false ->
            clock.Value <- clock.Value + limit
            return ProjectCheck.CutOff
      }
    let answer =
      ReferenceGather.run (seconds scenario.Budget) (fun () -> clock.Value) check (scenario.Projects |> List.map (fun p -> p.Name))
      |> Async.RunSynchronously
    let real = ReferenceReply.ofAnswer answer
    let reply =
      match policy, real with
      | Policy.LabelsPartialAsProjectWide, ReferenceReply.Counted(CountedScope.SomeProjects(checkedNames, _), sites, unclean) ->
        ReferenceReply.Counted(CountedScope.ProjectWide checkedNames, sites, unclean)
      | Policy.ForgetsWhatItSkipped, ReferenceReply.Counted(CountedScope.SomeProjects(checkedNames, _), sites, unclean) ->
        ReferenceReply.Counted(CountedScope.SomeProjects(checkedNames, []), sites, unclean)
      | Policy.Real, _
      | Policy.BudgetPerProject, _
      | Policy.LabelsPartialAsProjectWide, _
      | Policy.ForgetsWhatItSkipped, _ -> real
    { Scenario = scenario
      Reply = reply
      ScopeLine = ReferenceReply.describe reply
      Truth = truthOf scenario
      Elapsed = clock.Value.TotalSeconds
      Reducer =
        (match policy with
         | Policy.Real -> "real (ReferenceGather.run over ReferenceReply.ofAnswer)"
         | Policy.LabelsPartialAsProjectWide -> "twin-labels-partial-as-project-wide"
         | Policy.BudgetPerProject -> "twin-budget-per-project"
         | Policy.ForgetsWhatItSkipped -> "twin-forgets-what-it-skipped") }
