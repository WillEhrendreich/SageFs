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
open System.Threading
open System.Threading.Tasks
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

/// A throwaway fixture project for the current SDK, built by a real `dotnet` whose environment the case sets for
/// that CHILD only: the shared test process is never written to.
module FixtureBuild =
  let private currentMajor = Environment.Version.Major

  let private capture (workDir: string) (arguments: string list) (poison: (string * string) list) (stripFirst: bool) : Task<int * string> =
    task {
      let psi = ProcessStartInfo("dotnet", WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
      for argument in arguments do psi.ArgumentList.Add argument
      for key, value in poison do psi.Environment.[key] <- value
      if stripFirst then ProcessEnvironment.applyTo psi []
      use proc = Process.Start psi
      let output = proc.StandardOutput.ReadToEndAsync()
      let errors = proc.StandardError.ReadToEndAsync()
      use cts = new CancellationTokenSource(TestTimeouts.workerSessionReady)
      do! proc.WaitForExitAsync cts.Token
      let! out = output
      let! err = errors
      return proc.ExitCode, out + err
    }

  let create () : Task<string> =
    task {
      let workDir = Directory.CreateTempSubdirectory("sagefs-procenv-").FullName
      File.WriteAllText(
        Path.Combine(workDir, "Fixture.fsproj"),
        sprintf """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net%d.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Lib.fs" /></ItemGroup>
</Project>""" currentMajor)
      File.WriteAllText(Path.Combine(workDir, "Lib.fs"), "module Fixture.Lib\nlet ok = 1\n")
      return workDir
    }

  let run (workDir: string) (poison: (string * string) list) (stripFirst: bool) : Task<int * string> =
    capture workDir [ "build"; "Fixture.fsproj"; "--nologo" ] poison stripFirst

  let delete (workDir: string) = try Directory.Delete(workDir, true) with _ -> ()

  /// The directory of an installed SDK whose major version is not the current one, if any (`dotnet --list-sdks`
  /// prints `<version> [<sdk root>]`).
  let anotherSdk () : Task<string option> =
    task {
      let! _, listing = capture (Path.GetTempPath()) [ "--list-sdks" ] [] false
      return
        listing.Split('\n')
        |> Array.choose (fun line ->
          let m = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)\.\S+ \[(.+)\]\s*$")
          match m.Success && int m.Groups.[1].Value <> currentMajor with
          | true -> Some (Path.Combine(m.Groups.[2].Value, (line.Split(' ').[0])))
          | false -> None)
        |> Array.tryHead
    }

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
      proc.WaitForExit(TestTimeouts.briefPatience) |> ignore
      output.Contains "poison-value" |> Expect.isFalse "the child never sees the poisoned value"

    // The MSBuild-SDK-resolution failure is real and reproducible: MSBuildSDKsPath
    // pointed at a bogus directory makes a real `dotnet build` fail with
    // "Could not resolve SDK 'Microsoft.NET.Sdk'" (MSB4236) — verified by hand
    // against this SDK before writing this test. This proves SessionBuild's own
    // production call site (`runBuildAsync`, which now calls applyTo) survives
    // exactly that poisoning, reproducing the shape of the MSB4242 a user hit
    // building a fixture from inside a session.
    //
    // The poison goes into the CHILD's environment and nowhere else. This case used to set MSBuildSDKsPath on the
    // whole test process, "safe because this tree is testSequenced, so no other test's `dotnet build` can observe it".
    // testSequenced orders the cases of ONE list; every other list of the shard keeps running in the same process,
    // so a `dotnet build` another case started inside that window inherited the bogus path. The production call site
    // is covered without a real build by `buildStartInfo`'s own case below; this one proves the end result against
    // the real dotnet, and that the poison is real (the same build fails without `applyTo`), so it has teeth.
    testTask "WHY — a real dotnet build resolves its own project's SDK once applyTo has run, and fails on the same poison without it" {
      let! fixture = FixtureBuild.create ()
      try
        let poison = [ "MSBuildSDKsPath", "/nonexistent/sagefs-test-bogus/Sdks" ]
        let! stripped, strippedOutput = FixtureBuild.run fixture poison true
        stripped |> Expect.equal (sprintf "the build resolves its SDK once applyTo dropped the poison: %s" strippedOutput) 0
        let! poisoned, _ = FixtureBuild.run fixture poison false
        poisoned |> Expect.notEqual "and the same poison fails the build when nothing drops it, so the case above proves something" 0
      finally
        FixtureBuild.delete fixture
    }

    // The failure the gate showed nine times (#185): "Determining projects to restore... Build FAILED, 0 Error(s)"
    // in a second, only in the host shard that also held a case loading a project through Ionide.ProjInfo, and green
    // on every rerun alone. ProjInfo pins MSBuild's variables on its own process to the SDK it found, and every
    // `dotnet build` a neighbouring case starts inherits them: the build then runs under ANOTHER SDK's MSBuild
    // (the gate's log showed sdk/10.0.402 for a net11.0 harness) and fails with nothing printed. This pins another
    // installed SDK's MSBuild into the child the way ProjInfo pins it into a process, and builds a fixture for the
    // current SDK. The tool ships for net10.0 and net11.0, so every machine that gates it has both SDKs.
    testTask "WHY — a build whose environment pins another SDK's MSBuild, as Ionide.ProjInfo does to a process, builds under its own SDK once applyTo has run, and fails silently without it" {
      let! other = FixtureBuild.anotherSdk ()
      match other with
      | None -> failtest "this machine has one SDK, so the failure cannot be reproduced; install .NET SDK 10 beside 11, as the tool ships for both"
      | Some (otherSdkDirectory: string) ->
        let! fixture = FixtureBuild.create ()
        try
          let dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName otherSdkDirectory: string)
          let pinned =
            [ "MSBUILD_EXE_PATH", Path.Combine(otherSdkDirectory, "MSBuild.dll")
              "MSBuildSDKsPath", Path.Combine(otherSdkDirectory, "Sdks")
              "MSBuildExtensionsPath", otherSdkDirectory
              "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR", dotnetRoot ]
          let! stripped, strippedOutput = FixtureBuild.run fixture pinned true
          stripped |> Expect.equal (sprintf "built under its own SDK once applyTo dropped %s's pins: %s" otherSdkDirectory strippedOutput) 0
          let! pinnedExit, pinnedOutput = FixtureBuild.run fixture pinned false
          pinnedExit |> Expect.notEqual (sprintf "and under %s's MSBuild the same build fails: %s" otherSdkDirectory pinnedOutput) 0
        finally
          FixtureBuild.delete fixture
    }
  ]

/// A test must not put an MSBuild-resolution variable into the PROCESS environment. The shard's cases run in
/// parallel in one process, and every child any of them starts inherits what the process holds: with
/// MSBuildSDKsPath set to a bogus path for a moment, whichever case started a `dotnet build` in that moment
/// failed with no message (#185). Poison a child's own `ProcessStartInfo.Environment`, never the process.
[<Tests>]
let processEnvironmentHygieneTests =
  testList "Process environment in tests" [
    testCase "WHY - no test sets an MSBuild-resolution variable on the shared process, because every case beside it would inherit it into its own dotnet build" <| fun _ ->
      let root = RepoPaths.requireRepoRoot ()
      let setter = "SetEnvironment" + "Variable"
      let offenders =
        [ for file in Directory.EnumerateFiles(Path.Combine(root, "SageFs.Tests"), "*.fs", SearchOption.TopDirectoryOnly) do
            File.ReadAllLines file
            |> Array.indexed
            |> Array.filter (fun (_, line) ->
              not (line.TrimStart().StartsWith "//")
              && line.Contains setter
              && ProcessEnvironment.poisonedVariables |> List.exists (fun name -> line.Contains("\"" + name + "\"")))
            |> Array.map (fun (index, _) -> sprintf "%s:%d" (Path.GetFileName file) (index + 1))
            |> List.ofArray ]
        |> List.concat
      offenders |> Expect.isEmpty (sprintf "poison a child's ProcessStartInfo.Environment instead of the process: %s" (String.concat ", " offenders))

    // The other half of the same rule: a child must not INHERIT what a neighbouring case pinned. Every place a test
    // starts a process (`Process.Start psi`, or `new Process(StartInfo = psi)` then `.Start()`) is preceded, within a
    // few lines, by `SpawnEnvironment.apply psi` or `ProcessEnvironment.applyTo psi`. There is no allow-list: a git
    // child costs one line and a build child needs it, so no site has to be judged. A mock `new Process()` that is
    // never started is not a spawn and is not matched.
    testCase "WHY - every place a test starts a child process strips the MSBuild pins first, because a neighbouring case may have pinned another SDK's on the shared process" <| fun _ ->
      let root = RepoPaths.requireRepoRoot ()
      let spawns = [ "Process" + ".Start"; "new Process(" + "StartInfo" ]
      let strips = [ "SpawnEnvironment" + ".apply"; "ProcessEnvironment" + ".applyTo" ]
      let insideString (line: string) (index: int) =
        (line.Substring(0, index) |> Seq.filter ((=) '"') |> Seq.length) % 2 = 1
      // A whole word: `ownerProcess.StartTime` is not a spawn, and neither is `Process.StartInfo`.
      let dynamicWrite = System.Text.RegularExpressions.Regex(@"\.(Environment|EnvironmentVariables)\.?\[[A-Za-z_]")
      let isWordChar (c: char) = Char.IsLetterOrDigit c || c = '_'
      let wholeWord (line: string) (at: int) (needle: string) =
        (at = 0 || not (isWordChar line.[at - 1]))
        && (at + needle.Length >= line.Length || not (isWordChar line.[at + needle.Length]))
      let offenders =
        [ for file in Directory.EnumerateFiles(Path.Combine(root, "SageFs.Tests"), "*.fs", SearchOption.TopDirectoryOnly) do
            let lines = File.ReadAllLines file
            for index in 0 .. lines.Length - 1 do
              let line = lines.[index]
              let isSpawn =
                not (line.TrimStart().StartsWith "//")
                && spawns |> List.exists (fun needle ->
                  let at = line.IndexOf needle
                  at >= 0 && not (insideString line at) && wholeWord line at needle)
              if isSpawn then
                let first = max 0 (index - 12)
                let stripAt =
                  [ first .. index ]
                  |> List.tryFindBack (fun back -> strips |> List.exists (fun s -> lines.[back].Contains s))
                match stripAt with
                | None -> yield sprintf "%s:%d (no strip)" (Path.GetFileName file) (index + 1)
                | Some at ->
                  // Variables written under a name that is not a literal (a list copied onto the child) must come
                  // AFTER the strip: the strip removes the pins, and the worker's own DOTNET_MODIFIABLE_ASSEMBLIES is
                  // among what such a list carries. A strip placed after the copy removed it (the metadata-delta
                  // host never came up). The cases of this file poison a child on purpose first and are exempt.
                  let writesBefore =
                    [ first .. at - 1 ]
                    |> List.filter (fun back -> dynamicWrite.IsMatch lines.[back])
                  match writesBefore, Path.GetFileName file with
                  | _ :: _, name when name <> "ProcessEnvironmentTests.fs" ->
                    yield sprintf "%s:%d (variables copied onto the child at line %d, before the strip at line %d)" name (index + 1) (writesBefore.Head + 1) (at + 1)
                  | _ -> () ]
      offenders |> Expect.isEmpty (sprintf "call SpawnEnvironment.apply on the ProcessStartInfo before starting it: %s" (String.concat ", " offenders))
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant processEnvironmentHygieneTests |> ignore

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

      try
        // Both variables are process-wide, and Expecto runs this list's cases beside other lists, so they are set
        // and restored under the suite's one environment lock (`withEnvVar`), as every other test that sets one does.
        TestInfrastructure.withEnvVar ProcessEnvironment.forwardPrefixesEnvVar (Some "SAGEFS_DETERMINISM_") (fun () ->
          TestInfrastructure.withEnvVar "SAGEFS_DETERMINISM_POLICY" (Some "loss=40;latency=250ms") (fun () ->
            let psi = new ProcessStartInfo()
            psi.FileName <- "bash"
            psi.ArgumentList.Add(script)
            psi.UseShellExecute <- false
            psi.RedirectStandardOutput <- true
            // EXACTLY the call the worker spawn site makes (SessionManager.startWorkerProcess).
            ProcessEnvironment.applyToWithForwarding psi []

            use proc = Process.Start psi
            let out = proc.StandardOutput.ReadToEnd()
            proc.WaitForExit TestTimeouts.patience |> ignore
            out |> Expect.stringContains "the tool's variable reached the spawned process" "loss=40;latency=250ms"))
      finally
        try Directory.Delete(workDir, true) with _ -> ()
  ]

/// Session rebuilds and the pack died with MSB4223 three times in one session under load, and only went on once an agent
/// exported MSBUILDDISABLENODEREUSE=1 by hand. SageFs starts every one of these builds once and never reuses the node, so the
/// variable is part of what a build SageFs starts IS, set where the process is made.
[<Tests>]
let buildEnvironmentTests =
  testList "ProcessEnvironment.buildEnvironment" [

    testCase "WHY — the build environment turns MSBuild node reuse off, so a build under load cannot die on a node an earlier build left behind (MSB4223)" <| fun _ ->
      ProcessEnvironment.buildEnvironment
      |> Expect.contains "MSBUILDDISABLENODEREUSE=1 is in the build environment" ("MSBUILDDISABLENODEREUSE", "1")

    testCase "WHY — the ProcessStartInfo a session rebuild starts carries it, even when the daemon itself inherited a different value" <| fun _ ->
      TestInfrastructure.withEnvVar "MSBUILDDISABLENODEREUSE" (Some "0") (fun () ->
        let psi = SessionBuild.buildStartInfo [ "build"; "App.fsproj"; "--no-restore" ] (Path.GetTempPath())
        psi.Environment.["MSBUILDDISABLENODEREUSE"] |> Expect.equal "the rebuild's child sees node reuse off" "1"
        psi.ArgumentList |> Seq.toList |> Expect.equal "the arguments are the ones asked for" [ "build"; "App.fsproj"; "--no-restore" ]
        psi.FileName |> Expect.equal "it runs dotnet" "dotnet")

    testCase "WHY — the FSI host build's environment carries it beside the tiering knobs, because that build is a dotnet build too" <| fun _ ->
      let selection : FsiHostBuild.SdkSelection = { Version = "10.0.100"; DotnetRoot = None }
      FsiHostBuild.hostBuildEnvironment "dotnet" selection
      |> Expect.contains "node reuse is off for the host build" ("MSBUILDDISABLENODEREUSE", "1")
  ]
