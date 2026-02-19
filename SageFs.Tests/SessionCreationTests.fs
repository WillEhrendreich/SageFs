module SageFs.Tests.SessionCreationTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server.Dashboard

/// Helper: write text to a file with explicit types.
let writeText (path: string) (content: string) =
  File.WriteAllText(path, content)

/// Create a temp directory, run setup + test, then clean up.
let withTempDir (setup: string -> unit) (test: string -> unit) =
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

let addFakeProject dir name =
  writeText (Path.Combine(dir, name)) "<Project />"

let addConfig dir content =
  let configDir = Path.Combine(dir, ".SageFs")
  Directory.CreateDirectory(configDir) |> ignore
  writeText (Path.Combine(configDir, "config.fsx")) content

let addSolution dir name =
  writeText (Path.Combine(dir, name)) ""

[<Tests>]
let tests = testList "Session Creation" [

  testList "resolveSessionProjects" [

    testCase "respects NoLoad strategy" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addConfig dir """{ DirectoryConfig.empty with Load = NoLoad }""")
        (fun dir ->
          resolveSessionProjects dir ""
          |> Expect.isEmpty "should return no projects with NoLoad")

    testCase "auto-discovers with AutoDetect config" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addConfig dir """{ DirectoryConfig.empty with Load = AutoDetect }""")
        (fun dir ->
          resolveSessionProjects dir ""
          |> Expect.isNonEmpty "should auto-discover with AutoDetect")

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
          addConfig dir """{ DirectoryConfig.empty with Load = Projects ["Other.fsproj"] }""")
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
          addConfig dir """{ DirectoryConfig.empty with Load = Projects ["Fake.fsproj"] }""")
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

    testCase "config solution strategy returns solution path" <| fun _ ->
      withTempDir
        (fun dir ->
          addSolution dir "MyApp.sln"
          addConfig dir """{ DirectoryConfig.empty with Load = Solution "MyApp.sln" }""")
        (fun dir ->
          let result = resolveSessionProjects dir ""
          result |> Expect.hasLength "should find one solution" 1
          result.[0]
          |> Expect.stringContains "should use config solution" "MyApp.sln")
  ]

  testList "DirectoryConfig.evaluate" [

    testCase "evaluates NoLoad" <| fun _ ->
      DirectoryConfig.evaluate """{ DirectoryConfig.empty with Load = NoLoad }"""
      |> Result.map (fun c -> c.Load)
      |> Expect.equal "should evaluate NoLoad" (Ok NoLoad)

    testCase "evaluates AutoDetect (default)" <| fun _ ->
      DirectoryConfig.evaluate "DirectoryConfig.empty"
      |> Result.map (fun c -> c.Load)
      |> Expect.equal "should default to AutoDetect" (Ok AutoDetect)
  ]
]
