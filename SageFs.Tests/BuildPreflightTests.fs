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

let private outputDll = "/repo/bin/Debug/net11.0/App.dll"

/// The fake-filesystem `findOutputs`: every present path under the output root
/// whose file name is the DLL the preflight is looking for.
let private outputsIn (present: Set<string>) (dir: string) (pattern: string) : string list =
  let prefix = dir + string System.IO.Path.DirectorySeparatorChar
  present
  |> Set.filter (fun p -> p.StartsWith(prefix, System.StringComparison.Ordinal) && System.IO.Path.GetFileName p = pattern)
  |> Set.toList

/// A built project: restore inputs AND an output DLL. `Ready` now requires
/// both, so a fixture that represents a build must carry an output.
let private check files target =
  let present = Set.ofList (outputDll :: files)
  BuildPreflight.check present.Contains (fun _ -> "<Project />") (outputsIn present) target

[<Tests>]
let tests =
  testList "BuildPreflight" [
    test "bare is ready without filesystem reads" {
      let mutable reads = 0
      let result =
        BuildPreflight.check
          (fun _ -> reads <- reads + 1; true)
          (fun _ -> reads <- reads + 1; "<Project />")
          (fun _ _ -> reads <- reads + 1; [])
          SessionProjectTarget.Bare
      result |> Expect.equal "bare" BuildPreflight.Ready
      reads |> Expect.equal "bare preflight does not inspect the filesystem" 0
    }

    // WHY — restore artifacts (obj/project.assets.json) survive a `bin` clear.
    // A preflight that only checks those says Ready for a project with no
    // output DLLs, so create_session skips its recovery build and warmup
    // faults with "Not all DLLs are found". The output check is the one that
    // decides whether the project can actually be loaded.
    test "restore inputs present but output DLLs cleared is NeedsRebuild, not Ready" {
      let target = SessionProjectTarget.Project projectFile
      let present = Set.ofList (projectFile :: conventional)
      let result = BuildPreflight.check present.Contains (fun _ -> "<Project />") (outputsIn present) (SessionProjectTarget.Project projectFile)
      match result with
      | BuildPreflight.NeedsRebuild missing ->
        missing |> Expect.exists "names the missing output" (fun p -> p.Contains "App.dll")
      | other -> failtestf "expected NeedsRebuild with the missing output, got %A" other
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
        BuildPreflight.check present.Contains (fun _ -> "<Project><PropertyGroup><OutputPath>custom</OutputPath></PropertyGroup></Project>") (outputsIn present) (SessionProjectTarget.Project projectFile)
      result |> Expect.equal "custom layout remains unknown" (BuildPreflight.Unknown "the project declares a custom OutputPath")
    }

    test "solution target is Unknown, not falsely Ready" {
      BuildPreflight.check (fun _ -> true) (fun _ -> "<Project />") (fun _ _ -> []) (SessionProjectTarget.Solution "/repo/App.slnx")
      |> Expect.equal "solution preflight boundary" (BuildPreflight.Unknown "solution preflight is not classified yet")
    }
  ]
