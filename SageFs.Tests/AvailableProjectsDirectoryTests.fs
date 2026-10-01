/// `get_available_projects` scans a directory for projects and solutions. A path
/// that does not exist used to be scanned anyway: the enumeration failed, the
/// failure was swallowed, and the answer read "Available Projects in <path>:
/// (none found)", which a caller takes for a real, empty root. A typo in
/// `working_directory` then cost a wrong conclusion ("this repo has no projects")
/// instead of a correction.
module SageFs.Tests.AvailableProjectsDirectoryTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools

let private context : McpContext =
  { FrictionStore = None
    DiagnosticsChanged = Event<Features.DiagnosticsStore.T>().Publish
    StateChanged = None
    SessionOps = SessionManagementOps.stub
    SessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortOwner = None
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

let private inTempDir (run: string -> System.Threading.Tasks.Task) : System.Threading.Tasks.Task =
  task {
    let dir = Directory.CreateTempSubdirectory("sagefs-available-projects-").FullName
    try
      do! run dir
    finally
      Directory.Delete(dir, true)
  }

[<Tests>]
let tests =
  testList "get_available_projects checks the directory it was given" [
    testTask "WHY — a directory that does not exist is refused by name, not reported as a root with no projects" {
      do! inTempDir (fun dir -> task {
        let missing = Path.Combine(dir, "not-here")
        let! (result: string) = getAvailableProjects context "test" (Some missing)
        result |> Expect.stringContains "names the path it was asked about" missing
        result |> Expect.stringContains "says it does not exist" "does not exist"
        result |> Expect.stringContains "says how to fix it" "working_directory"
        result.Contains "none found" |> Expect.isFalse "and never reads like a scan that found nothing" })
    }

    testTask "WHY — an existing directory with nothing in it is still an ordinary empty answer" {
      do! inTempDir (fun dir -> task {
        let! (result: string) = getAvailableProjects context "test" (Some dir)
        result |> Expect.stringContains "still says where it looked" dir
        result.Contains "does not exist" |> Expect.isFalse "it exists" })
    }

    testTask "WHY — an existing directory's projects are still listed" {
      do! inTempDir (fun dir -> task {
        File.WriteAllText(Path.Combine(dir, "App.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let! (result: string) = getAvailableProjects context "test" (Some dir)
        result |> Expect.stringContains "lists the project" "App.fsproj" })
    }
  ]
