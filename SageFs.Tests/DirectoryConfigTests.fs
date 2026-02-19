module SageFs.Tests.DirectoryConfigTests

open System
open System.IO
open Expecto
open SageFs

/// Helper to evaluate config and unwrap the Ok result.
let evalOk content =
  match DirectoryConfig.evaluate content with
  | Ok cfg -> cfg
  | Error msg -> failwithf "Config evaluation failed: %s" msg

[<Tests>]
let evaluateTests = testList "DirectoryConfig.evaluate" [
  testCase "loads solution strategy" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with Load = Solution "MyApp.sln" }"""
    Expect.equal config.Load (Solution "MyApp.sln") "should parse solution")

  testCase "loads projects strategy" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with Load = Projects ["Lib.fsproj"; "Tests.fsproj"] }"""
    Expect.equal config.Load (Projects ["Lib.fsproj"; "Tests.fsproj"]) "should parse projects")

  testCase "loads NoLoad strategy" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with Load = NoLoad }"""
    Expect.equal config.Load NoLoad "should parse NoLoad")

  testCase "loads AutoDetect strategy" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with Load = AutoDetect }"""
    Expect.equal config.Load AutoDetect "should parse AutoDetect")

  testCase "loads initScript" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with InitScript = Some "setup.fsx" }"""
    Expect.equal config.InitScript (Some "setup.fsx") "should parse initScript")

  testCase "loads defaultArgs" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with DefaultArgs = ["--no-warn:1182"; "--bare"] }"""
    Expect.equal config.DefaultArgs ["--no-warn:1182"; "--bare"] "should parse defaultArgs")

  testCase "loads full config" (fun () ->
    let config = evalOk """
{ DirectoryConfig.empty with
    Load = Solution "BigApp.slnx"
    InitScript = Some "bootstrap.fsx"
    DefaultArgs = ["--no-watch"] }"""
    Expect.equal config.Load (Solution "BigApp.slnx") "load strategy"
    Expect.equal config.InitScript (Some "bootstrap.fsx") "initScript"
    Expect.equal config.DefaultArgs ["--no-watch"] "defaultArgs")

  testCase "empty expression returns defaults" (fun () ->
    let config = evalOk "DirectoryConfig.empty"
    Expect.equal config DirectoryConfig.empty "should return empty defaults"
    Expect.equal config.IsRoot false "IsRoot defaults to false"
    Expect.equal config.SessionName None "SessionName defaults to None")

  testCase "loads isRoot override" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with IsRoot = true }"""
    Expect.equal config.IsRoot true "should parse IsRoot")

  testCase "loads sessionName" (fun () ->
    let config = evalOk """{ DirectoryConfig.empty with SessionName = Some "my-service" }"""
    Expect.equal config.SessionName (Some "my-service") "should parse SessionName")

  testCase "invalid expression returns Error" (fun () ->
    let result = DirectoryConfig.evaluate "this is not valid F#"
    Expect.isError result "should return error for invalid expression")
]

[<Tests>]
let loadTests = testList "DirectoryConfig.load" [
  testCase "returns None when no config dir" (fun () ->
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tempDir) |> ignore
    try
      let result = DirectoryConfig.load tempDir
      Expect.isNone result "no config file"
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
      Expect.isSome result "should find config"
      Expect.equal result.Value.Load (Projects ["Test.fsproj"]) "load strategy"
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
      Expect.isSome result "should still return Some"
      Expect.equal result.Value DirectoryConfig.empty "should fall back to defaults"
    finally
      Directory.Delete(tempDir, true))

  testCase "configPath constructs correct path" (fun () ->
    let path = DirectoryConfig.configPath @"C:\Code\MyProject"
    Expect.stringContains path ".SageFs" "contains .SageFs"
    Expect.stringContains path "config.fsx" "contains config.fsx")
]
