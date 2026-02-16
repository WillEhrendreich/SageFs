module SageFs.Tests.DirectoryConfigTests

open System
open System.IO
open Expecto
open SageFs

[<Tests>]
let parseTests = testList "DirectoryConfig.parse" [
  testCase "extracts projects list as LoadStrategy.Projects" (fun () ->
    let content = """let projects = ["Lib.fsproj"; "Tests.fsproj"]"""
    let config = DirectoryConfig.parse content
    Expect.equal config.Load (Projects ["Lib.fsproj"; "Tests.fsproj"]) "should parse projects")

  testCase "extracts solution as LoadStrategy.Solution" (fun () ->
    let content = """let solution = "MyApp.sln" """
    let config = DirectoryConfig.parse content
    Expect.equal config.Load (Solution "MyApp.sln") "should parse solution")

  testCase "autoLoad false produces NoLoad" (fun () ->
    let config = DirectoryConfig.parse "let autoLoad = false"
    Expect.equal config.Load NoLoad "should be NoLoad")

  testCase "autoLoad true produces AutoDetect" (fun () ->
    let config = DirectoryConfig.parse "let autoLoad = true"
    Expect.equal config.Load AutoDetect "should be AutoDetect")

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

  testCase "full config with projects" (fun () ->
    let content = """let projects = ["App.fsproj"; "App.Tests.fsproj"]
let autoLoad = false
let initScript = Some "bootstrap.fsx"
let defaultArgs = ["--no-watch"]"""
    let config = DirectoryConfig.parse content
    // projects takes precedence over autoLoad=false
    Expect.equal config.Load (Projects ["App.fsproj"; "App.Tests.fsproj"]) "load strategy"
    Expect.equal config.InitScript (Some "bootstrap.fsx") "initScript"
    Expect.equal config.DefaultArgs ["--no-watch"] "defaultArgs")

  testCase "full config with solution" (fun () ->
    let content = """let solution = "BigApp.slnx"
let initScript = Some "bootstrap.fsx"
let defaultArgs = ["--no-watch"]"""
    let config = DirectoryConfig.parse content
    Expect.equal config.Load (Solution "BigApp.slnx") "load strategy"
    Expect.equal config.InitScript (Some "bootstrap.fsx") "initScript")

  testCase "solution takes precedence over projects" (fun () ->
    let content = """let solution = "MyApp.sln"
let projects = ["Lib.fsproj"]"""
    let config = DirectoryConfig.parse content
    Expect.equal config.Load (Solution "MyApp.sln") "solution wins")

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
      Expect.equal result.Value.Load (Projects ["Test.fsproj"]) "load strategy"
    finally
      Directory.Delete(tempDir, true))

  testCase "configPath constructs correct path" (fun () ->
    let path = DirectoryConfig.configPath @"C:\Code\MyProject"
    Expect.stringContains path ".SageFs" "contains .SageFs"
    Expect.stringContains path "config.fsx" "contains config.fsx")
]
