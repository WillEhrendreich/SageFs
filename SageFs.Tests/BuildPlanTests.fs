/// The stages before the test tiers: what the gate builds, in what order, and what it compiles twice (it must not).
/// The rules are build/TierPlan.fs (and build/GateProducts.proj); ci-pipeline.fsx only runs them.
module SageFs.Tests.BuildPlanTests

open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open Expecto
open Expecto.Flip
open SageFs.Build.TierPlan

let repoRoot =RepoPaths.repoPathFull [||]
let read(relative: string) = File.ReadAllText(Path.Combine(repoRoot, relative))

/// The `.fsproj` names a project's `<ProjectReference Include="..\X\X.fsproj" />` entries point at.
let referencedProjects(fsproj: string) : string list =
  XDocument.Parse(read fsproj).Descendants(XName.Get "ProjectReference")
  |> Seq.map (fun e -> e.Attribute(XName.Get "Include").Value.Replace('\\', '/'))
  |> Seq.map (fun path -> Path.GetFileNameWithoutExtension path)
  |> List.ofSeq

let planTests =
  testList "Build plan (pure)" [
    testCase "the other framework's test build compiles only the test assembly" <| fun _ ->
      match testBuildCommands Net10 with
      | [ restore; build ] ->
        build |> Expect.stringContains "no project reference is rebuilt: the shipped closure was already built for this framework" "--no-dependencies"
        restore.Contains "--no-dependencies" |> Expect.isFalse "a restore has no dependencies flag"
      | other -> failtestf "expected a restore then a build, got %A" other

    testCase "the seed puts every product's primary-build outputs in the private obj tree the isolated build reads" <| fun _ ->
      let script = seedScript Net10
      for project in productProjects do
        script |> Expect.stringContains (sprintf "%s: reference assemblies come from the primary build" project) (sprintf "%s/obj/Release/net10.0" project)
        script |> Expect.stringContains (sprintf "%s: and land where BaseIntermediateOutputPath points" project) (sprintf "%s/obj/tier-net10.0/Release/net10.0" project)

    testCase "the seed fails the step when a product was not built, and replaces whatever copy was there" <| fun _ ->
      let script = seedScript Net10
      script |> Expect.stringContains "a missing source is an error, never an empty copy" "test -d SageFs.Core/obj/Release/net10.0 &&"
      script |> Expect.stringContains "a stale private copy never survives" "rm -rf SageFs.Core/obj/tier-net10.0/Release/net10.0"
      script.Contains ";" |> Expect.isFalse "every command is chained with &&, so the first failure ends the script"

    testCase "the seed's target is inside the obj directory the restore and build were told to use" <| fun _ ->
      let restore = testBuildCommands Net10 |> List.head
      let privateObj = Regex.Match(restore, @"-p:BaseIntermediateOutputPath=(\S+)").Groups[1].Value
      privateObj |> Expect.equal "the private obj directory" "obj/tier-net10.0/"
      for project in productProjects do
        seedOf Net10 project |> snd |> Expect.stringStarts (sprintf "%s: seeded under the private obj" project) (sprintf "%s/%s" project privateObj)
  ]

let wiringTests =
  testList "Build plan (reads the tree)" [
    testCase "the products the test assembly compiles against are exactly the projects it references" <| fun _ ->
      let referenced =
        referencedProjects "SageFs.Tests/SageFs.Tests.fsproj"
        |> List.filter (fun name -> not (name.EndsWith "Fixture"))
        |> List.sort
      productProjects |> List.sort |> Expect.equal "a project the tests reference is seeded, and nothing else is" referenced

    testCase "the products project builds every product, so the isolated build has all of them to compile against" <| fun _ ->
      let proj = XDocument.Parse(read "build/GateProducts.proj")
      let direct =
        proj.Descendants(XName.Get "Product")
        |> Seq.map (fun e -> Path.GetFileNameWithoutExtension(e.Attribute(XName.Get "Include").Value))
        |> Set.ofSeq
      // Core is not listed: every other product references it, so the same build makes it.
      let viaReference =
        direct
        |> Set.filter (fun name -> referencedProjects (sprintf "%s/%s.fsproj" name name) |> List.contains "SageFs.Core")
      Set.ofList productProjects
      |> Set.remove "SageFs.Core"
      |> Expect.equal "every product but Core is built directly" direct
      viaReference |> Expect.equal "and each of those pulls Core in" direct

    testCase "the build stage builds the products before it starts the other framework's test build" <| fun _ ->
      let pipeline = read "ci-pipeline.fsx"
      let products = pipeline.IndexOf "dotnet build build/GateProducts.proj"
      let started = pipeline.IndexOf "startBackgroundOnce \"net10-tests\""
      (products > 0) |> Expect.isTrue "the pipeline builds the products project"
      (started > products) |> Expect.isTrue "and starts the isolated build only after that step"

    testCase "WHY — the net10 recipe in AGENTS.md is the commands the build plan produces, in the order the pipeline runs them, so the prose cannot drift from TierPlan" <| fun _ ->
      let lines = (read "AGENTS.md").Replace("\r\n", "\n").Split('\n')
      let anchor =
        match lines |> Array.tryFindIndex (fun line -> line.StartsWith "The default suite runs on every framework") with
        | Some index -> index
        | None -> failtest "AGENTS.md lost the paragraph that gives the net10 recipe"
      let fenceOpen =
        match lines |> Array.indexed |> Array.skip anchor |> Array.tryFind (fun (_, line) -> line.StartsWith "```") with
        | Some (index, _) -> index
        | None -> failtest "the net10 paragraph in AGENTS.md has no command block"
      let documented =
        lines
        |> Array.skip (fenceOpen + 1)
        |> Array.takeWhile (fun line -> not (line.StartsWith "```"))
        |> Array.filter (fun line -> line.Trim() <> "")
        |> List.ofArray
      let restore, build =
        match testBuildCommands Net10 with
        | [ restore; build ] -> restore, build
        | other -> failtestf "expected a restore then a build, got %A" other
      let primaryClosureBuild = "dotnet build build/GateProducts.proj -c Release"
      read "ci-pipeline.fsx" |> Expect.stringContains "the pipeline builds the closure with the command the recipe starts from" primaryClosureBuild
      documented
      |> Expect.equal
           "the closure build for both frameworks, then the plan's restore, the seed, the plan's build, then the net10 test assembly"
           [ primaryClosureBuild
             restore
             sprintf "sh -c '%s'" (seedScript Net10)
             build
             sprintf "dotnet %s --summary" (dllOf Net10) ]
  ]

[<Tests>]
let tests = testList "Build plan suite" [ planTests; wiringTests ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant wiringTests |> ignore
