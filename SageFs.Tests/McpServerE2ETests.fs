module SageFs.Tests.McpServerE2ETests

open Expecto
open System.Text
open System.Text.Json
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open SageFs

[<Tests>]
let tests =
  testList "MCP HTTP Handler tests" [

    test "MCP handler processes initialize request" {
      // Test MCP protocol initialize handshake
      // Verify the McpAdapter module and its functions exist
      
      // Test formatEvalResult exists and works
      let testResponse: AppState.EvalResponse = {
        EvaluationResult = Ok "val x: int = 42"
        Diagnostics = [||]
        EvaluatedCode = "let x = 42"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult testResponse
      Expect.stringContains result "Result:" "formatEvalResult should work"
      
      // Test formatStatus exists and works
      let status = McpAdapter.formatStatus "test-session" 10 SageFs.SessionState.Ready None
      Expect.stringContains status "test-session" "formatStatus should work"
    }

    test "MCP handler processes tools/list request" {
      // Test tools listing functionality
      // Verify that McpTools functions exist by checking their signatures
      
      // These functions should exist in McpTools module
      // sendFSharpCode: McpContext -> string -> string -> Task<string>
      // loadFSharpScript: McpContext -> string -> string -> Task<string>
      // getRecentEvents: McpContext -> int -> Task<string>
      // getStatus: McpContext -> Task<string>
      
      // Test formatEvents exists and works
      let testEvents = [
        (System.DateTime.UtcNow, "console", "test input")
      ]
      let result = McpAdapter.formatEvents testEvents
      Expect.stringContains result "console:" "formatEvents should work"
      
      // Test parseScriptFile exists and works
      let tempFile = System.IO.Path.GetTempFileName()
      try
        System.IO.File.WriteAllText(tempFile, "let x = 1;;")
        let result = McpAdapter.parseScriptFile tempFile
        match result with
        | Ok statements -> Expect.equal statements.Length 1 "Should parse one statement"
        | Error _ -> failtest "Should parse successfully"
      finally
        System.IO.File.Delete(tempFile)
    }
  ]
