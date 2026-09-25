module SageFs.Tests.BuildPreflightTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs

let private projectFile = "/repo/App.fsproj"
let private conventional =
  [ "/repo/obj/project.assets.json"
    "/repo/obj/App.fsproj.nuget.g.props" ]

let private arcade =
  [ "/repo/artifacts/obj/App/project.assets.json"
    "/repo/artifacts/obj/App/App.fsproj.nuget.g.props" ]

let private check files target =
  let present = Set.ofList files
  BuildPreflight.check (present.Contains) (fun _ -> "<Project />") target

[<Tests>]
let tests =
  testList "BuildPreflight" [
    test "bare is ready without filesystem reads" {
      let mutable reads = 0
      let result =
        BuildPreflight.check
          (fun _ -> reads <- reads + 1; true)
          (fun _ -> reads <- reads + 1; "<Project />")
          SessionProjectTarget.Bare
      result |> Expect.equal "bare" BuildPreflight.Ready
      reads |> Expect.equal "bare preflight does not inspect the filesystem" 0
    }

    test "missing project is NeedsRebuild with the exact path" {
      let target = SessionProjectTarget.Project projectFile
      let result = check [] target
      match result with
      | BuildPreflight.NeedsRebuild missing -> missing |> Expect.equal "missing project" [ projectFile ]
      | other -> failtestf "expected NeedsRebuild, got %A" other
    }

    testPropertyWithConfig FsCheckConfig.defaultConfig "conventional generated inputs classify exactly" <|
      fun (NonNegativeInt missingCount) ->
        let missingCount = min missingCount conventional.Length
        let missing = conventional |> List.truncate missingCount
        let present = projectFile :: (conventional |> List.filter (fun path -> not (List.contains path missing)))
        let result = check present (SessionProjectTarget.Project projectFile)
        let required =
          if conventional |> List.exists (fun path -> List.contains path present) || arcade |> List.exists (fun path -> List.contains path present) then
            if conventional |> List.exists (fun path -> List.contains path present) then conventional else arcade
          else
            conventional
        let expectedMissing = required |> List.filter (fun path -> not (List.contains path present))
        match expectedMissing with
        | [] -> result |> Expect.equal "all conventional inputs present" BuildPreflight.Ready
        | expectedMissing ->
          match result with
          | BuildPreflight.NeedsRebuild missingPaths ->
            missingPaths |> List.sort |> Expect.equal "missing generated paths follow the selected layout" (expectedMissing |> List.sort)
          | other -> failtestf "expected NeedsRebuild, got %A" other

    testPropertyWithConfig FsCheckConfig.defaultConfig "Arcade generated inputs classify exactly" <|
      fun (NonNegativeInt missingCount) ->
        let missingCount = min missingCount arcade.Length
        let missing = arcade |> List.truncate missingCount
        let present = projectFile :: (arcade |> List.filter (fun path -> not (List.contains path missing)))
        let result = check present (SessionProjectTarget.Project projectFile)
        let required =
          if conventional |> List.exists (fun path -> List.contains path present) || arcade |> List.exists (fun path -> List.contains path present) then
            if conventional |> List.exists (fun path -> List.contains path present) then conventional else arcade
          else
            conventional
        let expectedMissing = required |> List.filter (fun path -> not (List.contains path present))
        match expectedMissing with
        | [] -> result |> Expect.equal "all Arcade inputs present" BuildPreflight.Ready
        | expectedMissing ->
          match result with
          | BuildPreflight.NeedsRebuild missingPaths ->
            missingPaths |> List.sort |> Expect.equal "missing Arcade paths follow the selected layout" (expectedMissing |> List.sort)
          | other -> failtestf "expected NeedsRebuild, got %A" other

    test "all conventional generated inputs present is Ready" {
      check (projectFile :: conventional) (SessionProjectTarget.Project projectFile)
      |> Expect.equal "conventional generated state present" BuildPreflight.Ready
    }

    test "all Arcade generated inputs present is Ready" {
      check (projectFile :: arcade) (SessionProjectTarget.Project projectFile)
      |> Expect.equal "Arcade generated state present" BuildPreflight.Ready
    }

    test "custom OutputPath is Unknown, not NeedsRebuild" {
      let present = Set.ofList (projectFile :: conventional)
      let result =
        BuildPreflight.check present.Contains (fun _ -> "<Project><PropertyGroup><OutputPath>custom</OutputPath></PropertyGroup></Project>") (SessionProjectTarget.Project projectFile)
      result |> Expect.equal "custom layout remains unknown" (BuildPreflight.Unknown "the project declares a custom OutputPath")
    }

    test "solution target is Unknown, not falsely Ready" {
      BuildPreflight.check (fun _ -> true) (fun _ -> "<Project />") (SessionProjectTarget.Solution "/repo/App.slnx")
      |> Expect.equal "solution preflight boundary" (BuildPreflight.Unknown "solution preflight is not classified yet")
    }
  ]
