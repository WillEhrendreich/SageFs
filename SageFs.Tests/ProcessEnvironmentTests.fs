/// SageFs.ProcessEnvironment is the one place every spawn site drops MSBuild's
/// own resolution variables before starting a child process. Two real bugs
/// motivated it: a user's own `dotnet build` of a small fixture, run from
/// inside a session, failed with MSB4242 (SDK 11's MSBuild loaded on a net10
/// runtime); and the FSI host build ignored a repo-local SDK pin — both because
/// Ionide.ProjInfo pins those variables on THIS process the moment it loads a
/// project, and every child inherits them by default.
module SageFs.Tests.ProcessEnvironmentTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private allPoisoned : Map<string, string> =
  ProcessEnvironment.poisonedVariables
  |> List.map (fun k -> k, "poison")
  |> Map.ofList
  |> Map.add "PATH" "/usr/bin"

[<Tests>]
let sanitizeTests =
  testList "ProcessEnvironment.sanitize (pure)" [

    testCase "WHY — every poisoned key is gone, given a parent environment that has all of them set — the exact shape Ionide.ProjInfo leaves a daemon or worker process in" <| fun _ ->
      let result = ProcessEnvironment.sanitize allPoisoned []
      let stillPoisoned = ProcessEnvironment.poisonedVariables |> List.filter result.ContainsKey
      stillPoisoned |> Expect.isEmpty "no poisoned key survives sanitize"

    testCase "WHY — an innocent variable the parent legitimately needs (PATH) survives, because sanitize only removes the named poison list" <| fun _ ->
      let result = ProcessEnvironment.sanitize allPoisoned []
      result.ContainsKey "PATH" |> Expect.isTrue "PATH is not on the poison list"

    testCase "WHY — an override wins even when its own key is on the poison list — FsiHostBuild deliberately re-sets DOTNET_ROOT to point the host build at a repo-local SDK (an Arcade-style repo)" <| fun _ ->
      let result = ProcessEnvironment.sanitize allPoisoned [ "DOTNET_ROOT", "/repo/.dotnet" ]
      result.["DOTNET_ROOT"] |> Expect.equal "explicit override beats the poison-list removal" "/repo/.dotnet"

    testCase "WHY — a clean parent environment (nothing poisoned) round-trips unchanged aside from overrides" <| fun _ ->
      let clean = Map.ofList [ "PATH", "/usr/bin"; "HOME", "/home/x" ]
      ProcessEnvironment.sanitize clean [] |> Expect.equal "nothing to remove" clean
  ]

[<Tests>]
let applyToTests =
  testList "ProcessEnvironment.applyTo (real ProcessStartInfo)" [

    testCase "WHY — a ProcessStartInfo carrying every poisoned key no longer carries any of them after applyTo" <| fun _ ->
      let psi = ProcessStartInfo()
      for key in ProcessEnvironment.poisonedVariables do
        psi.Environment.[key] <- "poison"
      ProcessEnvironment.applyTo psi []
      let stillPoisoned = ProcessEnvironment.poisonedVariables |> List.filter psi.Environment.ContainsKey
      stillPoisoned |> Expect.isEmpty "no poisoned key survives applyTo"

    testCase "WHY — overrides land on the ProcessStartInfo after the poison scrub, same as sanitize" <| fun _ ->
      let psi = ProcessStartInfo()
      psi.Environment.["MSBuildSDKsPath"] <- "poison"
      ProcessEnvironment.applyTo psi [ "SAGEFS_TEST_MARKER", "hello" ]
      psi.Environment.ContainsKey "MSBuildSDKsPath" |> Expect.isFalse "poison removed"
      psi.Environment.["SAGEFS_TEST_MARKER"] |> Expect.equal "override applied" "hello"
  ]

/// A portable `ProcessStartInfo` that echoes one environment variable back on
/// stdout, so a test can prove what a REAL child process saw — not just what a
/// `Map` says it should see. `psi.Environment` is set directly on this one
/// instance (never `Environment.SetEnvironmentVariable`), so this never
/// touches process-wide state and needs no cross-test locking.
let private echoVariable (variable: string) : ProcessStartInfo =
  match OperatingSystem.IsWindows() with
  | true ->
    let psi = ProcessStartInfo("cmd.exe")
    psi.ArgumentList.Add "/c"
    psi.ArgumentList.Add(sprintf "echo %%%s%%" variable)
    psi
  | false ->
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add(sprintf "echo \"$%s\"" variable)
    psi

[<Tests>]
let realSpawnTests =
  Integration.hostList "ProcessEnvironment (real spawned process)" [

    testCase "WHY — a REAL child process does not see a poisoned MSBuildSDKsPath once applyTo has run — proven against the OS, not just the pure Map function" <| fun _ ->
      let psi = echoVariable "MSBuildSDKsPath"
      psi.RedirectStandardOutput <- true
      psi.UseShellExecute <- false
      psi.Environment.["MSBuildSDKsPath"] <- "poison-value"
      ProcessEnvironment.applyTo psi []
      use proc = Process.Start psi
      let output = proc.StandardOutput.ReadToEnd()
      proc.WaitForExit 5_000 |> ignore
      output.Contains "poison-value" |> Expect.isFalse "the child never sees the poisoned value"

    // The MSBuild-SDK-resolution failure is real and reproducible: MSBuildSDKsPath
    // pointed at a bogus directory makes a real `dotnet build` fail with
    // "Could not resolve SDK 'Microsoft.NET.Sdk'" (MSB4236) — verified by hand
    // against this SDK before writing this test. This proves SessionBuild's own
    // production call site (`runBuildAsync`, which now calls applyTo) survives
    // exactly that poisoning, reproducing the shape of the MSB4242 a user hit
    // building a fixture from inside a session.
    //
    // Mutates the real process environment for its duration — safe here only
    // because --integration-host runs this whole tree with testSequenced, so
    // no other test's `dotnet build` can observe it mid-flight; no other test
    // touches MSBuildSDKsPath.
    testAsync "WHY — a session's own dotnet build resolves its own project's SDK, not a poisoned MSBuild resolver this process (daemon or worker) may have picked up from Ionide.ProjInfo" {
      let workDir = Directory.CreateTempSubdirectory("sagefs-procenv-").FullName
      let original = Environment.GetEnvironmentVariable "MSBuildSDKsPath"
      try
        File.WriteAllText(
          Path.Combine(workDir, "Fixture.fsproj"),
          """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Lib.fs" /></ItemGroup>
</Project>""")
        File.WriteAllText(Path.Combine(workDir, "Lib.fs"), "module Fixture.Lib\nlet ok = 1\n")
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", "/nonexistent/sagefs-test-bogus/Sdks")
        let! result = SessionBuild.runBuildAsync [ "Fixture.fsproj" ] workDir
        match result with
        | Error err -> failtestf "expected the fixture to build despite this process's own poisoned MSBuildSDKsPath: %A" err
        | Ok _ -> ()
      finally
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", original)
        try Directory.Delete(workDir, true) with _ -> ()
    }
  ]

/// A seam for tools that must reach a worker they do not launch.
///
/// `forwarded` is pure, so these assert the contract exactly: only names
/// matching a declared prefix travel, a poisoned key NEVER travels even when
/// the prefix would match, and an explicit override wins over an inherited
/// value without being duplicated.
[<Tests>]
let forwardingTests =
  testList "ProcessEnvironment forwarding" [
    test "a prefix forwards exactly the variables that match it, and nothing else" {
      let parent =
        [ "SAGEFS_FOO", "1"; "SAGEFS_BAR", "2"; "OTHER", "3" ] |> Map.ofList
      ProcessEnvironment.forwarded parent [] [ "SAGEFS_" ]
      |> List.sortBy fst
      |> Expect.equal "only the matching keys" (List.sortBy fst [ "SAGEFS_FOO", "1"; "SAGEFS_BAR", "2" ])
    }

    test "no declared prefixes forwards nothing" {
      let parent = [ "SAGEFS_FOO", "1" ] |> Map.ofList
      ProcessEnvironment.forwarded parent [] []
      |> Expect.equal "nothing travels without a prefix" []
    }

    test "a prefix is a PREFIX, not a substring" {
      let parent = [ "SAGEFS_FOO", "1"; "X_SAGEFS_BAR", "2" ] |> Map.ofList
      ProcessEnvironment.forwarded parent [] [ "SAGEFS_" ]
      |> List.sortBy fst
      |> Expect.equal "X_SAGEFS_BAR does not start with the prefix" [ "SAGEFS_FOO", "1" ]
    }

    test "WHY — a poisoned key is never forwarded, even when the prefix matches it" {
      // The safety property, and the reason forwarding is not just "copy
      // whatever the tool declared". Forwarding DOTNET_ROOT would reintroduce
      // exactly the SDK-pinning failure this module exists to prevent, and a
      // tool cannot be trusted to know that.
      let parent = allPoisoned |> Map.add "SAGEFS_ROOT" "/poisoned"
      ProcessEnvironment.forwarded parent [] [ "SAGEFS_" ]
      |> List.exists (fun (k, _) -> ProcessEnvironment.poisonedVariables |> List.contains k)
      |> Expect.isFalse "a poisoned key must never be forwarded"
    }

    test "an explicit override wins over an inherited value, and is not duplicated" {
      let parent = [ "SAGEFS_FOO", "inherited" ] |> Map.ofList
      ProcessEnvironment.forwarded parent [ "SAGEFS_FOO", "explicit" ] [ "SAGEFS_" ]
      |> List.filter (fun (k, _) -> k = "SAGEFS_FOO")
      |> Expect.equal "the override appears exactly once" [ "SAGEFS_FOO", "explicit" ]
    }

    testList "splitPrefixes" [
      test "both separators parse, so one declaration works on either platform" {
        ProcessEnvironment.splitPrefixes "A;B"
        |> Expect.equal "semicolon (Windows)" [ "A"; "B" ]
        ProcessEnvironment.splitPrefixes "A:B"
        |> Expect.equal "colon (POSIX)" [ "A"; "B" ]
      }

      test "whitespace and empties are discarded" {
        ProcessEnvironment.splitPrefixes " A ; ; B "
        |> Expect.equal "trimmed, empties dropped" [ "A"; "B" ]
        ProcessEnvironment.splitPrefixes ""
        |> Expect.equal "empty declares nothing" []
      }
    ]

    // The end-to-end claim: a process SageFs SPAWNS receives a tool's variable,
    // even though SageFs knows nothing about that variable. A pure test of
    // `forwarded` is not that claim -- this is, and it calls the same function
    // the worker spawn site calls.
    testCase "a process spawned through applyToWithForwarding receives a declared tool variable" <| fun _ ->
      let workDir = Directory.CreateTempSubdirectory("sagefs-forward-").FullName
      let script = Path.Combine(workDir, "child.sh")
      File.WriteAllText(script, "#!/bin/sh\necho \"SEEN=$SAGEFS_DETERMINISM_POLICY\"\n")
      File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

      let saved =
        [ ProcessEnvironment.forwardPrefixesEnvVar; "SAGEFS_DETERMINISM_POLICY" ]
        |> List.map (fun k -> k, Environment.GetEnvironmentVariable k)
      try
        Environment.SetEnvironmentVariable(ProcessEnvironment.forwardPrefixesEnvVar, "SAGEFS_DETERMINISM_")
        Environment.SetEnvironmentVariable("SAGEFS_DETERMINISM_POLICY", "loss=40;latency=250ms")

        let psi = new ProcessStartInfo()
        psi.FileName <- "bash"
        psi.ArgumentList.Add(script)
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        // EXACTLY the call the worker spawn site makes (SessionManager.startWorkerProcess).
        ProcessEnvironment.applyToWithForwarding psi []

        use proc = Process.Start psi
        let out = proc.StandardOutput.ReadToEnd()
        proc.WaitForExit 30000 |> ignore
        out |> Expect.stringContains "the tool's variable reached the spawned process" "loss=40;latency=250ms"
      finally
        saved |> List.iter (fun (k, v) -> Environment.SetEnvironmentVariable(k, v))
        try Directory.Delete(workDir, true) with _ -> ()
  ]
