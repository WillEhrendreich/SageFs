/// A cold tree is a project with no `obj/` and no `bin/`: a fresh clone, a
/// `git clean`, a brand new worktree. `SessionBuild.runBuildAsync` builds
/// `--no-restore` first and retries WITH a restore only when the failed output
/// matches `buildOutputNeedsRestore`. A cold tree whose failure is worded in a
/// way the classifier does not know is reported as a compile failure and the
/// session never comes up, although one restore would have fixed it.
///
/// The fixture strings below are REAL `dotnet build --no-restore` output from
/// a cold tree (SDK 11.0.100-rc.1), captured by running the build, with only
/// the temp directory swapped for `/src/App`. They are not invented.
///
/// What the capture showed:
///   - a plain cold project, with or without a PackageReference and with or
///     without central package management, fails with NETSDK1004. Already
///     handled.
///   - MSB4019 ("imported project ... was not found") appears only when
///     something imports a restore-generated file. Restore fixes it when the
///     missing file is `obj/<proj>.nuget.g.props` or hangs off
///     `$(NuGetPackageRoot)`, both of which restore creates or defines. It does
///     NOT fix an import of a file that is simply not there, so the bare code
///     MSB4019 is not a restore signal.
module SageFs.Tests.SessionBuildColdTreeTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// Cold tree, plain project (also the shape with a PackageReference, and the
/// shape with Directory.Packages.props: all three print exactly this).
let private netsdk1004 =
  "/home/will/.dotnet/sdk/11.0.100-rc.1.26425.128/Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets(266,5): error NETSDK1004: Assets file '/src/App/obj/project.assets.json' not found. Run a NuGet package restore to generate this file. [/src/App/App.fsproj]"

/// Cold tree, a Directory.Build.props that imports the restore-generated
/// `obj/App.fsproj.nuget.g.props` unconditionally.
let private msb4019RestoreGeneratedProps =
  "/src/App/Directory.Build.props(1,10): error MSB4019: The imported project \"/src/App/obj/App.fsproj.nuget.g.props\" was not found. Confirm that the expression in the Import declaration \"obj/App.fsproj.nuget.g.props\", which evaluated to \"obj/App.fsproj.nuget.g.props\", is correct, and that the file exists on disk. [/src/App/App.fsproj]"

/// Cold tree, a project that imports a file under `$(NuGetPackageRoot)`, which
/// only restore defines: cold, the property is empty and the path collapses to
/// a relative one.
let private msb4019NuGetPackageRoot =
  "/src/App/App.fsproj(5,3): error MSB4019: The imported project \"/src/App/newtonsoft.json/13.0.3/lib/nothing.props\" was not found. Confirm that the expression in the Import declaration \"$(NuGetPackageRoot)newtonsoft.json/13.0.3/lib/nothing.props\", which evaluated to \"newtonsoft.json/13.0.3/lib/nothing.props\", is correct, and that the file exists on disk. [/src/App/App.fsproj]"

/// An import of a file that is just not there. Restore cannot create it.
let private msb4019UnrelatedMissingImport =
  "/src/App/App.fsproj(4,3): error MSB4019: The imported project \"/src/App/shared/typo.props\" was not found. Confirm that the expression in the Import declaration \"shared/typo.props\", which evaluated to \"shared/typo.props\", is correct, and that the file exists on disk. [/src/App/App.fsproj]"

/// A real F# type error from a project that was restored.
let private fs0001 =
  "/src/App/Program.fs(1,15): error FS0001: This expression was expected to have type    'int'    but here has type    'string' [/src/App/App.fsproj]"

[<Tests>]
let classifierTests =
  testList "SessionBuild.buildOutputNeedsRestore on a cold tree" [

    testCase "WHY — a plain cold tree (NETSDK1004) needs a restore, because every cold project prints this and the retry is what makes a fresh clone build" <| fun _ ->
      SessionBuild.buildOutputNeedsRestore [ netsdk1004 ]
      |> Expect.isTrue "the missing assets file is a restore problem"

    testCase "WHY — MSB4019 on the restore-generated obj/*.nuget.g.props needs a restore, because the file exists only after restore and reporting it as a build failure leaves a fresh clone with a session that never starts" <| fun _ ->
      SessionBuild.buildOutputNeedsRestore [ "Build FAILED."; msb4019RestoreGeneratedProps ]
      |> Expect.isTrue "an import of the restore-generated props file is a restore problem"

    testCase "WHY — MSB4019 on an import under $(NuGetPackageRoot) needs a restore, because only restore defines that property" <| fun _ ->
      SessionBuild.buildOutputNeedsRestore [ msb4019NuGetPackageRoot ]
      |> Expect.isTrue "an import through NuGetPackageRoot is a restore problem"

    testCase "WHY — MSB4019 on an import that has nothing to do with restore is NOT a restore problem, because restore cannot create a file the author never wrote and would only add a slow retry before the same failure" <| fun _ ->
      SessionBuild.buildOutputNeedsRestore [ msb4019UnrelatedMissingImport ]
      |> Expect.isFalse "a bare MSB4019 is not enough to trigger a restore"

    testCase "WHY — a real F# compile error is NOT a restore problem, because a genuine build failure must be reported as-is and never turn into a restore loop" <| fun _ ->
      SessionBuild.buildOutputNeedsRestore [ fs0001; "Build FAILED." ]
      |> Expect.isFalse "FS0001 must be reported, not retried"
  ]

/// Real builds in a scratch directory: they run `dotnet` and need the NuGet
/// cache for the restore, so they run in the host tier with the other real-build tests, not in the
/// default suite.
[<Tests>]
let coldBuildTests =
  let tfm = sprintf "net%d.0" Environment.Version.Major
  let writeProject (dir: string) =
    File.WriteAllText(
      Path.Combine(dir, "App.fsproj"),
      sprintf """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>%s</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Program.fs" /></ItemGroup>
</Project>""" tfm)
    File.WriteAllText(Path.Combine(dir, "Program.fs"), "printfn \"hi\"\n")
  let inScratchDir (body: string -> Async<unit>) =
    async {
      let dir = Directory.CreateTempSubdirectory("sagefs-coldtree-").FullName
      try
        Directory.Exists(Path.Combine(dir, "obj")) |> Expect.isFalse "the fixture starts cold: no obj"
        Directory.Exists(Path.Combine(dir, "bin")) |> Expect.isFalse "the fixture starts cold: no bin"
        return! body dir
      finally
        try Directory.Delete(dir, true) with _ -> ()
    }
  Integration.hostList "SessionBuild.runBuildAsync on a cold tree (real build)" [

    testAsync "WHY — a cold tree builds through runBuildAsync, because the NETSDK1004 retry is the only thing between a fresh clone and a session that never starts" {
      do! inScratchDir (fun dir ->
        async {
          writeProject dir
          let! result = SessionBuild.runBuildAsync [ "App.fsproj" ] dir
          match result with
          | Ok _ -> ()
          | Error err -> failtestf "a cold tree must build after a restore, got %s" (SageFsError.describe err)
        })
    }

    testAsync "WHY — a cold tree whose Directory.Build.props imports the restore-generated props builds through runBuildAsync, because that MSB4019 is fixed by restore and was reported as a compile failure instead" {
      do! inScratchDir (fun dir ->
        async {
          writeProject dir
          File.WriteAllText(
            Path.Combine(dir, "Directory.Build.props"),
            "<Project><Import Project=\"obj/App.fsproj.nuget.g.props\" /></Project>")
          let! result = SessionBuild.runBuildAsync [ "App.fsproj" ] dir
          match result with
          | Ok _ -> ()
          | Error err -> failtestf "the restore-generated import must be healed by the restore retry, got %s" (SageFsError.describe err)
        })
    }

    testAsync "WHY — a cold tree with a real compile error ends in a named BuildFailed, never an exception, and is not retried into a restore loop" {
      do! inScratchDir (fun dir ->
        async {
          writeProject dir
          File.WriteAllText(Path.Combine(dir, "Program.fs"), "let x : int = \"not an int\"\n")
          let! result = SessionBuild.runBuildAsync [ "App.fsproj" ] dir
          match result with
          | Error (SageFsError.BuildFailed (_, diagnostics)) ->
            diagnostics
            |> List.exists (fun d -> d.Code = Some "FS0001")
            |> Expect.isTrue "the F# error is what the user sees, not a restore complaint"
          | Ok _ -> failtest "a type error cannot build"
          | Error other -> failtestf "expected BuildFailed, got %s" (SageFsError.describe other)
        })
    }
  ]
