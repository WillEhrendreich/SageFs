module SageFs.VisualStudio.Core.Tests.DaemonManagerTests

open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

[<Fact>]
let ``resolveConfiguredMcpPort uses daemon json port when available`` () =
  let json = """{"Url":"http://localhost:38123"}"""
  DaemonManager.tryReadConfiguredDaemonUrlFromContent json
  |> DaemonManager.resolveConfiguredMcpPort
  |> should equal 38123

[<Fact>]
let ``resolveConfiguredMcpPort falls back to default when daemon json is invalid`` () =
  let json = """{"Port":38123}"""
  DaemonManager.tryReadConfiguredDaemonUrlFromContent json
  |> DaemonManager.resolveConfiguredMcpPort
  |> should equal Constants.DefaultMcpPort

[<Fact>]
let ``buildDaemonArguments includes configured mcp port for project`` () =
  let args =
    DaemonManager.buildDaemonArguments @"C:\repo\App.fsproj" 38123

  args |> should equal """--proj "C:\repo\App.fsproj" --mcp-port 38123"""

[<Fact>]
let ``buildDaemonArguments uses solution flag for slnx`` () =
  let args =
    DaemonManager.buildDaemonArguments @"C:\repo\App.slnx" Constants.DefaultMcpPort

  args |> should equal """--sln "C:\repo\App.slnx" --mcp-port 37749"""
