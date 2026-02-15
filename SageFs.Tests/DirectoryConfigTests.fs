module SageFs.Tests.DirectoryConfigTests

open System
open System.IO
open Expecto
open SageFs

[<Tests>]
let parseTests = testList "DirectoryConfig.parse" [
  testCase "extracts projects list" (fun () ->
    let content = """let projects = ["Lib.fsproj"; "Tests.fsproj"]"""
    let config = DirectoryConfig.parse content
    Expect.equal config.Projects ["Lib.fsproj"; "Tests.fsproj"] "should parse projects")

  testCase "extracts autoLoad false" (fun () ->
    let config = DirectoryConfig.parse "let autoLoad = false"
    Expect.isFalse config.AutoLoad "should be false")

  testCase "extracts autoLoad true" (fun () ->
    let config = DirectoryConfig.parse "let autoLoad = true"
    Expect.isTrue config.AutoLoad "should be true")

  testCase "extracts initScript with Some" (fun () ->
    let content = """let initScript = Some "setup.fsx" """
    let config = DirectoryConfig.parse content
    Expect.equal config.InitScript (Some "setup.fsx") "should extract init script")

  testCase "extracts initScript as string" (fun () ->
    let content = """let initScript = "init.fsx" """
    let config = DirectoryConfig.parse content
    Expect.equal config.InitScript (Some "init.fsx") "should extract init script")

  testCase "extracts defaultArgs" (fun () ->
    let content = """let defaultArgs = ["--no-warn:1182"; "--bare"]"""
    let config = DirectoryConfig.parse content
    Expect.equal config.DefaultArgs ["--no-warn:1182"; "--bare"] "should parse args")

  testCase "full config file" (fun () ->
    let content = """let projects = ["App.fsproj"; "App.Tests.fsproj"]
let autoLoad = false
let initScript = Some "bootstrap.fsx"
let defaultArgs = ["--no-watch"]"""
    let config = DirectoryConfig.parse content
    Expect.equal config.Projects ["App.fsproj"; "App.Tests.fsproj"] "projects"
    Expect.isFalse config.AutoLoad "autoLoad"
    Expect.equal config.InitScript (Some "bootstrap.fsx") "initScript"
    Expect.equal config.DefaultArgs ["--no-watch"] "defaultArgs")

  testCase "empty content returns defaults" (fun () ->
    let config = DirectoryConfig.parse ""
    Expect.equal config DirectoryConfig.empty "should return empty defaults")
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
      """let projects = ["Test.fsproj"]""")
    try
      let result = DirectoryConfig.load tempDir
      Expect.isSome result "should find config"
      Expect.equal result.Value.Projects ["Test.fsproj"] "projects"
    finally
      Directory.Delete(tempDir, true))

  testCase "configPath constructs correct path" (fun () ->
    let path = DirectoryConfig.configPath @"C:\Code\MyProject"
    Expect.stringContains path ".SageFs" "contains .SageFs"
    Expect.stringContains path "config.fsx" "contains config.fsx")
]
