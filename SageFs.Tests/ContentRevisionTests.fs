/// A build's revision is the last commit that changed what it is built from, never HEAD (Directory.Build.targets,
/// SageFsContentRevision). The SDK stamps HEAD into every assembly and SourceLink writes it into a file the compiler
/// reads, so ANY commit, even an empty one, rebuilt every project: a measured 750 CPU seconds per commit.
///
/// The case runs the repo's own Directory.Build.props and Directory.Build.targets over a throwaway git repo with one
/// tiny project, because what is being pinned is MSBuild's behaviour with those two files, not their text.
module SageFs.Tests.ContentRevisionTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

/// Runs a program to completion with both pipes drained, so a chatty child cannot block on a full pipe.
let private run (workingDirectory: string) (file: string) (args: string list) : Threading.Tasks.Task<int * string> =
  task {
    let psi = ProcessStartInfo(file)
    psi.WorkingDirectory <- workingDirectory
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    args |> List.iter psi.ArgumentList.Add
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    use cts = new Threading.CancellationTokenSource(TestTimeouts.processStartPatience)
    do! p.WaitForExitAsync cts.Token
    let! stdout = out
    let! stderr = err
    return p.ExitCode, stdout + stderr
  }

let private git (dir: string) (args: string list) =
  task {
    let! code, output = run dir "git" ([ "-c"; "user.email=t@example.test"; "-c"; "user.name=t"; "-c"; "commit.gpgsign=false" ] @ args)
    match code with
    | 0 -> return output.Trim()
    | _ -> return failtestf "git %s failed (%d): %s" (String.concat " " args) code output
  }

/// The informational version MSBuild computes for the probe project, the string an assembly would be stamped with.
let private informationalVersion (dir: string) =
  task {
    let! code, output =
      run dir "dotnet" [ "msbuild"; Path.Combine("Probe", "Probe.csproj"); "-t:GetAssemblyAttributes"; "-getProperty:InformationalVersion"; "-nologo" ]
    match code with
    | 0 -> return output.Trim()
    | _ -> return failtestf "msbuild failed (%d): %s" code output
  }

let tests =
  testList "Content revision" [
    TestInfrastructure.Integration.hostCaseTask "a commit that touches nothing the project is built from does not change its revision" <| fun () ->
      task {
        let dir = Path.Combine(Path.GetTempPath(), "sagefs-content-revision-" + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory dir |> ignore
        try
          File.Copy(Path.Combine(repoRoot, "Directory.Build.props"), Path.Combine(dir, "Directory.Build.props"))
          File.Copy(Path.Combine(repoRoot, "Directory.Build.targets"), Path.Combine(dir, "Directory.Build.targets"))
          Directory.CreateDirectory(Path.Combine(dir, "Probe")) |> ignore
          Directory.CreateDirectory(Path.Combine(dir, "docs")) |> ignore
          File.WriteAllText(Path.Combine(dir, "Probe", "Probe.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>")
          File.WriteAllText(Path.Combine(dir, "Probe", "C.cs"), "class C {}")
          let! _ = git dir [ "init"; "-q" ]
          let! _ = git dir [ "add"; "-A" ]
          let! _ = git dir [ "commit"; "-q"; "-m"; "the project" ]
          let! projectCommit = git dir [ "rev-parse"; "HEAD" ]
          File.WriteAllText(Path.Combine(dir, "docs", "readme.md"), "words")
          let! _ = git dir [ "add"; "-A" ]
          let! _ = git dir [ "commit"; "-q"; "-m"; "docs only" ]
          let! head = git dir [ "rev-parse"; "HEAD" ]
          head |> Expect.notEqual "the docs commit is a different commit" projectCommit

          let! afterDocs = informationalVersion dir
          afterDocs
          |> Expect.stringEnds "the revision is the commit that last changed the project, so a docs commit rebuilds nothing" ("+" + projectCommit)

          File.WriteAllText(Path.Combine(dir, "Probe", "C.cs"), "class C { }")
          let! _ = git dir [ "add"; "-A" ]
          let! _ = git dir [ "commit"; "-q"; "-m"; "the project changes" ]
          let! changedCommit = git dir [ "rev-parse"; "HEAD" ]
          let! afterChange = informationalVersion dir
          afterChange
          |> Expect.stringEnds "and a commit that changes the project moves it, so two different builds are never labelled the same" ("+" + changedCommit)
        finally
          try Directory.Delete(dir, true) with _ -> ()
      }
  ]

[<Tests>]
let contentRevisionTests = tests
