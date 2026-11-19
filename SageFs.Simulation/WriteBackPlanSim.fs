namespace SageFs.Simulation

open SageFs.Features.Tweak

/// DST for the granularity rule: a caller can accept any subset of the chunks
/// a write-back plan proposes, and the daemon must apply exactly those, in an
/// order that respects the dependencies the plan displayed. Chaos is data:
/// the op list is the scheduler. The decision under test is the real
/// `WriteBackPlan.order`; this module only builds the world it decides over.
module WriteBackPlanSim =

  [<RequireQualifiedAccess>]
  type Op =
    | Add of id: string * dependsOn: string list
    | Accept of id: string

  type Scenario = { Seed: int; Ops: Op list }

  type Step =
    { Op: Op
      Accepted: string list
      Graph: (string * string list) list
      Order: Result<string list, WriteBackPlan.OrderRefusal> }

  type Trace = { Scenario: Scenario; Steps: Step list }

  let private emptyPlan =
    { WriteBackPlan.Plan.File = "/repo/App.fs"
      WriteBackPlan.Plan.Source = "module App\n"
      WriteBackPlan.Plan.Chunks = [] }

  let run (scenario: Scenario) : Trace =
    let _, steps =
      scenario.Ops
      |> List.fold (fun (chunks, acc) op ->
        match op with
        | Op.Add(id, deps) ->
          let chunk =
            { WriteBackPlan.Chunk.Id = id
              WriteBackPlan.Chunk.Name = id
              WriteBackPlan.Chunk.File = "/repo/App.fs"
              WriteBackPlan.Chunk.StartLine = 1
              WriteBackPlan.Chunk.EndLine = 1
              WriteBackPlan.Chunk.Replacement = "let " + id + " = 2"
              WriteBackPlan.Chunk.DependsOn = deps
              WriteBackPlan.Chunk.Status = WriteBackPlan.ChunkStatus.Ready }
          // One chunk per id: a later Add of the same id replaces the earlier
          // one, so the graph the invariant reads is the graph `order` decided on.
          chunk :: (chunks |> List.filter (fun c -> c.Id <> id)), acc
        | Op.Accept id ->
          let plan = { emptyPlan with WriteBackPlan.Plan.Chunks = chunks }
          let accepted = acc |> List.rev |> List.choose (fun s -> match s.Op with Op.Accept x -> Some x | _ -> None)
          let accepted' = accepted @ [ id ]
          let graph = chunks |> List.map (fun c -> c.Id, c.DependsOn)
          let step = { Op = op; Accepted = accepted'; Graph = graph; Order = WriteBackPlan.order plan accepted' }
          chunks, acc @ [ step ]) ([], [])
    { Scenario = scenario; Steps = steps }
