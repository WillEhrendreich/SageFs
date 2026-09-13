/// `sagefs-demos` CLI entry point (demo-gif-plan.md §8): `doctor | list |
/// storyboard | record | check | measure | compose`, plus the hidden
/// `cell-agent` verb this same binary runs AS when it is executed inside a
/// sealed cell (§4.1: one process, one boundary, no second published tool).
/// Standalone tool — this project never references SageFs.Core/SageFs; it
/// drives the daemon over HTTP and Playwright at runtime instead (the
/// host-closure lesson, §4/roast §1).
module SageFs.Demos.Program

open System

[<RequireQualifiedAccess>]
type Verb =
  | Doctor
  | List
  | Storyboard
  | Record of scenarioId: string option
  | Check
  | Measure
  | Compose
  | CellAgent
  | Unknown of string

let private parseVerb (argv: string[]) : Verb option =
  match argv |> Array.tryHead with
  | Some "doctor" -> Some Verb.Doctor
  | Some "list" -> Some Verb.List
  | Some "storyboard" -> Some Verb.Storyboard
  | Some "record" -> Some(Verb.Record(argv |> Array.tryItem 1))
  | Some "check" -> Some Verb.Check
  | Some "measure" -> Some Verb.Measure
  | Some "compose" -> Some Verb.Compose
  | Some "cell-agent" -> Some Verb.CellAgent
  | Some other -> Some(Verb.Unknown other)
  | None -> None

let private notImplemented (name: string) =
  printfn "%s: not implemented" name
  0

let private allScenarios: Domain.Scenario list =
  [ Scenarios.helloDashboard
    Scenarios.sessionsDashboard
    Scenarios.replDashboard
    Scenarios.ltDashboard ]

let private scenarioById (scenarioId: string) : Domain.Scenario option =
  allScenarios |> List.tryFind (fun s -> Domain.ScenarioId.value s.Id = scenarioId)

/// `record <scenarioId>` — builds the daemon from this worktree's source,
/// records the cell, and writes the artifacts under `artifacts/demos/<id>/`
/// (§1, §10 Phase 1). Every failure is reported with the exact stage reached,
/// never silently swallowed (the job's own "do NOT fake it" instruction).
let private runRecord (scenarioIdArg: string option) : int =
  match scenarioIdArg with
  | None ->
    eprintfn "usage: sagefs-demos record <scenario-id>"
    1
  | Some scenarioId ->

  match scenarioById scenarioId with
  | None ->
    let known = allScenarios |> List.map (fun s -> Domain.ScenarioId.value s.Id) |> String.concat ", "
    eprintfn "sagefs-demos: unknown scenario '%s' (known: %s)" scenarioId known
    1
  | Some scenario ->

  match Runtime.findRepoRoot AppContext.BaseDirectory with
  | None ->
    eprintfn "sagefs-demos: could not find the repo root (no SageFs.slnx above %s)" AppContext.BaseDirectory
    1
  | Some repoRoot ->

  printfn "sagefs-demos: recording '%s' from %s ..." scenarioId repoRoot

  match Runtime.record repoRoot scenario |> Async.RunSynchronously with
  | Error message ->
    eprintfn "sagefs-demos: record failed:\n%s" message
    1
  | Ok(stepLog, artifacts) ->
    printfn "sagefs-demos: recorded '%s'" scenarioId

    for step in stepLog.Steps do
      printfn "  step %d [%s]: %s" step.Index step.Outcome step.Message

    printfn "  gif:      %s" artifacts.Gif
    printfn "  mp4:      %s" artifacts.Mp4
    printfn "  stills:   %s" artifacts.StillsDir
    printfn "  steps.md: %s" artifacts.StepsMd
    printfn "  manifest: %s" artifacts.Manifest

    if stepLog.Steps |> List.forall (fun s -> s.Outcome = "Passed") then 0 else 1

/// The cell-agent verb: runs ONLY inside a sealed cell, as the bwrap-launched
/// pid 1 (`Runtime.fs`'s inner script invokes exactly this). Never throws
/// past `main` — every unhandled exception is caught, reported as a `Failed`
/// StepLog line, and turned into a non-zero exit (§4.11: no `abort()`, ever).
let private runCellAgent () : int =
  try
    CellAgent.run () |> Async.RunSynchronously
  with ex ->
    let log: Wire.StepLog =
      { ScenarioId = "unknown"
        Steps =
          [ { Index = 0
              Caption = "cell-agent crashed"
              Segment = ""
              StartedMs = 0L
              EndedMs = 0L
              PointerPath = []
              ObservedAtMs = 0L
              Outcome = "Failed"
              Message = sprintf "unhandled exception: %s" (ex.ToString()) } ] }

    Wire.serializeStepLog log |> Console.Out.WriteLine
    Console.Out.Flush()
    1

[<EntryPoint>]
let main argv =
  try
    match parseVerb argv with
    | Some Verb.Doctor -> notImplemented "doctor"
    | Some Verb.List -> notImplemented "list"
    | Some Verb.Storyboard -> notImplemented "storyboard"
    | Some(Verb.Record scenarioIdArg) -> runRecord scenarioIdArg
    | Some Verb.Check -> notImplemented "check"
    | Some Verb.Measure -> notImplemented "measure"
    | Some Verb.Compose -> notImplemented "compose"
    | Some Verb.CellAgent -> runCellAgent ()
    | Some(Verb.Unknown other) ->
      eprintfn "sagefs-demos: unknown verb '%s'" other
      eprintfn "usage: sagefs-demos <doctor|list|storyboard|record|check|measure|compose> [args]"
      1
    | None ->
      eprintfn "usage: sagefs-demos <doctor|list|storyboard|record|check|measure|compose> [args]"
      1
  with ex ->
    // §4.11: no unhandled exception ever reaches the runtime's abort() path
    // — this is the top-level try/with every entry point in this tool needs.
    eprintfn "sagefs-demos: fatal: %s" (ex.ToString())
    1
