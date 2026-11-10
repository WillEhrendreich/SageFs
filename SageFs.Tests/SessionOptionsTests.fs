module SageFs.Tests.SessionOptionsTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Tests.TestInfrastructure

let private configWith (edit: DirectoryConfig -> DirectoryConfig) : DirectoryConfig = edit DirectoryConfigDefaults.empty

let private hostEnv (sageFsEnv: (string * string) list) (config: DirectoryConfig) =
  let options, _ = SessionOptions.ofConfig config
  SessionOptions.hostEnvironment sageFsEnv options

/// A name an environment variable may have, that is on nobody's refusal list.
let private genPlainName : Gen<string> =
  Gen.elements [ 'A' .. 'Z' ]
  |> Gen.listOfLength 6
  |> Gen.map (fun letters -> "USER_" + String(List.toArray letters))

let private genPoisonedName : Gen<string> = Gen.elements ProcessEnvironment.poisonedVariables

let private genConfig : Gen<DirectoryConfig> =
  gen {
    let! gc = Gen.elements [ WorkstationGc; ServerGc ]
    let! names = Gen.listOf genPlainName
    let! args = Gen.listOf (Gen.elements [ "--no-warn:1182"; "--langversion:preview" ])
    let! artifacts = Gen.oneof [ Gen.constant ProjectChooses; genPlainName |> Gen.map (fun n -> ArtifactsIn("/tmp/" + n)) ]
    return { DirectoryConfigDefaults.empty with Gc = gc; SessionEnv = names |> List.map (fun n -> EnvEntry(n, "v")); DefaultArgs = args; Artifacts = artifacts }
  }

[<Tests>]
let sessionOptionsTests =
  testList "SessionOptions" [

    testCase "WHY - a Server GC choice puts DOTNET_gcServer=1 in the host's environment and Workstation does not" <| fun _ ->
      let server, _ = hostEnv [] (configWith (fun c -> { c with Gc = ServerGc }))
      let workstation, _ = hostEnv [] (configWith (fun c -> { c with Gc = WorkstationGc }))
      server |> List.contains ("DOTNET_gcServer", "1") |> Expect.isTrue "Server sets DOTNET_gcServer=1"
      workstation |> List.exists (fun (name, _) -> name = "DOTNET_gcServer") |> Expect.isFalse "Workstation sets nothing"

    testCase "WHY - a config that says nothing leaves the host's environment exactly as SageFs built it" <| fun _ ->
      let sageFs = [ "SAGEFS_PROJECT_OUTPUT", "/p/bin" ]
      let env, clashes = hostEnv sageFs DirectoryConfigDefaults.empty
      env |> Expect.equal "unchanged" sageFs
      clashes |> Expect.isEmpty "no clashes"

    testProperty "WHY - a session environment entry on the poisoned list is refused with its reason, and the rest are kept"
      (Prop.forAll (Arb.fromGen (Gen.zip (Gen.nonEmptyListOf genPoisonedName) (Gen.nonEmptyListOf genPlainName))) (fun (poisoned, plain) ->
        let plain = List.distinct plain
        let entries = (poisoned |> List.map (fun n -> n, "x")) @ (plain |> List.map (fun n -> n, "v"))
        let options, refusals = SessionOptions.ofConfig (configWith (fun c -> { c with SessionEnv = List.map EnvEntry entries }))
        let refusedNames = refusals |> List.choose (function SessionOptions.EnvNamePoisoned n -> Some n | _ -> None) |> List.distinct |> List.sort
        refusedNames = (List.distinct poisoned |> List.sort)
        && (options.Env |> List.map (fun (EnvEntry(name, _)) -> name) |> List.sort) = (List.sort plain)))

    testCase "WHY - a refusal says what it refused and what to do" <| fun _ ->
      let text = SessionOptions.describe (SessionOptions.EnvNamePoisoned "DOTNET_ROOT")
      text |> Expect.stringContains "names the variable" "DOTNET_ROOT"
      text |> Expect.stringContains "gives the next step" "→"

    testCase "WHY - an empty variable name and one with an equals sign are refused" <| fun _ ->
      let _, refusals = SessionOptions.ofConfig (configWith (fun c -> { c with SessionEnv = [ EnvEntry("", "x"); EnvEntry("A=B", "y") ] }))
      refusals |> Expect.equal "both refused" [ SessionOptions.EnvNameEmpty; SessionOptions.EnvNameInvalid "A=B" ]

    testCase "WHY - a user variable that clashes with one SageFs sets is reported and SageFs's value wins" <| fun _ ->
      let config = configWith (fun c -> { c with SessionEnv = [ EnvEntry("SAGEFS_PROJECT_OUTPUT", "/mine"); EnvEntry("OTHER", "1") ] })
      let env, clashes = hostEnv [ "SAGEFS_PROJECT_OUTPUT", "/p/bin" ] config
      env |> List.filter (fun (n, _) -> n = "SAGEFS_PROJECT_OUTPUT") |> Expect.equal "one entry, SageFs's" [ "SAGEFS_PROJECT_OUTPUT", "/p/bin" ]
      env |> List.contains ("OTHER", "1") |> Expect.isTrue "the other entry is kept"
      clashes |> Expect.equal "reported" [ { SessionOptions.Name = "SAGEFS_PROJECT_OUTPUT"; UserValue = "/mine"; SageFsValue = "/p/bin" } ]

    testCase "WHY - Server GC wins over a user's DOTNET_gcServer=0 and the clash is reported" <| fun _ ->
      let config = configWith (fun c -> { c with Gc = ServerGc; SessionEnv = [ EnvEntry("DOTNET_gcServer", "0") ] })
      let env, clashes = hostEnv [] config
      env |> List.filter (fun (n, _) -> n = "DOTNET_gcServer") |> Expect.equal "Server's value" [ "DOTNET_gcServer", "1" ]
      clashes |> List.map (fun c -> c.Name) |> Expect.equal "reported" [ "DOTNET_gcServer" ]

    testCase "WHY - DefaultArgs reach the arguments the FSI host is started with" <| fun _ ->
      let options, _ = SessionOptions.ofConfig (configWith (fun c -> { c with DefaultArgs = [ "--no-warn:1182" ] }))
      options.FsiArgs |> Expect.equal "carried" [ "--no-warn:1182" ]

    testCase "WHY - an option set for one directory does not reach another" <| fun _ ->
      let a, _ = SessionOptions.ofConfig (configWith (fun c -> { c with Gc = ServerGc; SessionEnv = [ EnvEntry("A", "1") ] }))
      let b, _ = SessionOptions.ofConfig DirectoryConfigDefaults.empty
      a.Gc |> Expect.equal "a is Server" ServerGc
      b |> Expect.equal "b is the default" SessionOptions.none

    testCase "WHY - every field a config file can set names who reads it, so a dead field fails the build" <| fun _ ->
      let fields =
        Reflection.FSharpType.GetRecordFields typeof<DirectoryConfig>
        |> Array.map (fun p -> p.Name)
        |> Array.sort
        |> Array.toList
      SessionOptions.fieldReaders |> List.map fst |> List.sort |> Expect.equal "table matches the record" fields

    testCase "WHY - a field the session host reads changes what the host gets, for each of them" <| fun _ ->
      let baseline = SessionOptions.ofConfig DirectoryConfigDefaults.empty |> fst
      let changes =
        [ "DefaultArgs", configWith (fun c -> { c with DefaultArgs = [ "--x" ] })
          "Gc", configWith (fun c -> { c with Gc = ServerGc })
          "SessionEnv", configWith (fun c -> { c with SessionEnv = [ EnvEntry("A", "1") ] })
          "Artifacts", configWith (fun c -> { c with Artifacts = ArtifactsIn "/tmp/art" }) ]
      let hostFields =
        SessionOptions.fieldReaders
        |> List.choose (fun (name, reader) -> match reader with SessionOptions.SessionHost -> Some name | _ -> None)
        |> List.sort
      hostFields |> Expect.equal "the host-read fields" (changes |> List.map fst |> List.sort)
      for name, config in changes do
        (SessionOptions.ofConfig config |> fst) <> baseline |> Expect.isTrue (sprintf "%s changes the options" name)

    testCase "WHY - an artifacts path must be absolute, and an empty one, a relative one or one under /proc is refused with its reason" <| fun _ ->
      let artifacts = Path.Combine(Path.GetTempPath(), "sagefs-art")
      let kept, noRefusals = SessionOptions.ofConfig (configWith (fun c -> { c with Artifacts = ArtifactsIn artifacts }))
      kept.Artifacts |> Expect.equal "kept" (ArtifactsIn artifacts)
      noRefusals |> Expect.isEmpty "nothing refused"
      for given, refusal in [ "  ", SessionOptions.ArtifactsPathEmpty
                              "out/art", SessionOptions.ArtifactsPathNotAbsolute "out/art"
                              "/proc/x", SessionOptions.ArtifactsPathUnderProc "/proc/x" ] do
        let dropped, refusals = SessionOptions.ofConfig (configWith (fun c -> { c with Artifacts = ArtifactsIn given }))
        dropped.Artifacts |> Expect.equal (sprintf "%s is dropped" given) ProjectChooses
        refusals |> Expect.equal (sprintf "%s is named" given) [ refusal ]
        SessionOptions.describe refusal |> Expect.stringContains "says the next step" "→"

    testProperty "WHY - the options a worker reads from its environment are the options the daemon holds, so the build, the loader and the worker cannot disagree"
      (Prop.forAll (Arb.fromGen genConfig) (fun config ->
        let options, _ = SessionOptions.ofConfig config
        let environment = SessionOptions.workerEnvironment options
        let lookup (name: string) = environment |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd |> Option.toObj
        List.length environment <= 1
        && SessionOptions.fromEnvironment lookup = Ok options
        && (SessionOptions.fromEnvironment lookup |> Result.map SessionOptions.buildProperties) = Ok(SessionOptions.buildProperties options)))

    testCase "WHY - no carrier is no options, and a garbled carrier is refused with a reason, not read as no options" <| fun _ ->
      SessionOptions.fromEnvironment (fun _ -> null) |> Expect.equal "absent" (Ok SessionOptions.none)
      SessionOptions.fromEnvironment (fun _ -> "") |> Expect.equal "empty" (Ok SessionOptions.none)
      SessionOptions.fromEnvironment (fun _ -> "{nope") |> Expect.isError "garbled"
      SessionOptions.workerEnvironment SessionOptions.none |> Expect.isEmpty "none sends nothing"

    testCase "WHY - the artifacts path becomes the MSBuild property the loader and the builds pass" <| fun _ ->
      let options, _ = SessionOptions.ofConfig (configWith (fun c -> { c with Artifacts = ArtifactsIn "/tmp/art" }))
      SessionOptions.buildProperties options |> Expect.equal "the property" [ "ArtifactsPath", "/tmp/art" ]
      SessionOptions.buildProperties SessionOptions.none |> Expect.isEmpty "none, no property"

    testCase "WHY - a directory's options are remembered by the daemon for its builds and its worker, and another directory does not see them" <| fun _ ->
      let a = Path.Combine(Path.GetTempPath(), "registry-a")
      let b = Path.Combine(Path.GetTempPath(), "registry-b")
      let options, _ = SessionOptions.ofConfig (configWith (fun c -> { c with Artifacts = ArtifactsIn "/tmp/art-a" }))
      SessionOptionsRegistry.remember a options
      SessionOptionsRegistry.optionsFor a |> Expect.equal "a has it" options
      SessionOptionsRegistry.optionsFor b |> Expect.equal "b does not" SessionOptions.none
      SessionOptionsRegistry.workerEnvironmentOf "reg-session-a" [] a |> Expect.equal "a's worker gets the one carrier" (SessionOptions.workerEnvironment options)
      SessionOptionsRegistry.workerEnvironmentOf "reg-session-b" [] b |> Expect.isEmpty "b's worker gets nothing"
      let psi = SessionBuild.buildStartInfo [ "build" ] a
      psi.ArgumentList |> Seq.toList |> Expect.contains "a's build builds under the artifacts path, as a global property" "-p:ArtifactsPath=/tmp/art-a"
      (SessionBuild.buildStartInfo [ "build" ] b).ArgumentList |> Seq.toList |> Expect.equal "b's build is the project's own" [ "build" ]
      SessionOptionsRegistry.release "reg-session-a"
      SessionOptionsRegistry.release "reg-session-b"
      SessionOptionsRegistry.remember a SessionOptions.none
      SessionOptionsRegistry.optionsFor a |> Expect.equal "a config that stops asking is forgotten" SessionOptions.none

    testCase "WHY - a worker's own build reads the carrier it was started with, so its rebuild goes where the daemon's build went" <| fun _ ->
      let options, _ = SessionOptions.ofConfig (configWith (fun c -> { c with Artifacts = ArtifactsIn "/tmp/art-worker" }))
      let carrier = SessionOptions.workerEnvironment options |> List.head
      let lookup (name: string) = if name = fst carrier then snd carrier else null
      let psi = SessionBuild.buildStartInfoWith lookup [ "build" ] (Path.Combine(Path.GetTempPath(), "registry-nobody-knows"))
      psi.ArgumentList |> Seq.toList |> Expect.contains "the worker's build builds under the artifacts path" "-p:ArtifactsPath=/tmp/art-worker"

    Integration.hostCaseTask "WHY - reading a directory's config returns it and writes nothing, and only preparing a session writes the options its builds and worker use" (fun () -> task {
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-pure-load-" + Guid.NewGuid().ToString "N")
      Directory.CreateDirectory(Path.Combine(dir, ".SageFs")) |> ignore
      try
        File.WriteAllText(Path.Combine(dir, ".SageFs", "config.fsx"), "{ DirectoryConfig.empty with Artifacts = ArtifactsIn \"/tmp/pure-art\" }")
        (DirectoryConfig.load dir |> Option.map (fun c -> c.Artifacts)) |> Expect.equal "load returns the config" (Some(ArtifactsIn "/tmp/pure-art"))
        SessionOptionsRegistry.optionsFor dir |> Expect.equal "load wrote nothing" SessionOptions.none
        let! loaded = DirectoryConfig.loadAsync dir |> Async.StartAsTask
        (loaded |> Option.map (fun c -> c.Artifacts)) |> Expect.equal "loadAsync returns the config" (Some(ArtifactsIn "/tmp/pure-art"))
        SessionOptionsRegistry.optionsFor dir |> Expect.equal "loadAsync wrote nothing" SessionOptions.none
        DirectoryConfig.prepareForCreate dir |> Expect.isTrue "auto-open is still on"
        (SessionOptionsRegistry.optionsFor dir).Artifacts |> Expect.equal "preparing the session wrote them" (ArtifactsIn "/tmp/pure-art")
      finally
        SessionOptionsRegistry.remember dir SessionOptions.none
        try Directory.Delete(dir, true) with :? IOException -> () })

    Integration.hostCase "WHY - a real build of a small project with the session's artifacts path writes its output there and leaves the project's bin and obj untouched" (fun () ->
      let project = Path.Combine(Path.GetTempPath(), "sagefs-artifacts-" + Guid.NewGuid().ToString "N")
      let artifacts = Path.Combine(project, "art")
      Directory.CreateDirectory project |> ignore
      try
        File.WriteAllText(Path.Combine(project, "Small.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Lib.fs\" /></ItemGroup></Project>")
        File.WriteAllText(Path.Combine(project, "Lib.fs"), "module Lib\nlet answer = 42\n")
        let options, _ = SessionOptions.ofConfig (configWith (fun c -> { c with Artifacts = ArtifactsIn artifacts }))
        SessionOptionsRegistry.remember project options
        let psi = SessionBuild.buildStartInfo [ "build"; "Small.fsproj"; "-nodeReuse:false" ] project
        ProcessEnvironment.applyTo psi []
        use proc = Diagnostics.Process.Start psi
        let output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        proc.ExitCode |> Expect.equal (sprintf "the build succeeds: %s" output) 0
        Directory.GetFiles(artifacts, "Small.dll", SearchOption.AllDirectories) |> Expect.isNonEmpty "the assembly is under the artifacts path"
        Directory.Exists(Path.Combine(project, "bin")) |> Expect.isFalse "the project has no bin"
        Directory.Exists(Path.Combine(project, "obj")) |> Expect.isFalse "the project has no obj"
        // The loader must look where the build wrote: it is given the same path as an MSBuild property.
        let carrier = SessionOptions.workerEnvironment options |> List.head
        TestInfrastructure.withEnvVar (fst carrier) (Some(snd carrier)) (fun () ->
          let loaded = ProjectLoading.loadSolution TestInfrastructure.quietLogger { Targets = [ SessionProjectTarget.Project(Path.Combine(project, "Small.fsproj")) ]; WorkingDir = project } (fun _ _ _ -> ())
          loaded.Projects |> List.map (fun p -> p.TargetPath.StartsWith(artifacts, StringComparison.Ordinal))
          |> Expect.equal "the loaded project's output is under the artifacts path" [ true ])
      finally
        SessionOptionsRegistry.remember project SessionOptions.none
        try Directory.Delete(project, true) with :? IOException -> ())
  ]
