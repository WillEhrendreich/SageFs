module SageFs.Tests.DirectoryConfigTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Tests.TestInfrastructure

/// Helper to evaluate config and unwrap the Ok result.
let evalOk content =
  match DirectoryConfig.evaluate content with
  | Ok cfg -> cfg
  | Error msg -> failwithf "Config evaluation failed: %s" msg

[<Tests>]
let evaluateTests = Integration.hostList "DirectoryConfig.evaluate" [
  testCase "loads solution strategy" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with Load = Solution "MyApp.sln" }"""
    config.Load |> Expect.equal "should parse solution" (Solution "MyApp.sln"))

  testCase "loads projects strategy" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with Load = Projects ["Lib.fsproj"; "Tests.fsproj"] }"""
    config.Load |> Expect.equal "should parse projects" (Projects ["Lib.fsproj"; "Tests.fsproj"]))

  testCase "loads NoLoad strategy" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with Load = NoLoad }"""
    config.Load |> Expect.equal "should parse NoLoad" NoLoad)

  testCase "loads AutoDetect strategy" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with Load = AutoDetect }"""
    config.Load |> Expect.equal "should parse AutoDetect" AutoDetect)

  testCase "loads initScript" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with InitScript = Some "setup.fsx" }"""
    config.InitScript |> Expect.equal "should parse initScript" (Some "setup.fsx"))

  testCase "loads defaultArgs" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with DefaultArgs = ["--no-warn:1182"; "--bare"] }"""
    config.DefaultArgs |> Expect.equal "should parse defaultArgs" ["--no-warn:1182"; "--bare"])

  testCase "loads autoOpenNamespaces override" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with AutoOpenNamespaces = false }"""
    config.AutoOpenNamespaces |> Expect.equal "should parse AutoOpenNamespaces" false)

  testCase "opt-out template evaluates (single-line, no offside error)" (fun () ->
    // Regression: the template used to be a multi-line record update starting
    // at column 1, which FSI rejects with "this token is offside of context
    // started at position (1:3)" — so the disable button wrote a config that
    // could never load, silently keeping auto-open ON.
    let config = evalOk DirectoryConfig.autoOpenNamespacesOptOutTemplate
    config.AutoOpenNamespaces |> Expect.equal "template should disable auto-open" false)

  testCase "loads full config" (fun () ->
    let config = evalOk """
{ DirectoryConfig.empty with
    Load = Solution "BigApp.slnx"
    InitScript = Some "bootstrap.fsx"
    DefaultArgs = ["--no-watch"] }"""
    config.Load |> Expect.equal "load strategy" (Solution "BigApp.slnx")
    config.InitScript |> Expect.equal "initScript" (Some "bootstrap.fsx")
    config.DefaultArgs |> Expect.equal "defaultArgs" ["--no-watch"])

  testCase "empty expression returns defaults" (fun () ->
    let config = evalOk "DirectoryConfig.empty"
    config |> Expect.equal "should return empty defaults" DirectoryConfig.empty
    config.AutoOpenNamespaces |> Expect.equal "AutoOpenNamespaces defaults to true" true
    config.IsRoot |> Expect.equal "IsRoot defaults to false" false
    config.SessionName |> Expect.equal "SessionName defaults to None" None)

  testCase "loads isRoot override" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with IsRoot = true }"""
    config.IsRoot |> Expect.equal "should parse IsRoot" true)

  testCase "loads sessionName" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with SessionName = Some "my-service" }"""
    config.SessionName |> Expect.equal "should parse SessionName" (Some "my-service"))

  testCase "invalid expression returns Error" (fun () ->
    let result = DirectoryConfig.evaluate "this is not valid F#"
    result |> Expect.isError "should return error for invalid expression")
]

[<Tests>]
let loadTests = Integration.hostList "DirectoryConfig.load" [
  testCase "returns None when no config dir" (fun () ->
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tempDir) |> ignore
    try
      let result = DirectoryConfig.load tempDir
      result |> Expect.isNone "no config file"
    finally
      Directory.Delete(tempDir, true))

  testCase "returns Some when config exists" (fun () ->
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    let configDir = Path.Combine(tempDir, ".SageFs")
    Directory.CreateDirectory(configDir) |> ignore
    File.WriteAllText(
      Path.Combine(configDir, "config.fsx"),
      """{ DirectoryConfig.empty with Load = Projects ["Test.fsproj"] }""")
    try
      let result = DirectoryConfig.load tempDir
      result |> Expect.isSome "should find config"
      result.Value.Load |> Expect.equal "load strategy" (Projects ["Test.fsproj"])
    finally
      Directory.Delete(tempDir, true))

  testCase "returns defaults on malformed config" (fun () ->
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    let configDir = Path.Combine(tempDir, ".SageFs")
    Directory.CreateDirectory(configDir) |> ignore
    File.WriteAllText(
      Path.Combine(configDir, "config.fsx"),
      "this is garbage")
    try
      let result = DirectoryConfig.load tempDir
      result |> Expect.isSome "should still return Some"
      result.Value |> Expect.equal "should fall back to defaults" DirectoryConfig.empty
    finally
      Directory.Delete(tempDir, true))

  testCase "configPath constructs correct path" (fun () ->
    let path = DirectoryConfig.configPath @"C:\Code\MyProject"
    path |> Expect.stringContains "contains .SageFs" ".SageFs"
    path |> Expect.stringContains "contains config.fsx" "config.fsx")
]

[<Tests>]
let ensureAutoOpenOptOutTests = Integration.hostList "DirectoryConfig.ensureAutoOpenNamespacesOptOut" [
  testCase "creates config when missing" (fun () ->
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tempDir) |> ignore
    try
      let result = DirectoryConfig.ensureAutoOpenNamespacesOptOut tempDir
      match result with
      | Ok (AutoOpenNamespacesOptOutResult.Created path) ->
        path |> Expect.equal "should create config at expected path" (DirectoryConfig.configPath tempDir)
        (File.Exists path) |> Expect.isTrue "config file should exist"
        (File.ReadAllText path) |> Expect.stringContains "config should disable auto-open" "AutoOpenNamespaces = false"
      | other ->
        failtestf "expected Created, got %A" other
    finally
      Directory.Delete(tempDir, true))

  testCase "returns already disabled when config already opts out" (fun () ->
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    let configDir = Path.Combine(tempDir, ".SageFs")
    Directory.CreateDirectory(configDir) |> ignore
    let path = Path.Combine(configDir, "config.fsx")
    File.WriteAllText(path, """{ DirectoryConfig.empty with AutoOpenNamespaces = false }""")
    try
      let result = DirectoryConfig.ensureAutoOpenNamespacesOptOut tempDir
      match result with
      | Ok (AutoOpenNamespacesOptOutResult.AlreadyDisabled actualPath) ->
        actualPath |> Expect.equal "should report existing config path" path
      | other ->
        failtestf "expected AlreadyDisabled, got %A" other
    finally
      Directory.Delete(tempDir, true))

  testCase "requires manual edit when config exists without opt-out" (fun () ->
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    let configDir = Path.Combine(tempDir, ".SageFs")
    Directory.CreateDirectory(configDir) |> ignore
    let path = Path.Combine(configDir, "config.fsx")
    File.WriteAllText(path, """{ DirectoryConfig.empty with Load = AutoDetect }""")
    try
      let original = File.ReadAllText path
      let result = DirectoryConfig.ensureAutoOpenNamespacesOptOut tempDir
      match result with
      | Ok (AutoOpenNamespacesOptOutResult.RequiresManualEdit actualPath) ->
        actualPath |> Expect.equal "should report existing config path" path
        (File.ReadAllText path) |> Expect.equal "should not overwrite existing config" original
      | other ->
        failtestf "expected RequiresManualEdit, got %A" other
    finally
      Directory.Delete(tempDir, true))
]

/// The isolated-host evaluation of config.fsx: what comes back for each way a script can go right or wrong.
[<Tests>]
let configHostTests =
  Integration.hostList "ConfigHost (config.fsx is evaluated in an isolated host, never in the daemon)" [
    testCase "a script that does not compile is ScriptRejected with the compiler's diagnostics" (fun () ->
      match ConfigHost.evaluate Environment.CurrentDirectory "this is not valid F#" with
      | Error(ConfigHost.ScriptRejected(SageFs.FsiHost.FsiProtocol.ConfigDoesNotCompile diagnostics)) ->
        diagnostics |> Expect.isNonEmpty "carries the diagnostics"
      | other -> failtestf "expected ConfigDoesNotCompile, got %A" other)

    testCase "an expression of the wrong type is ConfigWrongType naming it" (fun () ->
      ConfigHost.evaluate Environment.CurrentDirectory "42"
      |> Expect.equal "typed" (Error(ConfigHost.ScriptRejected(SageFs.FsiHost.FsiProtocol.ConfigWrongType "Int32"))))

    testCase "a script that throws is ConfigThrew carrying the message" (fun () ->
      match ConfigHost.evaluate Environment.CurrentDirectory "(failwith \"nope\" : DirectoryConfig)" with
      | Error(ConfigHost.ScriptRejected(SageFs.FsiHost.FsiProtocol.ConfigThrew message)) ->
        message |> Expect.stringContains "the exception message" "nope"
      | other -> failtestf "expected ConfigThrew, got %A" other)

    testCase "the full record round-trips: every field reaches the daemon" (fun () ->
      let script =
        """{ DirectoryConfig.empty with Load = Projects ["A.fsproj"]; InitScript = Some "init.fsx"; DefaultArgs = ["--x"]; AutoOpenNamespaces = false; IsRoot = true; SessionName = Some "demo" }"""
      match ConfigHost.evaluate Environment.CurrentDirectory script with
      | Ok config ->
        config
        |> Expect.equal
             "all fields"
             { Load = Projects [ "A.fsproj" ]
               InitScript = Some "init.fsx"
               DefaultArgs = [ "--x" ]
               AutoOpenNamespaces = false
               IsRoot = true
               SessionName = Some "demo" }
      | Error error -> failtest (ConfigHost.describeError error))

    testCase "the same script text is evaluated once: the second answer is cached and identical" (fun () ->
      let script = """{ DirectoryConfig.empty with SessionName = Some "cached-demo" }"""
      let first = ConfigHost.evaluate Environment.CurrentDirectory script
      let stopwatch = Diagnostics.Stopwatch.StartNew()
      let second = ConfigHost.evaluate Environment.CurrentDirectory script
      second |> Expect.equal "same result" first
      Expect.isLessThan "no second host was started" (stopwatch.ElapsedMilliseconds, 100L))
  ]
