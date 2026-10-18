module SageFs.Tests.PackagedToolSmokeWiringTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs.Tests.TestInfrastructure

/// The packed tool is what users install, and every past install failure (missing SageFs.Host.dll, the tool exe
/// dropped, flat-dir pollution, the net11-only layout of #131) was "installs fine, `--version` works, a session
/// cannot start". `--version` alone proves the first half. `scripts/smoke-test.fsx` starts the installed tool's
/// daemon, opens a session on a sample and asks it real questions, but it was wired to nothing: a script nobody
/// runs is not a gate.
///
/// It cannot be a ledgered tier. The trust ledger's rows are written by the Expecto process
/// (`TrustSignal.run`: registered, ran, verdict), and the `trust report` stage judges them before
/// `pack release bundle` has produced the nupkg the smoke installs. So it is a stage whose failure fails the
/// pipeline, and these cases are what keep the stage from quietly losing its teeth.
let packagedToolSmokeWiringTests =
  let repoRoot = RepoPaths.repoPathFull [||]
  let pipeline = lazy (File.ReadAllText(Path.Combine(repoRoot, "ci-pipeline.fsx")))

  /// The text of the "packaged tool smoke" stage, from its declaration to the next stage (or the end of the file).
  let smokeStage () =
    let text = pipeline.Value
    match text.IndexOf "stage \"packaged tool smoke\"" with
    | -1 -> ""
    | start ->
      let tail = text.Substring start
      match tail.IndexOf("\n  stage \"", 1) with
      | -1 -> tail
      | next -> tail.Substring(0, next)

  /// The part of the stage that runs once per SDK: the body of `smokeOneSdk`, up to where the per-SDK runs are joined.
  let perSdkBody () =
    let stage = smokeStage ()
    match stage.IndexOf "let smokeOneSdk", stage.IndexOf "Async.Sequential" with
    | -1, _ | _, -1 -> ""
    | first, last when last > first -> stage.Substring(first, last - first)
    | _ -> ""

  testList "Packaged tool smoke wiring" [
    testCase "the stage exists, and the script it runs is on disk" <| fun _ ->
      smokeStage () |> Expect.isNotEmpty "ci-pipeline.fsx declares a stage named \"packaged tool smoke\""
      File.Exists(Path.Combine(repoRoot, "scripts", "smoke-test.fsx"))
      |> Expect.isTrue "scripts/smoke-test.fsx exists, so the stage has something to run"

    testCase "the stage runs scripts/smoke-test.fsx once per SDK it installs the tool under" <| fun _ ->
      smokeStage ()
      |> Expect.stringContains "the stage names the script" "smoke-test.fsx"
      perSdkBody ()
      |> Expect.stringContains "each SDK's install is followed by the session smoke against that install" "sessionSmoke"
      smokeStage ()
      |> Expect.stringContains "every SDK in the list goes through that per-SDK body" "List.map smokeOneSdk"

    testCase "each run has its own port, never the owner's daemon port, and its own data dir" <| fun _ ->
      let stage = smokeStage ()
      stage |> Expect.stringContains "the smoke is told its port explicitly (its own default is the owner's daemon port)" "--port"
      stage |> Expect.stringContains "a free port pair is looked up per run" "freePortPair"
      stage |> Expect.stringContains "the daemon's state is a data dir of its own, never ~/.SageFs" "SAGEFS_DATA_DIR"
      stage.Contains "37749"
      |> Expect.isFalse "37749 is the owner's daemon; the stage must not name it"

    testCase "a red run leaves its evidence: the smoke is given a diagnostics directory" <| fun _ ->
      smokeStage ()
      |> Expect.stringContains "the script writes what it saw there when a step fails" "--diagnostics-dir"

    testCase "the smoke runs against the install of the nupkg the gate packed, not a global tool" <| fun _ ->
      let stage = smokeStage ()
      stage |> Expect.stringContains "the throwaway --tool-path install is put first on the smoke's PATH" "toolPath + string Path.PathSeparator"
      stage |> Expect.stringContains "the script resolves `sagefs` from PATH" "\"PATH\""

    testCase "a failing or silent smoke fails the stage" <| fun _ ->
      let stage = smokeStage ()
      Regex.IsMatch(stage, "match\\s+smokeExit\\s+with[\\s\\S]*return Error")
      |> Expect.isTrue "a non-zero exit from the script becomes an Error for the stage"
      stage
      |> Expect.stringContains "an exit 0 without the script's own PASS line is not a pass (a script that never ran must not go green)" "RESULT: PASS"

    testCase "a missing script fails loudly instead of skipping the smoke" <| fun _ ->
      Regex.IsMatch(smokeStage (), "File\\.Exists smokeScript[\\s\\S]*Error")
      |> Expect.isTrue "the stage checks its prerequisite and returns an Error, never a green no-op"
  ]

[<Tests>]
let tests =
  let ratchet (test: Test) = TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant test
  ratchet packagedToolSmokeWiringTests
