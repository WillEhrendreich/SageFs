module SageFs.Tests.EnvCheckTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open Expecto
open Expecto.Flip

// ── Helpers ───────────────────────────────────────────────────────────────────

let private allPass results =
  results
  |> List.forall (fun (r: EnvCheck.CheckResult) -> r.Status = EnvCheck.Status.Pass)

let private anyFail results =
  results
  |> List.exists (fun (r: EnvCheck.CheckResult) -> r.Status = EnvCheck.Status.Fail)

let private anyWarn results =
  results
  |> List.exists (fun (r: EnvCheck.CheckResult) -> r.Status = EnvCheck.Status.Warn)

/// Bind a TCP listener so isPortFree returns false for that port.
let private withBoundPort (action: int -> unit) =
  let l = new TcpListener(IPAddress.Loopback, 0)
  l.Start()
  try
    let port = (l.LocalEndpoint :?> IPEndPoint).Port
    action port
  finally
    l.Stop()

// ── isPortFree ────────────────────────────────────────────────────────────────

[<Tests>]
let portFreeTests =
  testList "EnvCheck.isPortFree" [
    test "returns true for an unbound ephemeral port" {
      let l = new TcpListener(IPAddress.Loopback, 0)
      l.Start()
      let port = (l.LocalEndpoint :?> IPEndPoint).Port
      l.Stop()
      EnvCheck.isPortFree port
      |> Expect.isTrue "recently-released port should be free"
    }

    test "returns false when port is in use" {
      withBoundPort (fun port ->
        EnvCheck.isPortFree port
        |> Expect.isFalse "bound port should not be free")
    }
  ]

// ── findFsproj ────────────────────────────────────────────────────────────────

[<Tests>]
let findFsprojTests =
  testList "EnvCheck.findFsproj" [
    test "returns empty list for empty directory" {
      let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory(dir) |> ignore
      try
        EnvCheck.findFsproj dir
        |> Expect.equal "empty dir should have no fsproj files" []
      finally
        Directory.Delete(dir, true)
    }

    test "finds .fsproj files in directory" {
      let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory(dir) |> ignore
      try
        File.WriteAllText(Path.Combine(dir, "MyProject.fsproj"), "<Project/>")
        let found = EnvCheck.findFsproj dir
        found.Length |> Expect.equal "should find 1 fsproj" 1
        found.[0] |> Path.GetFileName |> Expect.equal "file name" "MyProject.fsproj"
      finally
        Directory.Delete(dir, true)
    }

    test "does not recurse into subdirectories" {
      let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      let sub = Path.Combine(dir, "sub")
      Directory.CreateDirectory(sub) |> ignore
      try
        File.WriteAllText(Path.Combine(sub, "Nested.fsproj"), "<Project/>")
        EnvCheck.findFsproj dir
        |> Expect.equal "should not recurse" []
      finally
        Directory.Delete(dir, true)
    }
  ]

// ── checkFsproj ───────────────────────────────────────────────────────────────

[<Tests>]
let checkFsprojTests =
  testList "EnvCheck.checkFsproj" [
    test "Pass when .fsproj is present" {
      let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory(dir) |> ignore
      try
        File.WriteAllText(Path.Combine(dir, "App.fsproj"), "<Project/>")
        let r = EnvCheck.checkFsproj dir
        r.Status |> Expect.equal "should be Pass" EnvCheck.Status.Pass
        r.Detail |> Expect.stringContains "detail should mention found" "found"
      finally
        Directory.Delete(dir, true)
    }

    test "Warn when no .fsproj in dir" {
      let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory(dir) |> ignore
      try
        let r = EnvCheck.checkFsproj dir
        r.Status |> Expect.equal "should be Warn" EnvCheck.Status.Warn
        r.Hint   |> Expect.isSome "should have a hint"
      finally
        Directory.Delete(dir, true)
    }
  ]

// ── checkPort ─────────────────────────────────────────────────────────────────

[<Tests>]
let checkPortTests =
  testList "EnvCheck.checkPort" [
    test "Pass when port is free" {
      let l = new TcpListener(IPAddress.Loopback, 0)
      l.Start()
      let port = (l.LocalEndpoint :?> IPEndPoint).Port
      l.Stop()
      let r = EnvCheck.checkPort "test port" port
      r.Status |> Expect.equal "should be Pass" EnvCheck.Status.Pass
    }

    test "Fail when port is occupied" {
      withBoundPort (fun port ->
        let r = EnvCheck.checkPort "test port" port
        r.Status |> Expect.equal "should be Fail" EnvCheck.Status.Fail
        r.Hint   |> Expect.isSome "should have a hint")
    }

    test "Label is included in result" {
      let l = new TcpListener(IPAddress.Loopback, 0)
      l.Start()
      let port = (l.LocalEndpoint :?> IPEndPoint).Port
      l.Stop()
      let r = EnvCheck.checkPort "MCP port" port
      r.Label |> Expect.equal "label preserved" "MCP port"
    }
  ]

// ── checkDotnetSdk ────────────────────────────────────────────────────────────

[<Tests>]
let dotnetSdkTests =
  testList "EnvCheck.checkDotnetSdk" [
    test "always returns Pass in a running process" {
      // If this test is running, .NET is present
      let r = EnvCheck.checkDotnetSdk ()
      r.Status |> Expect.equal "should be Pass" EnvCheck.Status.Pass
      r.Detail |> Expect.stringContains "should mention .NET" ".NET"
    }
  ]

// ── print (pure output logic) ─────────────────────────────────────────────────

let private suppressPrint (results: EnvCheck.CheckResult list) =
  let orig = Console.Out
  Console.SetOut(IO.TextWriter.Null)
  try EnvCheck.print results
  finally Console.SetOut(orig)

[<Tests>]
let printTests =
  testSequenced <| testList "EnvCheck.print" [
    test "returns 0 failures when all pass" {
      let results : EnvCheck.CheckResult list = [
        { Icon = "✓"; Label = "a"; Status = EnvCheck.Status.Pass; Detail = "ok"; Hint = None }
        { Icon = "✓"; Label = "b"; Status = EnvCheck.Status.Pass; Detail = "ok"; Hint = None }
      ]
      suppressPrint results |> Expect.equal "no failures" 0
    }

    test "returns count of Fail results" {
      let results : EnvCheck.CheckResult list = [
        { Icon = "✗"; Label = "a"; Status = EnvCheck.Status.Fail; Detail = "bad"; Hint = Some "fix it" }
        { Icon = "✓"; Label = "b"; Status = EnvCheck.Status.Pass; Detail = "ok"; Hint = None }
        { Icon = "✗"; Label = "c"; Status = EnvCheck.Status.Fail; Detail = "bad"; Hint = Some "fix it" }
      ]
      suppressPrint results |> Expect.equal "2 failures" 2
    }

    test "warnings do not count as failures" {
      let results : EnvCheck.CheckResult list = [
        { Icon = "⚠"; Label = "w"; Status = EnvCheck.Status.Warn; Detail = "maybe"; Hint = Some "check it" }
      ]
      suppressPrint results |> Expect.equal "warnings are not failures" 0
    }
  ]

[<Tests>]
let allEnvCheckTests =
  testList "EnvCheck" [
    portFreeTests
    findFsprojTests
    checkFsprojTests
    checkPortTests
    dotnetSdkTests
    printTests
  ]
