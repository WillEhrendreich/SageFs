/// `SageFsPrivateBuildRoot` (Directory.Build.props) is what lets a test build SageFs.Core and SageFs.Host from
/// source without sharing state with any other build of the checkout. The app-runner harness depends on it: its
/// build used to restore and compile those two projects in the checkout's own obj directories, raced the other
/// tiers of the gate doing the same, and died with "Build FAILED, 0 Error(s)" in roughly one gate run in two.
/// These cases ask MSBuild itself where each project will write, so they do not compile anything.
module SageFs.Tests.PrivateBuildRootTests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

/// The shipped projects the harness references, and the three places a restore or build writes besides the
/// project's own source: where its obj goes, where its bin goes, and where its lock file is read and written.
let private projects = [ "SageFs.Core"; "SageFs.Host" ]
let private writeLocations = [ "BaseIntermediateOutputPath"; "OutputPath"; "NuGetLockFilePath" ]

/// What MSBuild evaluates each of `writeLocations` to for `project`, with `extraArguments` as its command line.
let private evaluate (project: string) (extraArguments: string list) : Task<Map<string, string>> =
  task {
    let psi = ProcessStartInfo(FileName = "dotnet", WorkingDirectory = repoRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    for argument in
      [ "msbuild"; Path.Combine(repoRoot, project, project + ".fsproj")
        "-getProperty:" + String.concat "," writeLocations
        "-p:TargetFramework=net11.0"; "-p:TargetFrameworks=" ] @ extraArguments do
      psi.ArgumentList.Add argument
    use proc = Process.Start psi
    let stdout = proc.StandardOutput.ReadToEndAsync()
    let stderr = proc.StandardError.ReadToEndAsync()
    use cts = new CancellationTokenSource(TestTimeouts.workerSessionReady)
    do! proc.WaitForExitAsync cts.Token
    let! text = stdout
    let! errors = stderr
    proc.ExitCode |> Expect.equal (sprintf "msbuild evaluated %s (stderr: %s)" project errors) 0
    return
      writeLocations
      |> List.map (fun name ->
        let m = Regex.Match(text, sprintf "\"%s\":\\s*\"([^\"]*)\"" name)
        m.Success |> Expect.isTrue (sprintf "msbuild printed %s for %s: %s" name project text)
        name, m.Groups.[1].Value.Replace("\\\\", "/"))
      |> Map.ofList
  }

/// Both projects at once: each evaluation is its own `dotnet msbuild` process, a few seconds apiece.
let private evaluateAll (extraArguments: string list) : Task<(string * Map<string, string>) list> =
  task {
    let! evaluated = projects |> List.map (fun project -> evaluate project extraArguments) |> Task.WhenAll
    return List.zip projects (List.ofArray evaluated)
  }

[<Tests>]
let tests =
  testList "SageFsPrivateBuildRoot" [
    // Two builds with two roots cannot overlap, because every path starts with its own root; that is all
    // this case has to show.
    testTask "WHY - with the root set, every project a build of the harness touches restores, builds and locks inside it" {
      let root = Path.Combine(Path.GetTempPath(), "sagefs-private-root-probe") |> fun p -> p.Replace('\\', '/')
      let! evaluated = evaluateAll [ sprintf "-p:SageFsPrivateBuildRoot=%s" root ]
      for project, locations in evaluated do
        for name in writeLocations do
          Map.find name locations
          |> Expect.stringStarts (sprintf "%s's %s is inside the private root, not beside the project where another build writes it" project name) root
    }

    testTask "without the root nothing moves: obj and bin stay beside the project and no lock file is redirected" {
      let! evaluated = evaluateAll []
      for project, locations in evaluated do
        Map.find "OutputPath" locations |> Expect.stringStarts (sprintf "%s builds into its own bin" project) "bin/"
        Map.find "NuGetLockFilePath" locations |> Expect.equal (sprintf "%s keeps the lock file beside the project" project) ""
    }
  ]
