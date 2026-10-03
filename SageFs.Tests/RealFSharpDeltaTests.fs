/// The emitter against what the F# compiler really writes.
///
/// MetadataDeltaTests runs the emitter against assemblies Cecil wrote from a language of its own. That shows the
/// emitter is right about tokens, heaps and chains; it cannot show it copes with the shapes F# makes: closure
/// classes and Debug-build helper methods and module-level fields named after their line, a `<StartupCode$...>`
/// class per file, a state machine for a task body, a generic function, an instance member with state in a field.
/// This builds the run_app fixture's Handlers.fs twice with the real compiler (Debug, which is what SageFs builds a
/// session with), edits the second copy the way the RunAppDeltaTests rows do (and adds two lines above everything,
/// which renames every line-numbered name in the file), and has the emitter patch a process that loaded the first.
/// The first is run twice: as the compiler wrote it, and rewritten by the real CoverageInstrumenter, which is what
/// the worker loads.
///
/// It is a host-tier test because it runs `dotnet build`. It is what the planner's rows will stand on.
module SageFs.Tests.RealFSharpDeltaTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Features.MetadataDelta
open SageFs.Tests.DeltaChild

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private repoRoot = RepoPaths.repoPathFull [||]

let private fixtureFolder = Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "RunAppDeltaFixture")

/// The runtime this test host runs on, which is the one the child will load the build into.
let private framework = sprintf "net%d.0" Environment.Version.Major

/// The fixture's Handlers.fs as a library. It lives under the repo, like the other fixtures' scratch projects, so
/// Directory.Packages.props pins FSharp.Core to the version the test host has loaded.
let private projectText =
  String.concat "\n" [
    "<Project Sdk=\"Microsoft.NET.Sdk\">"
    "  <PropertyGroup>"
    sprintf "    <TargetFramework>%s</TargetFramework>" framework
    "    <OutputType>Library</OutputType>"
    "    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>"
    "    <RestoreLockedMode>false</RestoreLockedMode>"
    "  </PropertyGroup>"
    "  <ItemGroup>"
    "    <Compile Include=\"Handlers.fs\" />"
    "  </ItemGroup>"
    "</Project>"
    "" ]

let private build (directory: string) : Task<string> =
  task {
    let info = ProcessStartInfo("dotnet")
    for argument in [ "build"; Path.Combine(directory, "RealFixture.fsproj"); "-c"; "Debug"; "--nologo" ] do
      info.ArgumentList.Add argument
    info.WorkingDirectory <- directory
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()
    do! child.WaitForExitAsync()
    let! out = output
    let! err = errors
    child.ExitCode |> Expect.equal (sprintf "the real F# compiler built %s:\n%s\n%s" directory out err) 0
    return Path.Combine(directory, "bin", "Debug", framework, "RealFixture.dll")
  }

/// Each edit is one string that appears once in Handlers.fs.
let private edits : (string * string) list =
  [ // Two lines above everything: every closure, every Debug-build helper method and every module-level value's field
    // is named after its line, so every one of them is renamed, and the emitter has to see that nothing but the
    // bodies below changed.
    "open System.Threading.Tasks\n", "open System.Threading.Tasks\n\n// two lines added above everything\n"
    "\"generic:A:\"", "\"generic:B:\""
    "\"closure:A\"", "\"closure:B\""
    "\"instance:A#\"", "\"instance:B#\""
    "\"taskBody:A\"", "\"taskBody:B\""
    "let addedCaller () : string = \"addedMethod:A\"",
    "let addedHelper () : string = \"addedMethod:B\"\n\nlet addedCaller () : string = addedHelper ()" ]

let private handlers = "RunAppDeltaFixture.Handlers"

let private invoke (name: string) : Op = Op.Invoke (handlers, name)

[<Tests>]
let realFSharpDeltaTests =
  Integration.hostList "metadata delta on a real F# build" [
    testTask "WHY - a build of the run_app fixture is patched in place by the delta of an edited build, plain and instrumented the way the worker loads it: a generic function at every instantiation, a closure the app holds, an instance member with state, a task body, an added method" {
      let work = Path.Combine(fixtureFolder, ".runs", sprintf "real-%s" (Guid.NewGuid().ToString "N"))
      try
        let source = File.ReadAllText(Path.Combine(fixtureFolder, "Handlers.fs"))
        let edited =
          edits
          |> List.fold
            (fun (text: string) (find, replace) ->
              text.Split([| find |], StringSplitOptions.None).Length - 1 |> Expect.equal (sprintf "the edit anchor appears once: %s" find) 1
              text.Replace(find, replace))
            source
        let write (name: string) (text: string) =
          let directory = Path.Combine(work, name)
          Directory.CreateDirectory directory |> ignore
          File.WriteAllText(Path.Combine(directory, "Handlers.fs"), text)
          File.WriteAllText(Path.Combine(directory, "RealFixture.fsproj"), projectText)
          directory
        let before = write "before" source
        let after = write "after" edited
        // Both builds run at once; each is awaited below.
        let buildingBefore = build before
        let buildingAfter = build after
        let! baselineDll = buildingBefore
        let! nextDll = buildingAfter
        // The same edit, patched into two baselines: the build as the compiler wrote it, and the build as the worker
        // loads it, which the real CoverageInstrumenter has rewritten with a probe in front of every sequence point.
        for name, instrument in [ "plain", false; "instrumented", true ] do
          // The child loads a copy, so the build's own output is left alone.
          let directory = Path.Combine(work, name)
          let baselineCopy = Path.Combine(directory, "base", "RealFixture.dll")
          Directory.CreateDirectory(Path.GetDirectoryName baselineCopy) |> ignore
          File.Copy(baselineDll, baselineCopy)
          File.Copy(Path.ChangeExtension(baselineDll, ".pdb"), Path.ChangeExtension(baselineCopy, ".pdb"))
          let probes =
            match instrument with
            | false -> ProbeStripping.KeepEveryInstruction
            | true ->
              match CoverageInstrumenter.instrumentAssemblyInPlace baselineCopy with
              | Result.Ok map ->
                (map.TotalProbes, 0) |> Expect.isGreaterThan "the real instrumenter put probes in the real build"
                ProbeStripping.StripCoverageProbes CoverageProbe.hitSymbol
              | Result.Error message -> failtestf "instrumenting the real build failed: %s" message
          let chain = DeltaChain.Start(PeImage.OfFile baselineCopy, probes)
          match chain.Prepare(Guid.NewGuid(), PeImage.OfFile nextDll) with
          | PrepareOutcome.Refused causes ->
            failtestf "[%s] the emitter refused the edit of a real F# build: %s" name (String.Join("; ", causes |> List.map RudeCause.describe))
          | PrepareOutcome.NothingChanged -> failtestf "[%s] the emitter saw no change between two builds that differ" name
          | PrepareOutcome.Ready prepared ->
            File.WriteAllBytes(Path.Combine(directory, "d.meta"), prepared.Payload.Metadata)
            File.WriteAllBytes(Path.Combine(directory, "d.il"), prepared.Payload.Il)
            File.WriteAllText(Path.Combine(directory, "d.tokens"), String.Join(",", prepared.Payload.MethodTokens))
            let! result =
              DeltaChild.runWithin TestTimeouts.workerSessionReady directory ModifiableAssemblies.Debug
                [ Op.Load "base/RealFixture.dll"
                  // Before: every call, the instance member twice so it has state to keep.
                  invoke "generic"
                  invoke "closure"
                  invoke "instance"
                  invoke "instance"
                  invoke "taskBody"
                  invoke "addedCaller"
                  Op.Apply ("d.meta", "d.il")
                  // After: the same calls, and a generic instantiation that had not run.
                  invoke "generic"
                  invoke "genericLate"
                  invoke "closure"
                  invoke "instance"
                  invoke "taskBody"
                  invoke "addedCaller" ]
            result.ExitCode |> Expect.equal (sprintf "[%s] the child lived: %s" name result.Stderr) 0
            DeltaChild.applyOutcomes result |> Expect.equal (sprintf "[%s] the runtime took the delta" name) [ "Applied" ]
            let row (method': string) = sprintf "%s.%s=" handlers method'
            DeltaChild.factsOf FactKind.Invoked result
            |> Expect.equal
              (sprintf "[%s] before the delta the old bodies, after it the new ones, in the same process (%d bytes of metadata, %d of IL, %d methods)" name prepared.Payload.Metadata.Length prepared.Payload.Il.Length prepared.Payload.MethodTokens.Length)
              [ row "generic" + "generic:A:7|generic:A:s|generic:A:(1,2)"
                row "closure" + "closure:A!?"
                row "instance" + "instance:A#1"
                row "instance" + "instance:A#2"
                row "taskBody" + "taskBody:A"
                row "addedCaller" + "addedMethod:A"
                row "generic" + "generic:B:7|generic:B:s|generic:B:(1,2)"
                row "genericLate" + "generic:B:1.5"
                row "closure" + "closure:B!?"
                // The object is the one that counted to two before the delta.
                row "instance" + "instance:B#3"
                row "taskBody" + "taskBody:B"
                row "addedCaller" + "addedMethod:B" ]
      finally
        try Directory.Delete(work, true) with _ -> ()
    }
  ]
