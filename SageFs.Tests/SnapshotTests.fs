module SageFs.Tests.SnapshotTests

open Expecto
open VerifyExpecto
open VerifyTests
open System.IO
open SageFs
open SageFs.AppState

let snapshotsDir = Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

let verifyText name (value: string) =
  let settings = VerifySettings()
  settings.UseDirectory(snapshotsDir)
  Verifier.Verify(name, value, "txt", settings).ToTask()

[<Tests>]
let formatTests =
  testList "Snapshot tests" [

    testTask "formatStatus snapshot" {
      let result = McpAdapter.formatStatus "test-session" 42 SageFs.SessionState.Ready None
      do! verifyText "formatStatus" result
    }

    testTask "formatStartupInfo snapshot" {
      let config: StartupConfig = {
        CommandLineArgs = [| "--proj"; "Test.fsproj" |]
        LoadedProjects = [ "Test.fsproj" ]
        WorkingDirectory = "/code/test"
        McpPort = 8080
        HotReloadEnabled = true
        AspireDetected = false
        StartupTimestamp = System.DateTime(2025, 1, 1, 0, 0, 0, System.DateTimeKind.Utc); StartupProfileLoaded = None
      }
      let result = McpAdapter.formatStartupInfo config
      do! verifyText "formatStartupInfo" result
    }

    testTask "formatEnhancedStatus snapshot" {
      let config: StartupConfig = {
        CommandLineArgs = [| "--proj"; "Test.fsproj" |]
        LoadedProjects = [ "Test.fsproj" ]
        WorkingDirectory = "/code/test"
        McpPort = 8080
        HotReloadEnabled = true
        AspireDetected = false
        StartupTimestamp = System.DateTime(2025, 1, 1, 0, 0, 0, System.DateTimeKind.Utc); StartupProfileLoaded = None
      }
      let result = McpAdapter.formatEnhancedStatus "test-session" 10 SageFs.SessionState.Ready None (Some config)
      do! verifyText "formatEnhancedStatus" result
    }

    testTask "echoStatement single snapshot" {
      let sw = new StringWriter()
      McpAdapter.echoStatement sw "let x = 42;;"
      do! verifyText "echoStatement_single" (sw.ToString())
    }

    testTask "echoStatement multi snapshot" {
      let sw = new StringWriter()
      McpAdapter.echoStatement sw "let x = 1;;"
      McpAdapter.echoStatement sw "let y = 2;;"
      McpAdapter.echoStatement sw "printfn \"%d\" (x + y);;"
      do! verifyText "echoStatement_multi" (sw.ToString())
    }

    testTask "echoStatement multiline body snapshot" {
      let sw = new StringWriter()
      McpAdapter.echoStatement sw "type Dog = {\n  Name: string\n  Breed: string\n};;"
      do! verifyText "echoStatement_multiline" (sw.ToString())
    }

    testTask "echoStatement deeply nested snapshot" {
      let sw = new StringWriter()
      let code = String.concat "\n" [
        "let processResults (items: Item list) ="
        "  items"
        "  |> List.groupBy (fun i -> i.Category)"
        "  |> List.map (fun (cat, grouped) ->"
        "    let summary ="
        "      grouped"
        "      |> List.fold (fun acc item ->"
        "        match item.Status with"
        "        | Active ->"
        "          { acc with"
        "              Count = acc.Count + 1"
        "              Total = acc.Total + item.Value }"
        "        | Inactive reason ->"
        "          { acc with"
        "              Skipped ="
        "                acc.Skipped"
        "                |> Map.add item.Id reason }"
        "        | Pending approval ->"
        "          let nested ="
        "            approval"
        "            |> List.filter (fun a ->"
        "              a.Level > 2"
        "              && a.Department = cat)"
        "          { acc with"
        "              Waiting = acc.Waiting @ nested }"
        "      ) emptySummary"
        "    cat, summary)"
        ";;"
      ]
      McpAdapter.echoStatement sw code
      do! verifyText "echoStatement_deeplyNested" (sw.ToString())
    }

    testTask "formatEvalResult success snapshot" {
      let response: EvalResponse = {
        EvaluationResult = Ok "val x: int = 42"
        Diagnostics = [||]
        EvaluatedCode = "let x = 42;;"
        Metadata = Map.empty
      }
      do! verifyText "formatEvalResult_success" (McpAdapter.formatEvalResult response)
    }

    testTask "formatEvalResult error with diagnostics snapshot" {
      let response: EvalResponse = {
        EvaluationResult = Error (System.Exception("Operation could not be completed due to earlier error"))
        Diagnostics = [|
          { Message = "The value or constructor 'nonExistent' is not defined."
            Subcategory = "typecheck"
            Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 11 }
            Severity = Features.Diagnostics.DiagnosticSeverity.Error }
        |]
        EvaluatedCode = "let x = nonExistent();;"
        Metadata = Map.empty
      }
      do! verifyText "formatEvalResult_error" (McpAdapter.formatEvalResult response)
    }

    testTask "formatEvalResult success with stdout snapshot" {
      let response: EvalResponse = {
        EvaluationResult = Ok "val it: unit = ()"
        Diagnostics = [||]
        EvaluatedCode = "printfn \"hello\";;"
        Metadata = Map.ofList [ "stdout", box "hello\n" ]
      }
      do! verifyText "formatEvalResult_stdout" (McpAdapter.formatEvalResult response)
    }
  ]
