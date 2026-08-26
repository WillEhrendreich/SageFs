module SageFs.Tests.WarmupReplayCacheTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WarmUp
open SageFs.AppState
open SageFs.WarmupReplayCache

let private withTempDir run =
  let dir =
    Path.Combine(Path.GetTempPath(), $"sagefs-warmup-replay-{Guid.NewGuid():N}")

  Directory.CreateDirectory(dir) |> ignore

  try
    run dir
  finally
    if Directory.Exists dir then
      Directory.Delete(dir, true)

let private writeFile (dir: string) (relativePath: string) (contents: string) =
  let path = Path.Combine(dir, relativePath)
  let parent = Path.GetDirectoryName path

  if not (String.IsNullOrWhiteSpace parent) then
    Directory.CreateDirectory(parent) |> ignore

  File.WriteAllText(path, contents)
  File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes 1.0)
  path

let private overwriteFile (path: string) (contents: string) =
  File.WriteAllText(path, contents)
  File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes 2.0)

let private sampleAssembly (path: string) : LoadedAssembly = {
  Name = Path.GetFileNameWithoutExtension path
  Path = path
  NamespaceCount = 2
  ModuleCount = 1
}

let private makePlan fingerprint sourceFilesScanned assembliesLoaded namesToOpen =
  createPlan fingerprint sourceFilesScanned assembliesLoaded namesToOpen []

[<Tests>]
let warmupReplayCacheTests =
  testList "WarmupReplayCache" [
    testCase "fingerprint invalidates when startup files change" <| fun _ ->
      withTempDir <| fun dir ->
        let startupFile = writeFile dir "startup.fsx" "printfn \"start\""
        let sourceFile = writeFile dir "Domain.fs" "open System"
        let assemblyFile = writeFile dir "MyApp.dll" "assembly-v1"

        let before =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        overwriteFile startupFile "printfn \"changed\""

        let after =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        (before = after)
        |> Expect.isFalse "startup-file changes should invalidate the replay fingerprint"

    testCase "fingerprint invalidates when source files or assemblies change" <| fun _ ->
      withTempDir <| fun dir ->
        let startupFile = writeFile dir "startup.fsx" "printfn \"start\""
        let sourceFile = writeFile dir "Domain.fs" "open System"
        let assemblyFile = writeFile dir "MyApp.dll" "assembly-v1"

        let before =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        overwriteFile sourceFile "open System.IO"

        let afterSourceChange =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        (before = afterSourceChange)
        |> Expect.isFalse "source-file changes should invalidate the replay fingerprint"

        overwriteFile assemblyFile "assembly-v2"

        let afterAssemblyChange =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        (afterSourceChange = afterAssemblyChange)
        |> Expect.isFalse "assembly-file changes should invalidate the replay fingerprint"

    testCase "fingerprint changes when FSI args change" <| fun _ ->
      withTempDir <| fun dir ->
        let startupFile = writeFile dir "startup.fsx" "printfn \"start\""
        let sourceFile = writeFile dir "Domain.fs" "open System"
        let assemblyFile = writeFile dir "MyApp.dll" "assembly-v1"

        let before =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        let after =
          buildFingerprint
            true
            [| "fsi"; "--langversion:preview" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        (before = after)
        |> Expect.isFalse "changing FSI args should invalidate the replay fingerprint"

    testCase "replay plan round-trips ordered names with module flags intact" <| fun _ ->
      withTempDir <| fun dir ->
        let cachePath = Path.Combine(dir, "warmup-replay-cache.json")
        let startupFile = writeFile dir "startup.fsx" "printfn \"start\""
        let sourceFile = writeFile dir "Domain.fs" "open System"
        let assemblyFile = writeFile dir "MyApp.dll" "assembly-v1"

        let fingerprint =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        let expectedNames = [
          "System", OpenableKind.Namespace
          "MyApp.Domain", OpenableKind.Namespace
          "MyApp.Utils", OpenableKind.Module
        ]

        let plan =
          makePlan
            fingerprint
            1
            [ sampleAssembly assemblyFile ]
            expectedNames

        save cachePath plan

        let reloaded = tryLoad cachePath

        reloaded |> Expect.isSome "saved replay plan should deserialize again"

        match reloaded with
        | Some loaded ->
          namePairs loaded
          |> Expect.equal "ordered names should survive JSON round-tripping" expectedNames
        | None ->
          failtest "expected replay plan to round-trip"

    testCase "replay plan excludes post-boundary warmup data" <| fun _ ->
      withTempDir <| fun dir ->
        let cachePath = Path.Combine(dir, "warmup-replay-cache.json")
        let startupFile = writeFile dir "startup.fsx" "printfn \"start\""
        let sourceFile = writeFile dir "Domain.fs" "open System"
        let assemblyFile = writeFile dir "MyApp.dll" "assembly-v1"

        let fingerprint =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        makePlan fingerprint 1 [ sampleAssembly assemblyFile ] [ "System", OpenableKind.Namespace; "MyApp.Utils", OpenableKind.Module ]
        |> save cachePath

        let json = File.ReadAllText cachePath

        json.Contains("\"startedAt\"")
        |> Expect.isFalse "replay cache should not persist post-boundary timestamps"

        json.Contains("\"phaseTiming\"")
        |> Expect.isFalse "replay cache should not persist post-boundary timings"

        json.Contains("\"failedOpens\"")
        |> Expect.isFalse "replay cache should not persist previous warmup failures"

        json.Contains("\"durationMs\"")
        |> Expect.isFalse "replay cache should not persist per-open timings"

    testCase "resolveWarmupReplayPlan uses a valid cache hit without rediscovering" <| fun _ ->
      withTempDir <| fun dir ->
        let cachePath = Path.Combine(dir, "warmup-replay-cache.json")
        let startupFile = writeFile dir "startup.fsx" "printfn \"start\""
        let sourceFile = writeFile dir "Domain.fs" "open System"
        let assemblyFile = writeFile dir "MyApp.dll" "assembly-v1"

        let fingerprint =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        let cachedPlan =
          makePlan
            fingerprint
            1
            [ sampleAssembly assemblyFile ]
            [ "System", OpenableKind.Namespace ]

        save cachePath cachedPlan

        let discoverCalls = ResizeArray<string>()

        let resolved =
          resolveWarmupReplayPlan
            TestInfrastructure.quietLogger
            (Some cachePath)
            fingerprint
            (fun () ->
              discoverCalls.Add("discover")
              async.Return (makePlan fingerprint 9 [] [ "Should.Not.Run", OpenableKind.Namespace ]))
          |> Async.RunSynchronously

        discoverCalls
        |> Seq.toList
        |> Expect.isEmpty "valid cache hits should skip rediscovery"

        namePairs resolved
        |> Expect.equal "cache hits should replay the cached open order" [ "System", OpenableKind.Namespace ]

    testCase "resolveWarmupReplayPlan rediscovers and refreshes stale plans" <| fun _ ->
      withTempDir <| fun dir ->
        let cachePath = Path.Combine(dir, "warmup-replay-cache.json")
        let startupFile = writeFile dir "startup.fsx" "printfn \"start\""
        let sourceFile = writeFile dir "Domain.fs" "open System"
        let assemblyFile = writeFile dir "MyApp.dll" "assembly-v1"

        let staleFingerprint =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        makePlan staleFingerprint 1 [ sampleAssembly assemblyFile ] [ "System", OpenableKind.Namespace ]
        |> save cachePath

        overwriteFile sourceFile "open System.IO"

        let freshFingerprint =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            [ startupFile ]
            [ sourceFile ]
            [ assemblyFile ]

        let discoverCalls = ResizeArray<string>()

        let discoveredPlan =
          makePlan
            freshFingerprint
            1
            [ sampleAssembly assemblyFile ]
            [ "System.IO", OpenableKind.Namespace; "MyApp.Utils", OpenableKind.Module ]

        let resolved =
          resolveWarmupReplayPlan
            TestInfrastructure.quietLogger
            (Some cachePath)
            freshFingerprint
            (fun () ->
              discoverCalls.Add("discover")
              async.Return discoveredPlan)
          |> Async.RunSynchronously

        discoverCalls.Count
        |> Expect.equal "stale cache entries should trigger rediscovery once" 1

        namePairs resolved
        |> Expect.equal "rediscovery should return the fresh replay plan" [ "System.IO", OpenableKind.Namespace; "MyApp.Utils", OpenableKind.Module ]

        tryLoadValidPlan cachePath freshFingerprint
        |> Expect.isSome "stale cache entries should be replaced with the fresh replay plan"

    testCase "discovery warnings survive save/load round-trip" <| fun _ ->
      withTempDir <| fun dir ->
        let cachePath = Path.Combine(dir, "warmup-replay-cache.json")
        let fp =
          buildFingerprint
            true
            [| "fsi"; "--multiemit-" |]
            []
            []
            []

        let warnings = [ "Auto-open was enabled but no source files were found for this project." ]

        // Build a plan WITH warnings via createPlan directly (makePlan passes []).
        createPlan fp 0 [] [] warnings
        |> save cachePath

        match tryLoadValidPlan cachePath fp with
        | Some plan ->
          plan.DiscoveryWarnings
          |> Expect.equal "warnings should round-trip through the cache" warnings
        | None ->
          failtest "plan with warnings should load from cache"
  ]
