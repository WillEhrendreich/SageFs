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

let private sessionAuthorityTargetDir () =
  Path.Combine(Path.GetTempPath(), "sagefs-session-authority", "target")

let private normalizedSessionVariant (path: string) =
  let parent = Path.GetDirectoryName path
  let leaf = Path.GetFileName path
  Path.Combine(parent, ".", leaf).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
  + string Path.DirectorySeparatorChar

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

// ── checkDotnetSdk (fake `dotnet --list-sdks` output) ─────────────────────────

let private repoRequirement : EnvCheck.SdkRequirement =
  { Version = "11.0.100-preview.7"
    RollForward = "latestMinor"
    AllowPrerelease = true }

let private sdkLine version = sprintf "%s [C:\\Program Files\\dotnet\\sdk]" version

[<Tests>]
let dotnetSdkTests =
  testList "EnvCheck.checkDotnetSdk" [
    test "parses a `dotnet --list-sdks` line into its version" {
      EnvCheck.tryParseSdkListLine (sdkLine "11.0.100-preview.7.26381.103")
      |> Expect.equal "version extracted before the bracket path" (Some "11.0.100-preview.7.26381.103")
    }

    test "passes when the pinned SDK is installed" {
      let result =
        EnvCheck.sdkCheckFromInputs
          (Some repoRequirement)
          None
          [ sdkLine "11.0.100-preview.7.26381.103" ]
      result.Status |> Expect.equal "installed pin should pass" EnvCheck.Status.Pass
      result.Detail |> Expect.stringContains "detail should cite the installed SDK" "11.0.100-preview.7.26381.103"
      result.Detail |> Expect.stringContains "detail should cite global.json" "global.json"
    }

    test "passes when a newer SDK in the same major satisfies latestMinor" {
      let result =
        EnvCheck.sdkCheckFromInputs
          (Some repoRequirement)
          None
          [ sdkLine "11.0.200" ]
      result.Status |> Expect.equal "11.0.200 satisfies a latestMinor 11.0.100 pin" EnvCheck.Status.Pass
    }

    test "fails when only a lower-major SDK is installed" {
      let result =
        EnvCheck.sdkCheckFromInputs
          (Some repoRequirement)
          None
          [ sdkLine "10.0.204" ]
      result.Status |> Expect.equal "10.0.x must not satisfy an 11.0 latestMinor pin" EnvCheck.Status.Fail
      match result.Hint with
      | Some hint ->
        hint |> Expect.stringContains "hint must name the exact SDK to install" "11.0.100-preview.7"
        hint |> Expect.stringContains "hint should point at the .NET 11 downloads" "download/dotnet/11.0"
      | None -> failtest "missing SDK should carry install guidance"
    }

    test "latestMinor does not roll forward into a higher major" {
      let result =
        EnvCheck.sdkCheckFromInputs
          (Some repoRequirement)
          None
          [ sdkLine "12.0.100" ]
      result.Status |> Expect.equal "12.0.x is outside latestMinor" EnvCheck.Status.Fail
    }

    test "allowPrerelease=false excludes prerelease SDKs" {
      let req : EnvCheck.SdkRequirement =
        { Version = "11.0.200"; RollForward = "latestMinor"; AllowPrerelease = false }
      let withOnlyPrerelease =
        EnvCheck.sdkCheckFromInputs (Some req) None [ sdkLine "11.0.100-preview.7.26381.103" ]
      withOnlyPrerelease.Status |> Expect.equal "prerelease SDK must be excluded" EnvCheck.Status.Fail
      let withStable =
        EnvCheck.sdkCheckFromInputs (Some req) None [ sdkLine "11.0.200" ]
      withStable.Status |> Expect.equal "stable SDK satisfies the pin" EnvCheck.Status.Pass
    }

    test "fails when the selected SDK cannot build the project's target framework" {
      let result =
        EnvCheck.sdkCheckFromInputs (Some repoRequirement) (Some 12) [ sdkLine "11.0.100-preview.7.26381.103" ]
      result.Status |> Expect.equal "SDK 11 cannot build a net12.0 project" EnvCheck.Status.Fail
      match result.Hint with
      | Some hint -> hint |> Expect.stringContains "hint should say which SDK to install" "12"
      | None -> failtest "target framework mismatch should carry install guidance"
    }

    test "passes with any SDK when no global.json pin is found" {
      let result =
        EnvCheck.sdkCheckFromInputs None None [ sdkLine "9.0.317" ]
      result.Status |> Expect.equal "any SDK passes without a pin" EnvCheck.Status.Pass
    }

    test "fails when no SDK is installed at all" {
      let result = EnvCheck.sdkCheckFromInputs (Some repoRequirement) None []
      result.Status |> Expect.equal "no SDKs must fail" EnvCheck.Status.Fail
      match result.Hint with
      | Some hint -> hint |> Expect.stringContains "hint should say what to install" "Install the .NET SDK"
      | None -> failtest "empty SDK list should carry install guidance"
    }

    test "parses a global.json sdk section" {
      let json = """{ "sdk": { "version": "11.0.100-preview.7", "rollForward": "latestMinor", "allowPrerelease": true } }"""
      EnvCheck.parseGlobalJsonSdkRequirement json
      |> Expect.equal "requirement parsed" (Ok (Some repoRequirement))
    }

    test "global.json without an sdk section means no pin" {
      EnvCheck.parseGlobalJsonSdkRequirement """{ "msbuild-sdks": {} }"""
      |> Expect.equal "no sdk section" (Ok None)
    }

    test "global.json defaults rollForward to patch and allowPrerelease to true" {
      EnvCheck.parseGlobalJsonSdkRequirement """{ "sdk": { "version": "8.0.100" } }"""
      |> function
         | Ok (Some req) ->
           req.RollForward |> Expect.equal "default policy is patch" "patch"
           req.AllowPrerelease |> Expect.isTrue "prerelease is allowed by default"
         | other -> failtestf "expected a requirement, got %A" other
    }

    test "rejects an invalid sdk.version pin" {
      let result =
        EnvCheck.sdkCheckFromInputs
          (Some { Version = "11"; RollForward = "latestMinor"; AllowPrerelease = true })
          None
          [ sdkLine "11.0.100" ]
      result.Status |> Expect.equal "invalid pin must fail" EnvCheck.Status.Fail
      result.Detail |> Expect.stringContains "detail should name the bad pin" "11"
    }
  ]

// ── checkFsiAvailable (probe outcome, no process spawned) ─────────────────────

[<Tests>]
let fsiProbeTests =
  testList "EnvCheck.checkFsiAvailable (probe outcome)" [
    test "Pass when dotnet fsi starts and exits" {
      let r = EnvCheck.checkFsiFromProbe EnvCheck.FsiStartedAndExited
      r.Status |> Expect.equal "clean fsi start should pass" EnvCheck.Status.Pass
      r.Detail |> Expect.stringContains "detail should confirm availability" "available"
    }

    test "Fail when the fsi probe is killed on timeout" {
      let r = EnvCheck.checkFsiFromProbe EnvCheck.FsiTimedOutAndKilled
      r.Status |> Expect.equal "a killed probe must not pass" EnvCheck.Status.Fail
      r.Hint |> Expect.isSome "timeout should carry a hint"
    }

    test "Fail when dotnet fsi cannot start" {
      let r = EnvCheck.checkFsiFromProbe (EnvCheck.FsiFailedToStart "boom")
      r.Status |> Expect.equal "start failure must fail" EnvCheck.Status.Fail
      r.Hint |> Expect.isSome "start failure should carry a hint"
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
      let targetDir = sessionAuthorityTargetDir ()
      let sessions = [
        authoritySession "abc12345" (normalizedSessionVariant targetDir) "Ready"
      ]

      match EnvCheck.classifySessionAuthority targetDir sessions with
      | EnvCheck.SessionAuthority.ExactMatch session ->
        session.Id |> Expect.equal "matching session selected" "abc12345"
      | other ->
        failtestf "expected ExactMatch, got %A" other
    }

    test "classifySessionAuthority reports ambiguity for multiple target matches" {
      let targetDir = sessionAuthorityTargetDir ()
      let sessions = [
        authoritySession "abc12345" targetDir "Ready"
        authoritySession "def67890" (normalizedSessionVariant targetDir) "Evaluating"
      ]

      match EnvCheck.classifySessionAuthority targetDir sessions with
      | EnvCheck.SessionAuthority.Ambiguous matches ->
        matches |> List.map _.Id |> Expect.equal "both matching sessions returned" [ "abc12345"; "def67890" ]
      | other ->
        failtestf "expected Ambiguous, got %A" other
    }

    test "checkSessionAuthority warns when daemon is absent" {
      let targetDir = sessionAuthorityTargetDir ()
      let result = EnvCheck.checkSessionAuthority targetDir None
      result.Status |> Expect.equal "authority is not verified without a daemon" EnvCheck.Status.Warn
      result.Detail |> Expect.stringContains "detail should say not checked" "Not checked"
      result.Hint |> Expect.isSome "should explain how to verify authority"
    }

    test "checkSessionAuthority passes when exactly one ready session matches the target" {
      let targetDir = sessionAuthorityTargetDir ()
      let result =
        EnvCheck.checkSessionAuthority
          targetDir
          (Some [ authoritySession "abc12345" targetDir "Ready" ])

      result.Status |> Expect.equal "single ready match should pass" EnvCheck.Status.Pass
      result.Detail |> Expect.stringContains "detail should mention one match" "1 matching session"
      result.Detail |> Expect.stringContains "detail should mention ready status" "Ready"
    }

    test "checkSessionAuthority warns when daemon has no matching session for the target" {
      let targetDir = sessionAuthorityTargetDir ()
      let otherDir = Path.Combine(Path.GetTempPath(), "sagefs-session-authority", "other")
      let result =
        EnvCheck.checkSessionAuthority
          targetDir
          (Some [ authoritySession "other123" otherDir "Ready" ])

      result.Status |> Expect.equal "missing target session should warn" EnvCheck.Status.Warn
      result.Detail |> Expect.stringContains "detail should mention no match" "No matching session"
      result.Hint |> Expect.isSome "should explain how to fix the mismatch"
    }

    test "checkSessionAuthority warns when the only matching session is not ready" {
      let targetDir = sessionAuthorityTargetDir ()
      let result =
        EnvCheck.checkSessionAuthority
          targetDir
          (Some [ authoritySession "abc12345" targetDir "Evaluating" ])

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
    fsiProbeTests
    sessionAuthorityTests
    printTests
  ]
