/// SageFs keeps a warmup cache in `<project>/.SageFs/warmup-replay-cache.json`. It is
/// machine-generated and full of absolute paths to this machine's NuGet cache, in a
/// folder that also holds files the user writes and commits (`config.fsx`,
/// `init.fsx`). A new user saw an unexplained untracked file in their own project in
/// a trial and could easily have committed it. The cache writes its own .gitignore
/// next to it, for the generated file only, and never disturbs one already there.
module SageFs.Tests.WarmupCacheGitIgnoreTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.WarmUp
open SageFs.WarmupReplayCache

let private withProjectDir run =
  let dir = Path.Combine(Path.GetTempPath(), $"sagefs-cache-gitignore-{Guid.NewGuid():N}")
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally
    if Directory.Exists dir then Directory.Delete(dir, true)

let private cacheFileName = "warmup-replay-cache.json"

let private emptyPlan =
  createPlan (buildFingerprint true [||] [] [] [] []) 0 [] [] [] []

/// Save a plan the way the daemon does, under `<dir>/.SageFs/`.
let private saveUnder (dir: string) : string =
  let sageFsDir = Path.Combine(dir, ".SageFs")
  save (Path.Combine(sageFsDir, cacheFileName)) emptyPlan
  sageFsDir

let private ignoredLines (path: string) : string list =
  File.ReadAllLines path |> Array.map (fun line -> line.Trim()) |> Array.toList

[<Tests>]
let tests =
  testList "The warmup cache and git" [
    testCase "WHY — saving the cache ignores it, because it holds this machine's absolute paths and a new user's first git status showed it untracked" <| fun _ ->
      withProjectDir <| fun dir ->
        let sageFsDir = saveUnder dir
        let ignore' = Path.Combine(sageFsDir, ".gitignore")
        File.Exists ignore' |> Expect.isTrue "a .gitignore sits beside the cache"
        ignoredLines ignore' |> Expect.contains "the generated cache is ignored" cacheFileName

    testCase "WHY — only the generated file is ignored, so the config.fsx and init.fsx a user writes stay committable" <| fun _ ->
      withProjectDir <| fun dir ->
        let sageFsDir = saveUnder dir
        let lines = ignoredLines (Path.Combine(sageFsDir, ".gitignore"))
        List.contains "*" lines |> Expect.isFalse "nothing ignores everything"
        List.contains "config.fsx" lines |> Expect.isFalse "config.fsx is not ignored"
        List.contains "init.fsx" lines |> Expect.isFalse "init.fsx is not ignored"

    testCase "WHY — saving twice writes the entry once, because the cache is saved on every warmup" <| fun _ ->
      withProjectDir <| fun dir ->
        let sageFsDir = saveUnder dir
        saveUnder dir |> ignore
        ignoredLines (Path.Combine(sageFsDir, ".gitignore"))
        |> List.filter (fun line -> line = cacheFileName)
        |> Expect.hasLength "one entry" 1

    testCase "WHY — a .gitignore the user already has keeps its own lines and gains the entry, because it is their file" <| fun _ ->
      withProjectDir <| fun dir ->
        let sageFsDir = Path.Combine(dir, ".SageFs")
        Directory.CreateDirectory sageFsDir |> ignore
        let ignore' = Path.Combine(sageFsDir, ".gitignore")
        File.WriteAllText(ignore', "local-notes.txt\n")
        saveUnder dir |> ignore
        let lines = ignoredLines ignore'
        lines |> Expect.contains "their line survives" "local-notes.txt"
        lines |> Expect.contains "and the cache is ignored" cacheFileName

    testCase "WHY — a .gitignore that already ignores the cache is left byte for byte alone" <| fun _ ->
      withProjectDir <| fun dir ->
        let sageFsDir = Path.Combine(dir, ".SageFs")
        Directory.CreateDirectory sageFsDir |> ignore
        let ignore' = Path.Combine(sageFsDir, ".gitignore")
        let original = $"# mine\n{cacheFileName}\n"
        File.WriteAllText(ignore', original)
        saveUnder dir |> ignore
        File.ReadAllText ignore' |> Expect.equal "unchanged" original
  ]
