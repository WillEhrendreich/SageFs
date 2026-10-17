/// What an agent and an editor read after a plain eval. A call that named no file asked for no
/// write-back, so its text is its own output and nothing after it: the "not persisted" line is
/// information only for a caller that supplied `file_path`. The verdict stays a field of the
/// structured result for callers that read it. Driven through the real tool member, reading the
/// text an agent reads, the way `ReplFreshnessSurfaceTests` does.
module SageFs.Tests.EvalWriteBackSurfaceTests

open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.Server.McpTools
open SageFs.Tests.TestInfrastructure

let private textOf (result: ModelContextProtocol.Protocol.CallToolResult) : string =
  result.Content
  |> Seq.tryPick (fun c -> match c with :? ModelContextProtocol.Protocol.TextContentBlock as t -> Some t.Text | _ -> None)
  |> Option.defaultValue ""

[<Tests>]
let tests =
  // Sequenced for the same reason ReplFreshnessSurfaceTests is: these drive the one shared FSI actor.
  testSequenced <| testList "Eval write-back on the surface an agent reads" [

    testTask "WHY - an eval with no file_path returns its own output and nothing after it" {
      let tools = SageFsTools(sharedCtx (), NullLogger<SageFsTools>.Instance)
      let! (result: ModelContextProtocol.Protocol.CallToolResult) = tools.send_fsharp_code("test", "let writeBackSurfaceProbe = 41 + 1", "", "", "", 0, "")
      let text = textOf result
      text |> Expect.stringContains "the eval's own output is there" "writeBackSurfaceProbe"
      text.Contains "not persisted" |> Expect.isFalse "no write was asked for, so no verdict line follows the output"
      text.Contains "file_path" |> Expect.isFalse "and no advice to pass an argument the caller did not need"
      // The verdict is still data for a caller that reads structured output.
      result.StructuredContent.Value.GetProperty("writeBack").GetString()
      |> Expect.stringContains "the structured result still carries the verdict" "not persisted"
    }

    testTask "WHY - an eval that named a file keeps its verdict line, because the caller asked for a write" {
      let tools = SageFsTools(sharedCtx (), NullLogger<SageFsTools>.Instance)
      // A file but no block_start_line: the write was asked for and refused, and the caller is told why.
      let! (result: ModelContextProtocol.Protocol.CallToolResult) = tools.send_fsharp_code("test", "let writeBackSurfaceNamed = 1", "", "/tmp/WriteBackSurfaceProbe.fs", "", 0, "")
      let text = textOf result
      text |> Expect.stringContains "the refusal is said" "not persisted"
      text |> Expect.stringContains "and it names the argument to pass" "block_start_line"
    }
  ]
