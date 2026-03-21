module SageFs.Tests.ErrorAlgebraHardeningTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Tests.SharedGenerators

// ── Helpers ──

/// Build one instance of every SageFsError DU case via reflection,
/// returning the UnionCaseInfo alongside each instance.
let private buildAllCases () =
  FSharpType.GetUnionCases(typeof<SageFsError>)
  |> Array.map (fun case ->
    let fields =
      case.GetFields()
      |> Array.map (fun f ->
        match f.PropertyType with
        | t when t = typeof<string> -> box "test"
        | t when t = typeof<int> -> box 42
        | t when t = typeof<float> -> box 1.0
        | t when t = typeof<exn> -> box (Exception "test")
        | t when t = typeof<string list> -> box ([ "a"; "b" ] : string list)
        | t when t = typeof<SessionState> -> box SessionState.Ready
        | _ -> failwithf "Unhandled field type %s in case %s" f.PropertyType.Name case.Name)
    case, FSharpValue.MakeUnion(case, fields) :?> SageFsError)

// ── FsCheck generator (local copy to avoid coupling to other test files) ──

let private genStr =
  Gen.elements [ 'a' .. 'z' ]
  |> Gen.listOfLength 8
  |> Gen.map (fun cs -> String(cs |> List.toArray))

let private genStrList = genStr |> Gen.listOfLength 3

let private genSessState =
  Gen.elements [
    SessionState.Uninitialized
    SessionState.WarmingUp
    SessionState.Ready
    SessionState.Evaluating
    SessionState.Faulted
  ]

let private genError =
  Gen.oneof [
    gen {
      let! tool = genStr
      let! state = genSessState
      let! tools = genStrList
      return SageFsError.ToolNotAvailable(tool, state, tools)
    }
    genStr |> Gen.map SageFsError.SessionNotFound
    Gen.constant SageFsError.NoActiveSessions
    genStrList |> Gen.map SageFsError.AmbiguousSessions
    genStr |> Gen.map SageFsError.SessionCreationFailed
    gen {
      let! s = genStr
      let! r = genStr
      return SageFsError.SessionStopFailed(s, r)
    }
    gen {
      let! s = genStr
      let! r = genStr
      return SageFsError.SessionSwitchFailed(s, r)
    }
    gen {
      let! s = genStr
      let! r = genStr
      return SageFsError.WorkerCommunicationFailed(s, r)
    }
    genStr |> Gen.map SageFsError.WorkerSpawnFailed
    gen {
      let! s = genStr
      let! op = genStr
      let! t = Gen.elements [ 1.0; 5.0; 30.0 ]
      return SageFsError.WorkerTimeout(s, op, t)
    }
    gen {
      let! s = genStr
      let! ep = genStr
      let! code = Gen.choose (400, 599)
      return SageFsError.WorkerHttpError(s, ep, code)
    }
    Gen.constant SageFsError.PipeClosed
    genStr |> Gen.map SageFsError.EvalFailed
    genStr |> Gen.map SageFsError.ResetFailed
    genStr |> Gen.map SageFsError.HardResetFailed
    genStr |> Gen.map SageFsError.ScriptLoadFailed
    genStr |> Gen.map SageFsError.CheckFailed
    gen {
      let! s = genStr
      let! r = genStr
      return SageFsError.CompletionFailed(s, r)
    }
    genStr |> Gen.map SageFsError.CancelFailed
    gen {
      let! n = genStr
      let! r = genStr
      return SageFsError.WarmupOpenFailed(n, r)
    }
    gen {
      let! s = genStr
      let! r = genStr
      return SageFsError.WarmupContextFailed(s, r)
    }
    gen {
      let! p = genStr
      let! r = genStr
      return SageFsError.HotReloadFailed(p, r)
    }
    gen {
      let! s = genStr
      let! r = genStr
      return SageFsError.HotReloadStateError(s, r)
    }
    gen {
      let! c = Gen.choose (1, 10)
      let! w = Gen.elements [ 1.0; 5.0; 60.0 ]
      return SageFsError.RestartLimitExceeded(c, w)
    }
    genStr |> Gen.map SageFsError.DaemonStartFailed
    Gen.constant SageFsError.DaemonNotRunning
    Gen.choose (1024, 65535) |> Gen.map SageFsError.PortInUse
    genStr |> Gen.map SageFsError.SseConnectionError
    gen {
      let! ctx = genStr
      let! r = genStr
      return SageFsError.JsonParseError(ctx, r)
    }
    Gen.constant (SageFsError.Unexpected(Exception "test"))
  ]

let private pick g = (Gen.sample 1 g).[0]

// ── Tests ──

[<Tests>]
let errorAlgebraHardeningTests =
  testList "error algebra hardening" [

    // ── Group 1: Result<_,string> regression guard ──
    testList "Result<_,string> regression guard" [
      test "source count of Result<_,string> does not grow beyond snapshot" {
        let coreDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Core")
        let fsFiles = Directory.GetFiles(coreDir, "*.fs", SearchOption.AllDirectories)
        let regex = Regex @"Result<[^,]+,\s*string\s*>"
        let matches =
          fsFiles
          |> Array.collect (fun file ->
            let lines = File.ReadAllLines(file)
            lines
            |> Array.indexed
            |> Array.filter (fun (_, line) -> regex.IsMatch line)
            |> Array.map (fun (i, line) ->
              let rel = Path.GetRelativePath(coreDir, file)
              sprintf "  %s:%d  %s" rel (i + 1) (line.Trim())))
        let count = matches.Length
        if count > 0 then
          printfn "Result<_,string> occurrences (%d):\n%s" count (matches |> String.concat "\n")
        // Snapshot: 62 occurrences as of this writing.
        // +3 from persistence parser failwith→Result refactor (Wave 1 audit).
        // +1 from RequestRebuild/RebuildCompleted in LiveTestingTypes.fs.
        // If this fails upward, migrate new uses to Result<_,SageFsError>.
        (count <= 62)
        |> Expect.isTrue
          (sprintf "Result<_,string> grew to %d — migrate new uses to Result<_,SageFsError>" count)
      }
    ]

    // ── Group 2: SageFsError exhaustiveness proof ──
    testList "SageFsError exhaustiveness" [
      let cases = buildAllCases ()

      yield!
        cases
        |> Array.map (fun (info, err) ->
          test (sprintf "describe non-empty for %s" info.Name) {
            SageFsError.describe err
            |> String.IsNullOrWhiteSpace
            |> Expect.isFalse (sprintf "describe empty for %s" info.Name)
          })

      yield!
        cases
        |> Array.map (fun (info, err) ->
          test (sprintf "toLogLevel valid for %s" info.Name) {
            let v = int (SageFsError.toLogLevel err)
            (v >= 0 && v <= 6)
            |> Expect.isTrue (sprintf "toLogLevel out of range for %s: %d" info.Name v)
          })

      yield!
        cases
        |> Array.map (fun (info, err) ->
          test (sprintf "toHttpStatus in 100-599 for %s" info.Name) {
            let s = SageFsError.toHttpStatus err
            (s >= 100 && s <= 599)
            |> Expect.isTrue (sprintf "toHttpStatus out of range for %s: %d" info.Name s)
          })

      yield!
        cases
        |> Array.map (fun (info, err) ->
          test (sprintf "suggestedAction non-empty for %s" info.Name) {
            SageFsError.suggestedAction err
            |> String.IsNullOrWhiteSpace
            |> Expect.isFalse (sprintf "suggestedAction empty for %s" info.Name)
          })
    ]

    // ── Group 3: Error algebra properties (FsCheck) ──
    testList "error algebra properties" [
      testPropertyWithConfig propConfig "describe returns meaningful message (>= 10 chars)" <|
        fun () ->
          let err = pick genError
          let desc = SageFsError.describe err
          not (String.IsNullOrWhiteSpace desc) && desc.Length >= 10

      testPropertyWithConfig propConfig "toHttpStatus is idempotent" <|
        fun () ->
          let err = pick genError
          SageFsError.toHttpStatus err = SageFsError.toHttpStatus err

      testPropertyWithConfig propConfig "classification is mutually exclusive and exhaustive" <|
        fun () ->
          let err = pick genError
          let trueCount =
            [ SageFsError.isClientError err
              SageFsError.isServerError err
              SageFsError.isGatewayError err
              SageFsError.isInfraError err ]
            |> List.filter id
            |> List.length
          trueCount = 1

      testPropertyWithConfig propConfig "toJson returns valid case name for every variant" <|
        fun () ->
          let err = pick genError
          let json = SageFsError.toJson err
          not (String.IsNullOrWhiteSpace json.case) && json.case.Length > 0
    ]

    // ── Group 4: Error composition ──
    testList "error composition" [
      test "toJson roundtrip preserves case name for every variant" {
        buildAllCases ()
        |> Array.iter (fun (info, err) ->
          let json = SageFsError.toJson err
          json.case
          |> Expect.equal (sprintf "case roundtrip for %s" info.Name) info.Name)
      }

      test "error messages contain no sensitive info patterns" {
        let sensitive = [
          Regex(@"C:\\Users\\[^\\]+", RegexOptions.IgnoreCase)
          Regex(@"/home/[^/]+", RegexOptions.IgnoreCase)
          Regex @"[Cc]onnection[Ss]tring\s*="
          Regex @"[Pp]assword\s*="
        ]
        buildAllCases ()
        |> Array.iter (fun (info, err) ->
          let desc = SageFsError.describe err
          let action = SageFsError.suggestedAction err
          for pat in sensitive do
            pat.IsMatch desc
            |> Expect.isFalse
              (sprintf "describe for %s matches sensitive pattern %s" info.Name (pat.ToString()))
            pat.IsMatch action
            |> Expect.isFalse
              (sprintf "suggestedAction for %s matches sensitive pattern %s" info.Name (pat.ToString())))
      }

      test "toJson fields preserve typed values" {
        let err = SageFsError.WorkerTimeout("sess-1", "eval", 5.0)
        let json = SageFsError.toJson err
        json.fields.["sessionId"] :?> string
        |> Expect.equal "sessionId" "sess-1"
        json.fields.["operation"] :?> string
        |> Expect.equal "operation" "eval"
        json.fields.["timeoutSec"] :?> float
        |> Expect.equal "timeoutSec" 5.0
      }
    ]
  ]
