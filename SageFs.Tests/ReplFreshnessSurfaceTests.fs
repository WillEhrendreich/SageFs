/// What an agent reads when the REPL is behind the app. A session whose app was patched in place by a metadata delta runs the new
/// code in the app and the old build in the REPL and in live tests. The state is `ReplFreshness`, and every surface shows it:
/// get_session_status, list_sessions, and the result of every send_fsharp_code, check_fsharp_code and run_tests call. Each of those has
/// a test here, through the real tool member, reading the text an agent reads.
module SageFs.Tests.ReplFreshnessSurfaceTests

open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.McpTools
open SageFs.Server.McpTools
open SageFs.Tests.TestInfrastructure

let private behind = ReplFreshness.BehindApp (2, [ "Handlers.describe"; "Handlers.makeHeld" ])

/// The shared context, with a session whose freshness is `freshness`.
let private ctxWith (freshness: ReplFreshness) : McpContext =
  let ctx = sharedCtx ()
  let ops = ctx.SessionOps
  let withFreshness (info: WorkerProtocol.SessionInfo) = { info with Freshness = freshness }
  { ctx with
      SessionOps =
        { ops with
            GetSessionInfo = fun id -> task { let! info = ops.GetSessionInfo id in return Option.map withFreshness info }
            GetAllSessions = fun () ->
              task {
                let! one = ops.GetSessionInfo (WorkerProtocol.SessionId.newId ())
                return one |> Option.map withFreshness |> Option.toList
              } }
  }

let private textOf (result: ModelContextProtocol.Protocol.CallToolResult) : string =
  result.Content
  |> Seq.tryPick (fun c -> match c with :? ModelContextProtocol.Protocol.TextContentBlock as t -> Some t.Text | _ -> None)
  |> Option.defaultValue ""

let private warningWords (text: string) =
  text |> Expect.stringContains "says the REPL is behind the app" "BEHIND"
  text |> Expect.stringContains "says what to do" "hard_reset_fsi_session"
  text |> Expect.stringContains "with the argument that does it" "rebuild=true"
  text |> Expect.stringContains "names what was patched" "Handlers.describe"

[<Tests>]
let tests =
  // Sequenced for the same reason McpToolExecutionTests is: these drive the one shared FSI actor.
  testSequenced <| testList "ReplFreshness on every surface an agent reads" [

    testTask "WHY - send_fsharp_code puts the warning on the result when the REPL is behind, after the result, and keeps the result" {
      let tools = SageFsTools(ctxWith behind, NullLogger<SageFsTools>.Instance)
      let! result = tools.send_fsharp_code("test", "let freshnessProbe = 41 + 1", "", "", "", 0, "")
      let text = textOf result
      text |> Expect.stringContains "the result is still there" "freshnessProbe"
      warningWords text
      let root = result.StructuredContent.Value
      root.GetProperty("replFreshness").GetProperty("state").GetString() |> Expect.equal "and the structured result says it as a field" "BehindApp"
    }

    testTask "WHY - send_fsharp_code says nothing extra when the REPL is level, and the structured result still says so" {
      let tools = SageFsTools(ctxWith ReplFreshness.InSync, NullLogger<SageFsTools>.Instance)
      let! result = tools.send_fsharp_code("test", "let freshnessProbeLevel = 1", "", "", "", 0, "")
      (textOf result).Contains "BEHIND" |> Expect.isFalse "no warning"
      result.StructuredContent.Value.GetProperty("replFreshness").GetProperty("state").GetString() |> Expect.equal "level" "InSync"
    }

    testTask "WHY - a failing send_fsharp_code is warned too: an error from stale code is the one an agent most needs to be told about" {
      let tools = SageFsTools(ctxWith behind, NullLogger<SageFsTools>.Instance)
      let! result = tools.send_fsharp_code("test", "let broken : int = \"not an int\"", "", "", "", 0, "")
      result.IsError.HasValue |> Expect.isTrue "it is an error result"
      warningWords (textOf result)
    }

    testTask "WHY - check_fsharp_code is warned: a check against the old build can pass or fail for code the app no longer runs" {
      let tools = SageFsTools(ctxWith behind, NullLogger<SageFsTools>.Instance)
      let! text = tools.check_fsharp_code("let x = 1", "", "")
      warningWords text
    }

    testTask "WHY - get_session_status carries the state as a field, for a level session as well as a behind one" {
      let readState (freshness: ReplFreshness) = task {
        let tools = SageFsTools(ctxWith freshness, NullLogger<SageFsTools>.Instance)
        let! text = tools.get_session_status("", "", 0)
        use doc = JsonDocument.Parse text
        return doc.RootElement.GetProperty("replFreshness").Clone()
      }
      let! level = readState ReplFreshness.InSync
      level.GetProperty("state").GetString() |> Expect.equal "level" "InSync"
      let! late = readState behind
      late.GetProperty("state").GetString() |> Expect.equal "behind" "BehindApp"
      late.GetProperty("savesSince").GetInt32() |> Expect.equal "two saves" 2
      [ for d in late.GetProperty("declarations").EnumerateArray() -> d.GetString() ]
      |> Expect.equal "naming what was patched" [ "Handlers.describe"; "Handlers.makeHeld" ]
      late.GetProperty("message").GetString() |> Expect.stringContains "and what to do" "hard_reset_fsi_session"
    }

    testTask "WHY - list_sessions says it on the session's own entry" {
      let tools = SageFsTools(ctxWith behind, NullLogger<SageFsTools>.Instance)
      let! text = tools.list_sessions()
      warningWords text
    }
  ]
