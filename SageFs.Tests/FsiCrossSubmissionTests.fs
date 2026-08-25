module SageFs.Tests.FsiCrossSubmissionTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.McpTools
open SageFs.ProjectLoading
open SageFs.Tests.TestInfrastructure
open System.Collections.Concurrent

/// Dedicated FSI actor for cross-submission tests — isolated from globalActorResult
/// to avoid output contention with other integration tests.
let private dedicatedActor = lazy(
  let args = SageFs.ActorCreation.mkCommonActorArgs quietLogger false ignore SageFs.Args.ProjectLoadConfig.empty true
  SageFs.ActorCreation.createActor args |> Async.AwaitTask |> Async.RunSynchronously
)

/// Create an McpContext backed by the dedicated actor with a custom session ID.
let private isolatedCtx (sessionId: SageFs.WorkerProtocol.SessionId) =
  let result = dedicatedActor.Value
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- SageFs.WorkerProtocol.SessionId.value sessionId
  { FrictionStore = None
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = mkTestSessionOps result sessionId
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None } : McpContext

/// Unique ID per test invocation — prevents type name collisions
/// when --multiemit- puts all types in one assembly across re-runs.
let private nextUid () =
  System.Guid.NewGuid().ToString("N").[..7]

/// Eval two separate submissions in the SAME FSI session (tests cross-submission).
/// Uses a dedicated isolated actor to avoid contention with other tests.
let private evalPair code1 code2 =
  let uid = nextUid ()
  let ctx = isolatedCtx (SageFs.WorkerProtocol.SessionId.newId())
  let r1 =
    sendFSharpCode ctx "test" code1 OutputFormat.Text None None None None None None
    |> Async.AwaitTask |> Async.RunSynchronously
  let r2 =
    sendFSharpCode ctx "test" code2 OutputFormat.Text None None None None None None
    |> Async.AwaitTask |> Async.RunSynchronously
  r1, r2

/// Assert: result does not indicate a compilation error.
/// Checks for TypeLoadException and Error: prefix.
let private assertNoError msg (result: string) =
  let snippet = result.Substring(0, min 200 result.Length)
  result.Contains("TypeLoadException")
  |> Expect.isFalse (sprintf "%s: TypeLoadException in: %s" msg snippet)
  result.StartsWith("Error:")
  |> Expect.isFalse (sprintf "%s: error: %s" msg snippet)

let private propCfg =
  { FsCheckConfig.defaultConfig with maxTest = 5 }


/// All cross-submission tests in a single testSequenced block —
/// they share one FSI process (dedicatedActor), so must not run in parallel.
[<Tests>]
let allTests =
  testSequenced <| testList "FSI multiemit cross-submission" [

    // ── Unit tests: args correctness ──────────────────────────
    testList "args" [
      testCase "solutionToFsiArgs with hotReload=true includes --multiemit- at position 1" <| fun _ ->
        let args = solutionToFsiArgs quietLogger false true emptySolution
        args.[0] |> Expect.equal "first arg is fsi" "fsi"
        args.[1] |> Expect.equal "second arg is --multiemit-" "--multiemit-"

      testCase "solutionToFsiArgs with hotReload=true always contains --multiemit-"
      <| fun _ ->
        let args = solutionToFsiArgs quietLogger false true emptySolution
        args
        |> Array.exists (fun a -> a = "--multiemit-")
        |> Expect.isTrue "--multiemit- must be present when hotReload=true"

      testCase "--multiemit- appears exactly once with hotReload=true" <| fun _ ->
        let args = solutionToFsiArgs quietLogger false true emptySolution
        args
        |> Array.filter (fun a -> a = "--multiemit-")
        |> Array.length
        |> Expect.equal "exactly one --multiemit-" 1

      testCase "solutionToFsiArgs with hotReload=false includes --multiemit-" <| fun _ ->
        let args = solutionToFsiArgs quietLogger false false emptySolution
        args
        |> Array.exists (fun a -> a = "--multiemit-")
        |> Expect.isTrue "--multiemit- must always be present (even when hotReload=false) to enable cross-submission type+module pattern"

      testCase "solutionToFsiArgs with hotReload=false still starts with fsi" <| fun _ ->
        let args = solutionToFsiArgs quietLogger false false emptySolution
        args.[0] |> Expect.equal "first arg is fsi" "fsi"
    ]

    // ── Integration tests: cross-submission scenarios ─────────
    // These test that types/values defined in one FSI eval are usable
    // in a subsequent eval — the core behavior --multiemit- enables.
    // Assertions check for error ABSENCE only (not output format).
    testList "[Integration] type sharing" [
      ptestCase "record type from eval 1 can be instantiated in eval 2" <| fun _ ->
        let uid = nextUid ()
        let typeName = sprintf "Rec_%s" uid
        let r1, r2 = evalPair
                        (sprintf "type %s = { Name: string; Value: int };;" typeName)
                        (sprintf """let inst_%s = { Name = "hello"; Value = 42 };;""" uid)
        r1 |> assertNoError "define record"
        r2 |> assertNoError "instantiate record"

      testCase "record with-expression works across submissions" <| fun _ ->
        let uid = nextUid ()
        let typeName = sprintf "WithRec_%s" uid
        let r1, r2 = evalPair
                        (sprintf "type %s = { X: int; Y: string }\nlet orig_%s = { X = 1; Y = \"hello\" };;" typeName uid)
                        (sprintf "let mod_%s = { orig_%s with X = 99 };;" uid uid)
        r1 |> assertNoError "define + create original"
        r2 |> assertNoError "with-expression"

      testCase "DU from eval 1 can be pattern-matched in eval 2" <| fun _ ->
        let uid = nextUid ()
        let duName = sprintf "DU_%s" uid
        let r1, r2 = evalPair
                        (sprintf "type %s = | CaseA of int | CaseB of string;;" duName)
                        (sprintf """let matched_%s = match %s.CaseA 42 with | %s.CaseA n -> sprintf "got %%d" n | %s.CaseB s -> s;;""" uid duName duName duName)
        r1 |> assertNoError "define DU"
        r2 |> assertNoError "pattern match DU"

      testCase "anonymous record from eval 1 accessible in eval 2" <| fun _ ->
        let uid = nextUid ()
        let r1, r2 = evalPair
                        (sprintf """let anon_%s = {| Name = "test"; Count = 5 |};;""" uid)
                        (sprintf "let afield_%s = anon_%s.Name;;" uid uid)
        r1 |> assertNoError "create anon record"
        r2 |> assertNoError "access anon field"

      testCase "function from eval 1 callable in eval 2" <| fun _ ->
        let uid = nextUid ()
        let r1, r2 = evalPair
                        (sprintf "let add_%s a b = a + b;;" uid)
                        (sprintf "let sum_%s = add_%s 3 4;;" uid uid)
        r1 |> assertNoError "define function"
        r2 |> assertNoError "call function"
    ]

    // ── Property tests: parameterized cross-submission ────────
    testList "[Integration] properties" [
      ptestPropertyWithConfig propCfg
        "record with N fields (1-8) survives cross-submission"
      <| fun (fieldCount: PositiveInt) ->
        let n = min fieldCount.Get 8
        let uid = nextUid ()
        let typeName = sprintf "PRec_%s" uid
        let fields =
          [1..n] |> List.map (fun i -> sprintf "F%d: int" i) |> String.concat "; "
        let fieldVals =
          [1..n] |> List.map (fun i -> sprintf "F%d = %d" i (i * 10)) |> String.concat "; "
        let r1, r2 = evalPair
                        (sprintf "type %s = { %s };;" typeName fields)
                        (sprintf "let pr_%s = { %s };;" uid fieldVals)
        r1 |> assertNoError "define"
        r2 |> assertNoError "instantiate"

      testPropertyWithConfig propCfg
        "DU with N cases (2-8) survives cross-submission"
      <| fun (caseCount: PositiveInt) ->
        let n = max 2 (min caseCount.Get 8)
        let uid = nextUid ()
        let duName = sprintf "PDU_%s" uid
        let cases =
          [1..n] |> List.map (fun i -> sprintf "C%d of int" i) |> String.concat " | "
        let r1, r2 = evalPair
                        (sprintf "type %s = | %s;;" duName cases)
                        (sprintf "let pd_%s = match %s.C1 42 with | %s.C1 x -> x | _ -> 0;;" uid duName duName)
        r1 |> assertNoError "DU define"
        r2 |> assertNoError "DU match"

      testPropertyWithConfig propCfg
        "let binding of any int survives cross-submission"
      <| fun (value: int) ->
        let uid = nextUid ()
        let r1, r2 = evalPair
                        (sprintf "let pval_%s = %d;;" uid value)
                        (sprintf "let pres_%s = pval_%s + 1;;" uid uid)
        r1 |> assertNoError "define"
        r2 |> assertNoError "use"

      testPropertyWithConfig propCfg
        "anonymous record with N fields (1-8) survives cross-submission"
      <| fun (fieldCount: PositiveInt) ->
        let n = min fieldCount.Get 8
        let uid = nextUid ()
        let fields =
          [1..n] |> List.map (fun i -> sprintf "A%d = %d" i i) |> String.concat "; "
        let r1, r2 = evalPair
                        (sprintf "let par_%s = {| %s |};;" uid fields)
                        (sprintf "let paf_%s = par_%s.A1;;" uid uid)
        r1 |> assertNoError "anon define"
        r2 |> assertNoError "anon access"

      testPropertyWithConfig propCfg
        "with-expression preserves unmodified fields across submissions"
      <| fun (x: int) (y: int) ->
        let uid = nextUid ()
        let typeName = sprintf "PWith_%s" uid
        let r1, r2 = evalPair
                        (sprintf "type %s = { X: int; Y: int }\nlet pw_%s = { X = %d; Y = %d };;" typeName uid x y)
                        (sprintf "let pwm_%s = { pw_%s with X = %d };;" uid uid (x + 1))
        r1 |> assertNoError "define"
        r2 |> assertNoError "with-expr"
    ]
  ]
