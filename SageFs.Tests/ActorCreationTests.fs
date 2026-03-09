module SageFs.Tests.ActorCreationTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.ProjectLoading
open SageFs.ActorCreation
open Ionide.ProjInfo.Types

let mkProject (fileName: string) : ProjectOptions =
  { ProjectId = None
    ProjectFileName = fileName
    TargetFramework = "net10.0"
    SourceFiles = []
    OtherOptions = []
    ReferencedProjects = []
    PackageReferences = []
    LoadTime = DateTime.UtcNow
    TargetPath = ""
    TargetRefPath = None
    ProjectOutputType = ProjectOutputType.Library
    ProjectSdkInfo =
      { IsTestProject = false
        Configuration = "Debug"
        IsPackable = false
        TargetFramework = "net10.0"
        TargetFrameworkIdentifier = ".NETCoreApp"
        TargetFrameworkVersion = "v10.0"
        MSBuildAllProjects = []
        MSBuildToolsVersion = ""
        ProjectAssetsFile = ""
        RestoreSuccess = true
        Configurations = []
        TargetFrameworks = []
        RunArguments = None
        RunCommand = None
        IsPublishable = None }
    Items = []
    Properties = []
    CustomProperties = []
    AllProperties = Map.empty
    AllItems = Map.empty
    Analyzers = [] }

[<Tests>]
let tests =
  testList "ActorCreation" [
    testList "projectDirectories" [
      test "empty solution returns empty list" {
        let result = projectDirectories emptySolution
        result |> Expect.isEmpty "should return empty list for empty solution"
      }

      test "deduplicates same directory from multiple projects" {
        let sln =
          { emptySolution with
              Projects = [
                mkProject (Path.Combine("src", "MyLib", "A.fsproj"))
                mkProject (Path.Combine("src", "MyLib", "B.fsproj"))
              ] }
        let result = projectDirectories sln
        result |> Expect.hasLength "should deduplicate to one directory" 1
      }

      test "skips projects with empty ProjectFileName" {
        let sln =
          { emptySolution with
              Projects = [
                mkProject ""
                mkProject (Path.Combine("src", "MyLib", "A.fsproj"))
              ] }
        let result = projectDirectories sln
        result |> Expect.hasLength "should skip empty and keep one" 1
      }
    ]

    testList "mkCommonActorArgs" [
      let logger = TestInfrastructure.quietLogger
      let onEvent (_: SageFs.Features.Events.SageFsEvent) = ()
      let loadConfig = SageFs.Args.ProjectLoadConfig.empty

      test "sets IsBare correctly" {
        let args = mkCommonActorArgs logger false onEvent loadConfig true
        args.IsBare |> Expect.isTrue "IsBare should be true when passed true"
      }

      test "sets AutoOpenNamespaces to true" {
        let args = mkCommonActorArgs logger false onEvent loadConfig false
        args.AutoOpenNamespaces |> Expect.isTrue "AutoOpenNamespaces should default to true"
      }

      test "sets HotReloadEnabled to false" {
        let args = mkCommonActorArgs logger false onEvent loadConfig false
        args.HotReloadEnabled |> Expect.isFalse "HotReloadEnabled should be false"
      }
    ]

    test "commonMiddleware is non-empty" {
      commonMiddleware |> Expect.isNonEmpty "commonMiddleware should contain middleware entries"
    }
  ]
