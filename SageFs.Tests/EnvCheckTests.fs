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

let private authoritySession id workingDirectory status : EnvCheck.SessionAuthoritySession =
  { Id = id
    WorkingDirectory = workingDirectory
    Status = status }

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

let private capturePrint (results: EnvCheck.CheckResult list) =
  let orig = Console.Out
  use writer = new StringWriter()
  Console.SetOut(writer)
  try
    let failures = EnvCheck.print results
    writer.ToString(), failures
  finally
    Console.SetOut(orig)

let private suppressPrint (results: EnvCheck.CheckResult list) =
  capturePrint results |> snd

[<Tests>]
let sessionAuthorityTests =
  testList "EnvCheck.session authority" [
    test "classifySessionAuthority matches target directory after normalization" {
      let sessions = [
        authoritySession "abc12345" "C:/Code/Repos/SageFs/SageFs\\" "Ready"
      ]

      match EnvCheck.classifySessionAuthority "C:\\Code\\Repos\\SageFs\\SageFs" sessions with
      | EnvCheck.SessionAuthority.ExactMatch session ->
        session.Id |> Expect.equal "matching session selected" "abc12345"
      | other ->
        failtestf "expected ExactMatch, got %A" other
    }

    test "classifySessionAuthority reports ambiguity for multiple target matches" {
      let sessions = [
        authoritySession "abc12345" "C:\\Code\\Repos\\SageFs\\SageFs" "Ready"
        authoritySession "def67890" "C:\\Code\\Repos\\SageFs\\SageFs\\" "Evaluating"
      ]

      match EnvCheck.classifySessionAuthority "C:\\Code\\Repos\\SageFs\\SageFs" sessions with
      | EnvCheck.SessionAuthority.Ambiguous matches ->
        matches |> List.map _.Id |> Expect.equal "both matching sessions returned" [ "abc12345"; "def67890" ]
      | other ->
        failtestf "expected Ambiguous, got %A" other
    }

    test "checkSessionAuthority warns when daemon is absent" {
      let result = EnvCheck.checkSessionAuthority "C:\\Code\\Repos\\SageFs\\SageFs" None
      result.Status |> Expect.equal "authority is not verified without a daemon" EnvCheck.Status.Warn
      result.Detail |> Expect.stringContains "detail should say not checked" "Not checked"
      result.Hint |> Expect.isSome "should explain how to verify authority"
    }

    test "checkSessionAuthority passes when exactly one ready session matches the target" {
      let result =
        EnvCheck.checkSessionAuthority
          "C:\\Code\\Repos\\SageFs\\SageFs"
          (Some [ authoritySession "abc12345" "C:\\Code\\Repos\\SageFs\\SageFs" "Ready" ])

      result.Status |> Expect.equal "single ready match should pass" EnvCheck.Status.Pass
      result.Detail |> Expect.stringContains "detail should mention one match" "1 matching session"
      result.Detail |> Expect.stringContains "detail should mention ready status" "Ready"
    }

    test "checkSessionAuthority warns when daemon has no matching session for the target" {
      let result =
        EnvCheck.checkSessionAuthority
          "C:\\Code\\Repos\\SageFs\\SageFs"
          (Some [ authoritySession "other123" "C:\\Code\\Repos\\SageFs\\sagefs-vscode\\src" "Ready" ])

      result.Status |> Expect.equal "missing target session should warn" EnvCheck.Status.Warn
      result.Detail |> Expect.stringContains "detail should mention no match" "No matching session"
      result.Hint |> Expect.isSome "should explain how to fix the mismatch"
    }

    test "checkSessionAuthority warns when the only matching session is not ready" {
      let result =
        EnvCheck.checkSessionAuthority
          "C:\\Code\\Repos\\SageFs\\SageFs"
          (Some [ authoritySession "abc12345" "C:\\Code\\Repos\\SageFs\\SageFs" "Evaluating" ])

      result.Status |> Expect.equal "non-ready match should warn" EnvCheck.Status.Warn
      result.Detail |> Expect.stringContains "detail should mention session status" "Evaluating"
      result.Hint |> Expect.isSome "should guide the user"
    }
  ]

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

    test "all-pass summary no longer claims SageFs is ready to run" {
      let results : EnvCheck.CheckResult list = [
        { Icon = "✓"; Label = ".NET SDK"; Status = EnvCheck.Status.Pass; Detail = "ok"; Hint = None }
        { Icon = "✓"; Label = "Target session authority"; Status = EnvCheck.Status.Pass; Detail = "1 matching session for C:\\Code\\Repos\\SageFs\\SageFs (abc12345, Ready)"; Hint = None }
      ]

      let output, failures = capturePrint results
      failures |> Expect.equal "all pass should still have zero failures" 0
      output.Contains("ready to run") |> Expect.isFalse "summary should not overclaim readiness"
      output |> Expect.stringContains "summary should describe requested checks" "All requested checks passed"
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
    sessionAuthorityTests
    printTests
  ]
