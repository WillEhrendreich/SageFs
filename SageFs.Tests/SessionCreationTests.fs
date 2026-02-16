module SageFs.Tests.SessionCreationTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server.Dashboard

/// Helper: write text to a file with explicit types.
let private writeText (path: string) (content: string) =
  File.WriteAllText(path, content)

/// Create a temp directory, run setup + test, then clean up.
let private withTempDir (setup: string -> unit) (test: string -> unit) =
  let dir =
    Path.Combine(
      Path.GetTempPath(),
      sprintf "sagefs-test-%s" (Guid.NewGuid().ToString("N").[..7]))
  try
    Directory.CreateDirectory(dir) |> ignore
    setup dir
    test dir
  finally
    if Directory.Exists dir then
      Directory.Delete(dir, true)

let private addFakeProject dir name =
  writeText (Path.Combine(dir, name)) "<Project />"

let private addConfig dir content =
  let configDir = Path.Combine(dir, ".SageFs")
  Directory.CreateDirectory(configDir) |> ignore
  writeText (Path.Combine(configDir, "config.fsx")) content

let private addSolution dir name =
  writeText (Path.Combine(dir, name)) ""

[<Tests>]
let tests = testList "Session Creation" [

  testList "resolveSessionProjects" [

    testCase "respects autoLoad = false" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addConfig dir "let autoLoad = false")
        (fun dir ->
          resolveSessionProjects dir ""
          |> Expect.isEmpty "should return no projects when autoLoad = false")

    testCase "auto-discovers when autoLoad = true" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addConfig dir "let autoLoad = true")
        (fun dir ->
          resolveSessionProjects dir ""
          |> Expect.isNonEmpty "should auto-discover projects when autoLoad = true")

    testCase "auto-discovers when no config exists" <| fun _ ->
      withTempDir
        (fun dir -> addFakeProject dir "Fake.fsproj")
        (fun dir ->
          resolveSessionProjects dir ""
          |> Expect.isNonEmpty "should auto-discover when no config file")

    testCase "uses config Projects over auto-discovery" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addFakeProject dir "Other.fsproj"
          addConfig dir """let projects = ["Other.fsproj"]""")
        (fun dir ->
          let result = resolveSessionProjects dir ""
          result |> Expect.hasLength "should use config Projects" 1
          result.[0]
          |> Expect.stringContains "should be config project" "Other.fsproj")

    testCase "returns empty for empty directory" <| fun _ ->
      withTempDir
        (fun _ -> ())
        (fun dir ->
          resolveSessionProjects dir ""
          |> Expect.isEmpty "should return empty for empty directory")

    testCase "prefers manual over config" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addFakeProject dir "Manual.fsproj"
          addConfig dir """let projects = ["Fake.fsproj"]""")
        (fun dir ->
          let result = resolveSessionProjects dir "Manual.fsproj"
          result |> Expect.hasLength "should use manual project" 1
          result.[0]
          |> Expect.stringContains "should be manual project" "Manual.fsproj")

    testCase "prefers solution over project" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addSolution dir "Fake.sln")
        (fun dir ->
          let result = resolveSessionProjects dir ""
          result |> Expect.hasLength "should find one solution" 1
          result.[0]
          |> Expect.stringContains "should prefer solution" "Fake.sln")

    testCase "config projects still used with autoLoad = false" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addConfig dir "let autoLoad = false\nlet projects = [\"Fake.fsproj\"]")
        (fun dir ->
          resolveSessionProjects dir ""
          |> Expect.isNonEmpty
            "config-specified projects should still be used with autoLoad = false")
  ]

  testList "DirectoryConfig.parse" [

    testCase "reads autoLoad = false" <| fun _ ->
      DirectoryConfig.parse "let autoLoad = false"
      |> fun c -> c.AutoLoad
      |> Expect.isFalse "should parse autoLoad = false"

    testCase "defaults autoLoad = true" <| fun _ ->
      DirectoryConfig.parse ""
      |> fun c -> c.AutoLoad
      |> Expect.isTrue "should default autoLoad to true"
  ]
]
