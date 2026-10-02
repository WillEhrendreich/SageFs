/// The smoke test's decisions (scripts/SmokeRules.fs, linked into this project): how its arguments read and what
/// each check's evidence means. scripts/smoke-test.fsx only talks to a daemon around these functions.
module SageFs.Tests.SmokeRulesTests

open Expecto
open Expecto.Flip
open SmokeRules

let private isPass = function | Pass _ -> true | Fail _ -> false

[<Tests>]
let tests =
  testList "smoke rules" [

    testCase "WHY — no arguments means the defaults: the from-csharp sample, the shared port and a smoke-diagnostics directory" <| fun _ ->
      parseArgs [] |> Expect.equal "defaults" (Ok defaults)
      defaults.Port |> Expect.equal "the daemon's own port" 37749
      defaults.Sample |> Expect.stringContains "a small sample, not the big test project" "SageFs.Samples.FromCSharp"

    testCase "WHY — every option reads its value, in any order" <| fun _ ->
      let daemonWait, warmupWait, port = 5, 9, 38000
      parseArgs [ "--port"; string port; "--sample"; "x/y.fsproj"; "--daemon-timeout"; string daemonWait; "--session-warmup"; string warmupWait; "--diagnostics-dir"; "out" ]
      |> Expect.equal "all five" (Ok { Sample = "x/y.fsproj"; DaemonTimeoutSeconds = daemonWait; SessionWarmupSeconds = warmupWait; Port = port; DiagnosticsDir = "out" })

    testCase "WHY — a bad argument is named, never ignored" <| fun _ ->
      parseArgs [ "--prot"; "1" ] |> Expect.equal "unknown" (Error(UnknownOption "--prot"))
      parseArgs [ "--port" ] |> Expect.equal "missing value" (Error(MissingValue "--port"))
      parseArgs [ "--port"; "many" ] |> Expect.equal "not a number" (Error(NotAWholeNumber("--port", "many")))

    testCase "WHY — .NET 10 passes and older fails, whatever suffix the SDK prints" <| fun _ ->
      isPass (dotnetVerdict "11.0.100-rc.1.26425.128\n") |> Expect.isTrue "11 is fine"
      isPass (dotnetVerdict "10.0.100") |> Expect.isTrue "10 is fine"
      isPass (dotnetVerdict "9.0.300") |> Expect.isFalse "9 is too old"
      isPass (dotnetVerdict "not a version") |> Expect.isFalse "unreadable is a failure"

    testCase "WHY — discovery polling stops at any real answer, and for an older daemon only once tests show up" <| fun _ ->
      for state in [ ReadyWithTests; ReadyZeroTests; Disabled ] do
        discoveryFinished state 0 |> Expect.isTrue (sprintf "%A is an answer" state)
      discoveryFinished Discovering 5 |> Expect.isFalse "still discovering"
      discoveryFinished Unreported 0 |> Expect.isFalse "an older daemon with no tests yet"
      discoveryFinished Unreported 3 |> Expect.isTrue "an older daemon with tests"
      discoveryFinished (Unrecognised "weird") 3 |> Expect.isFalse "a state nobody knows is not an answer"

    testCase "WHY — the discovery state text round-trips for every state the daemon reports" <| fun _ ->
      for text in [ "ready_with_tests"; "ready_zero_tests"; "disabled"; "discovering" ] do
        parseDiscoveryState (Some text) |> discoveryStateText |> Expect.equal "round trip" text
      parseDiscoveryState None |> Expect.equal "silence" Unreported

    testCase "WHY — each discovery outcome is a verdict with its own words" <| fun _ ->
      discoveryVerdict ReadyWithTests 5 4 "" "x" |> Expect.equal "tests found" (Pass "Discovery completed with 5 tests after 4s")
      isPass (discoveryVerdict ReadyZeroTests 0 4 "hint" "x") |> Expect.isFalse "zero tests fails"
      isPass (discoveryVerdict Disabled 0 4 "hint" "x") |> Expect.isFalse "still disabled fails"
      isPass (discoveryVerdict Discovering 0 30 "hint" "x") |> Expect.isFalse "never finished fails"
      isPass (discoveryVerdict Unreported 2 30 "" "x") |> Expect.isTrue "an older daemon that showed tests passes"
      isPass (discoveryVerdict Unreported 0 30 "" "x") |> Expect.isFalse "an older daemon with none fails"
      discoveryVerdict ReadyZeroTests 0 4 "the hint" "state=ready_zero_tests"
      |> verdictMessage
      |> Expect.equal "the message carries hint and latest" "Discovery completed with zero tests after 4s. the hint Latest: state=ready_zero_tests"

    testCase "WHY — the live-testing summary names the state, the hint and every count the status carried" <| fun _ ->
      liveTestingSummary None |> Expect.equal "none" "no live-testing status available"
      liveTestingSummary (Some { State = None; Hint = None; Counts = [] }) |> Expect.equal "empty" "live-testing status had no summary fields"
      liveTestingSummary (Some { State = Some "ready_with_tests"; Hint = Some "  "; Counts = [ "total", 5; "passed", 4 ] })
      |> Expect.equal "blank hint dropped" "state=ready_with_tests, total=5, passed=4"

    testCase "WHY — the session summary reads project=status, or says there are none" <| fun _ ->
      sessionStatusSummary [] |> Expect.equal "none" "no sessions reported"
      sessionStatusSummary [ { Label = "Sample.fsproj"; Status = "Ready" }; { Label = "Other"; Status = "Faulted" } ]
      |> Expect.equal "two" "Sample.fsproj=Ready, Other=Faulted"

    testCase "WHY — warmup ends on the first Ready session, gives up early only when there are sessions and all faulted, and otherwise waits" <| fun _ ->
      warmupLook [ "a", "Starting"; "b", "Ready" ] |> Expect.equal "ready" (SessionReady "b")
      warmupLook [ "a", "Faulted"; "b", "Faulted" ] |> Expect.equal "all faulted" AllFaulted
      warmupLook [ "a", "Faulted"; "b", "Starting" ] |> Expect.equal "one still starting" KeepWaiting
      warmupLook [] |> Expect.equal "no sessions yet is not a fault" KeepWaiting

    testCase "WHY — completions: items pass, an empty list fails, text passes unless it starts with Error:" <| fun _ ->
      completionsVerdict (ItemArray 3) |> Expect.equal "array" (Pass "Completions returned 3 items")
      completionsVerdict (CountedObject 2) |> Expect.equal "object" (Pass "Completions returned 2 items")
      completionsVerdict (Text "some results") |> Expect.equal "text" (Pass "Completions returned results")
      isPass (completionsVerdict (ItemArray 0)) |> Expect.isFalse "empty array"
      isPass (completionsVerdict (CountedObject 0)) |> Expect.isFalse "zero count"
      completionsVerdict (Text "Error: no session") |> Expect.equal "error text" (Fail "Completions returned unexpected: Error: no session")
      isPass (completionsVerdict (Unexpected "{}")) |> Expect.isFalse "some other shape"

    testCase "WHY — every step has its own name, so the summary and diagnostics can tell them apart" <| fun _ ->
      let steps = [ Dotnet; SagefsOnPath; DaemonStart; ApiVersion; Completions; TestsDiscovery; RunTests; MarkAllStale ]
      steps |> List.map stepName |> List.distinct |> List.length |> Expect.equal "no two steps share a name" steps.Length
      stepName SagefsOnPath |> Expect.equal "the name the old script printed" "sagefs-path"
  ]
