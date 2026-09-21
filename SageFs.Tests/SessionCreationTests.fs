module SageFs.Tests.SessionCreationTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Tests.TestInfrastructure
open SageFs.Server.Dashboard
open SageFs.Server.DashboardTypes

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

/// Unwrap the Ok list from resolveSessionProjects, failing the test on Error.
let okProjects (msg: string) (r: Result<string list, SageFsError>) : string list =
  match r with
  | Ok ps -> ps
  | Error e -> failtestf "%s: expected Ok, got Error %A" msg e

[<Tests>]
let tests = testSequenced <| testList "Session Creation" [

  testList "resolveSessionProjects" [

    // These four cases write a real `.SageFs/config.fsx` and call
    // `resolveSessionProjects dir ""` (empty manual input) — which, per
    // `DashboardTypes.fs:1237`, falls through to `DirectoryConfig.load dir`
    // whenever a config file exists, which evaluates it via
    // `ConfigHost.evaluate`: a REAL isolated FSI host, exactly like every
    // other DirectoryConfig-evaluating case in this file. They were the only
    // `resolveSessionProjects` cases not tagged/registered as `[Integration]`,
    // so they silently ran inside the "fast" default `--summary` suite —
    // found 2026-09-21 while auditing this file for Target 2 duplication:
    // "respects NoLoad strategy" alone measured 10.4s in a full `--summary`
    // run. `Integration.hostCase` moves just these four (the ones with a real
    // config file AND empty manual input); the rest of this list never
    // reaches `DirectoryConfig.load` at all (either no config is written, or
    // manual input is non-empty and takes the manual-path branch that never
    // calls `load`), so they stay ordinary fast `testCase`s.
    Integration.hostCase "respects NoLoad strategy" (fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addConfig dir """{ DirectoryConfig.empty with Load = NoLoad }""")
        (fun dir ->
          resolveSessionProjects dir ""
          |> Expect.equal "should return no projects with NoLoad" (Ok [])))

    Integration.hostCase "auto-discovers with AutoDetect config" (fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addConfig dir """{ DirectoryConfig.empty with Load = AutoDetect }""")
        (fun dir ->
          resolveSessionProjects dir ""
          |> okProjects "AutoDetect"
          |> Expect.isNonEmpty "should auto-discover with AutoDetect"))

    testCase "auto-discovers when no config exists" <| fun _ ->
      withTempDir
        (fun dir -> addFakeProject dir "Fake.fsproj")
        (fun dir ->
          resolveSessionProjects dir ""
          |> okProjects "no config"
          |> Expect.isNonEmpty "should auto-discover when no config file")

    Integration.hostCase "uses config Projects over auto-discovery" (fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addFakeProject dir "Other.fsproj"
          addConfig dir """{ DirectoryConfig.empty with Load = Projects ["Other.fsproj"] }""")
        (fun dir ->
          let result = resolveSessionProjects dir "" |> okProjects "config Projects"
          result |> Expect.hasLength "should use config Projects" 1
          result.[0]
          |> Expect.stringContains "should be config project" "Other.fsproj"))

    testCase "returns empty for empty directory" <| fun _ ->
      withTempDir
        (fun _ -> ())
        (fun dir ->
          resolveSessionProjects dir ""
          |> Expect.equal "should return empty for empty directory" (Ok []))

    testCase "prefers manual over config" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addFakeProject dir "Manual.fsproj"
          addConfig dir """{ DirectoryConfig.empty with Load = Projects ["Fake.fsproj"] }""")
        (fun dir ->
          let result = resolveSessionProjects dir "Manual.fsproj" |> okProjects "manual over config"
          result |> Expect.hasLength "should use manual project" 1
          result.[0]
          |> Expect.stringContains "should be manual project" "Manual.fsproj")

    testCase "prefers solution over project" <| fun _ ->
      withTempDir
        (fun dir ->
          addFakeProject dir "Fake.fsproj"
          addSolution dir "Fake.sln")
        (fun dir ->
          let result = resolveSessionProjects dir "" |> okProjects "solution over project"
          result |> Expect.hasLength "should find one solution" 1
          result.[0]
          |> Expect.stringContains "should prefer solution" "Fake.sln")

    Integration.hostCase "config solution strategy returns solution path" (fun _ ->
      withTempDir
        (fun dir ->
          addSolution dir "MyApp.sln"
          addConfig dir """{ DirectoryConfig.empty with Load = Solution "MyApp.sln" }""")
        (fun dir ->
          let result = resolveSessionProjects dir "" |> okProjects "config solution"
          result |> Expect.hasLength "should find one solution" 1
          result.[0]
          |> Expect.stringContains "should use config solution" "MyApp.sln"))

    testCase "REJECTS manual projects outside the working directory (not silently dropped)" <| fun _ ->
      withTempDir
        (fun dir -> addFakeProject dir "Inside.fsproj")
        (fun dir ->
          // A rooted project path elsewhere on disk must be REFUSED loudly — a
          // dashboard peer cannot point the daemon at arbitrary projects, and a
          // caller who names N projects must never get a session quietly missing
          // one (roast-8 §4: unify with validateSessionCreateRequest).
          let outside =
            Path.Combine(Path.GetTempPath(), sprintf "sagefs-outside-%s.fsproj" (Guid.NewGuid().ToString("N").[..7]))
          try
            writeText outside "<Project />"
            match resolveSessionProjects dir outside with
            | Error (SageFsError.UnsafeSessionPath(p, _)) ->
              p |> Expect.equal "error must name the escaping path" outside
            | other ->
              failtestf "escaping manual project must be rejected, got %A" other
          finally
            if File.Exists outside then File.Delete outside)

    testCase "REJECTS on the first escaping project even when a valid one is also named" <| fun _ ->
      withTempDir
        (fun dir -> addFakeProject dir "Inside.fsproj")
        (fun dir ->
          let inside = Path.Combine(dir, "Inside.fsproj")
          let outside =
            Path.Combine(Path.GetTempPath(), sprintf "sagefs-outside-%s.fsproj" (Guid.NewGuid().ToString("N").[..7]))
          try
            writeText outside "<Project />"
            match resolveSessionProjects dir (inside + "," + outside) with
            | Error (SageFsError.UnsafeSessionPath(p, _)) ->
              p |> Expect.equal "error names the escaping path, not the valid one" outside
            | other ->
              failtestf "a mix with one escaping project must be rejected, got %A" other
          finally
            if File.Exists outside then File.Delete outside)

    testCase "keeps rooted manual projects inside the working directory" <| fun _ ->
      withTempDir
        (fun dir -> addFakeProject dir "Inside.fsproj")
        (fun dir ->
          let rootedInside = Path.Combine(dir, "Inside.fsproj")
          let result = resolveSessionProjects dir rootedInside |> okProjects "rooted inside"
          result |> Expect.hasLength "rooted manual project inside the working dir must be kept" 1
          result.[0]
          |> Expect.stringContains "should be the inside project" "Inside.fsproj")
  ]

  // The "DirectoryConfig.evaluate" cases that used to live here (NoLoad,
  // AutoDetect-default, AutoOpenNamespaces override, AutoOpenNamespaces
  // default) were deleted 2026-09-21: each was the exact same script text
  // AND the exact same assertion as a case already in DirectoryConfigTests.fs
  // (`evaluateTests`, `:18-79`) — "evaluates NoLoad"/"loads NoLoad strategy",
  // "evaluates AutoOpenNamespaces override"/"loads autoOpenNamespaces
  // override" (byte-identical scripts), and both "(default)" cases were
  // already fully subsumed by "empty expression returns defaults" (same
  // script text `"DirectoryConfig.empty"`, which asserts `.Load = AutoDetect`
  // AND `.AutoOpenNamespaces = true` together). Each of those four cases
  // started its own real isolated FSI host (`ConfigHost.evaluate`,
  // `StartupTimeoutMs = 120_000`) to prove a claim DirectoryConfigTests.fs
  // already proves — see fsi-mechanism-extraction.md §2 R3. Nothing here was
  // unique; deleting them loses no coverage.

  // `DirectoryConfig.autoOpenNamespacesForDirectory` is unique to this file —
  // DirectoryConfigTests.fs never calls it — so both cases stay. Moved under
  // `Integration.hostList` here (2026-09-21): "reads false from config"
  // writes a config.fsx and calls `load`, which calls `ConfigHost.evaluate`
  // and so starts a real isolated FSI host exactly like every other
  // DirectoryConfig case in this file — it was the one case in this module
  // NOT tagged/registered as `[Integration]`, so it silently ran inside the
  // "fast" default `--summary` suite. Measured cold, unfiltered by any other
  // case's cache warmth: `--filter-test-case "reads false from config"`
  // alone took 10.5s — an isolated FSI host boot, not a fast unit test.
  Integration.hostList "DirectoryConfig.autoOpenNamespacesForDirectory" [

    testCase "reads false from config" <| fun _ ->
      withTempDir
        (fun dir ->
          addConfig dir """{ DirectoryConfig.empty with AutoOpenNamespaces = false }""")
        (fun dir ->
          DirectoryConfig.autoOpenNamespacesForDirectory dir
          |> Expect.isFalse "should use config override")

    testCase "defaults to true when no config exists" <| fun _ ->
      withTempDir
        (fun _ -> ())
        (fun dir ->
          DirectoryConfig.autoOpenNamespacesForDirectory dir
          |> Expect.isTrue "should default to true")
  ]
]
