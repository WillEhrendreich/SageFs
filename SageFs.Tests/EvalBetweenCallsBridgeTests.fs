/// docs/plans/eval-output-for-an-llm.md, test 4: output a background thread writes between two evals comes back with the
/// next reply under a label, and the bridge's stdout stays valid JSON-RPC throughout. Real processes end to end: a daemon,
/// the built `sagefs mcp` bridge on stdio, a bare session with an isolated FSI host, and a thread in the user's code.
module SageFs.Tests.EvalBetweenCallsBridgeTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private sageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

/// The text of a `tools/call` answer: the first content block.
let private toolText (answer: string) : string =
  use doc = JsonDocument.Parse answer
  doc.RootElement.GetProperty("result").GetProperty("content").[0].GetProperty("text").GetString()

let private runBetweenCallsTest () : Task<unit> =
  task {
    let mcpPort, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
    let work = Path.Combine(Path.GetTempPath(), "sagefs-test", "between-calls", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory work |> ignore
    let goFile = Path.Combine(work, "go")
    let printedFile = Path.Combine(work, "printed")
    let! daemon, client = SageFs.Tests.HttpApiIntegrationTests.startDaemonWithArgs mcpPort work [ "--no-resume" ]
    client.Dispose()

    let psi = ProcessStartInfo()
    psi.FileName <- sageFsExe
    psi.ArgumentList.Add "mcp"
    psi.ArgumentList.Add "--mcp-port"
    psi.ArgumentList.Add(string mcpPort)
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.StandardOutputEncoding <- Encoding.UTF8
    psi.StandardInputEncoding <- Encoding.UTF8
    psi.Environment["SAGEFS_DATA_DIR"] <- Path.Combine(work, "bridge-data")
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    use bridge = Process.Start psi
    let stderrBuf = StringBuilder()
    bridge.ErrorDataReceived.Add(fun e -> match e.Data with null -> () | line -> stderrBuf.AppendLine line |> ignore)
    bridge.BeginErrorReadLine()

    let seen = Collections.Generic.List<string>()
    let describe () = sprintf "stdout lines: %A\nstderr:\n%s" (List.ofSeq seen) (stderrBuf.ToString())
    let mutable nextId = 0

    /// Writes one request and reads stdout until the line that answers its id; every line on the way must be JSON.
    let call (patience: TimeSpan) (method': string) (parameters: string) : Task<string> =
      task {
        nextId <- nextId + 1
        let id = nextId
        do! bridge.StandardInput.WriteLineAsync(sprintf """{"jsonrpc":"2.0","id":%d,"method":"%s","params":%s}""" id method' parameters)
        do! bridge.StandardInput.FlushAsync()
        let deadline = DateTime.UtcNow + patience
        let mutable answer = None
        while answer.IsNone do
          let remaining = deadline - DateTime.UtcNow
          match remaining > TimeSpan.Zero with
          | false -> failwithf "no answer to %s within %gs\n%s" method' patience.TotalSeconds (describe ())
          | true ->
            let readTask = bridge.StandardOutput.ReadLineAsync()
            let! completed = Task.WhenAny(readTask, Task.Delay remaining)
            match Object.ReferenceEquals(completed, readTask), readTask.IsCompleted with
            | true, true ->
              match readTask.Result with
              | null -> failwithf "the bridge closed stdout while waiting for %s\n%s" method' (describe ())
              | line ->
                seen.Add line
                use doc = JsonDocument.Parse line // a line that is not JSON throws here and fails the case
                match doc.RootElement.TryGetProperty "id" with
                | true, value when value.ValueKind = JsonValueKind.Number && value.GetInt32() = id -> answer <- Some line
                | _ -> ()
            | _ -> failwithf "no answer to %s within %gs\n%s" method' patience.TotalSeconds (describe ())
        return answer.Value
      }

    let tool (patience: TimeSpan) (name: string) (arguments: string) =
      call patience "tools/call" (sprintf """{"name":"%s","arguments":%s}""" name arguments)

    let escape (text: string) = JsonSerializer.Serialize text

    try
      try
        let! init =
          call TestTimeouts.daemonFirstOutput "initialize" """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"between-calls-e2e","version":"0.0.1"}}"""
        init |> Expect.stringContains "the bridge answered initialize" "\"name\":\"SageFs\""
        do! bridge.StandardInput.WriteLineAsync """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
        do! bridge.StandardInput.FlushAsync()

        let! created = tool TestTimeouts.daemonFirstOutput "create_bare_session" (sprintf """{"working_directory":%s}""" (escape work))
        let sessionId = (toolText created).Split([| '\n'; ' ' |], StringSplitOptions.RemoveEmptyEntries).[0]
        let! ready = tool TestTimeouts.sessionReadyColdBuild "get_session_status" (sprintf """{"session_id":"%s","wait_seconds":60}""" sessionId)
        toolText ready |> Expect.stringContains "the session is Ready" "\"state\":\"Ready\""

        let eval (code: string) =
          tool TestTimeouts.sessionReadyColdBuild "send_fsharp_code" (sprintf """{"agentName":"between-calls-e2e","session_id":"%s","code":%s}""" sessionId (escape code))

        // A thread in the user's code that prints once a file appears, then writes a second file to say it did. Both files are how
        // this test learns the line has been written; no clock decides it.
        let! started =
          eval (
            sprintf
              "let backgroundThread = System.Threading.Thread((fun () -> System.Threading.SpinWait.SpinUntil(fun () -> System.IO.File.Exists @\"%s\") |> ignore; printfn \"BG-LINE-FROM-THREAD\"; System.IO.File.WriteAllText(@\"%s\", \"x\")), IsBackground = true);;\nbackgroundThread.Start();;"
              goFile
              printedFile
          )
        (toolText started).Contains "BG-LINE-FROM-THREAD" |> Expect.isFalse "nothing has been printed yet"

        File.WriteAllText(goFile, "go")
        let deadline = DateTime.UtcNow + TestTimeouts.patience
        while not (File.Exists printedFile) && DateTime.UtcNow < deadline do
          do! Task.Delay TestTimeouts.pollTight
        File.Exists printedFile |> Expect.isTrue "the thread printed"

        let! second = eval "1 + 1;;"
        let reply = toolText second
        reply |> Expect.stringContains "the next reply carries what the thread printed" "BG-LINE-FROM-THREAD"
        reply |> Expect.stringContains "under its label" SageFs.Features.EvalResultSummary.BetweenCallsLabel
        reply |> Expect.stringContains "after the eval's own result" "val it: int = 2"
        (reply.IndexOf "val it: int = 2" < reply.IndexOf "BG-LINE-FROM-THREAD") |> Expect.isTrue "the result comes first"

        let! third = eval "2 + 2;;"
        (toolText third).Contains "BG-LINE-FROM-THREAD" |> Expect.isFalse "and it is handed over once"

        // The same session end to end for the cost: a 200 MB allocation earns a line on its reply, and the timeline shows the bytes beside its duration.
        let! heavy = eval "Array.zeroCreate<byte> 200000000 |> ignore;;"
        (toolText heavy) |> Expect.stringContains "a heavy eval ends with its cost line" "MB allocated · GCs gen0"
        let! timeline = tool TestTimeouts.patience "get_eval_timeline" "{}"
        (toolText timeline) |> Expect.stringContains "get_eval_timeline shows the bytes beside the duration" "MB allocated"

        // A clipped reply's notice names an expression, and running it over the same bridge gives back the characters that were cut.
        // The printed text is 6,000 x's, a newline and FSI's own `val it` line, so the cut lies inside the x's and is all x's.
        let! clipped = eval "printfn \"%s\" (String.replicate 6000 \"x\");;"
        let clippedReply = toolText clipped
        let notice = Text.RegularExpressions.Regex.Match(clippedReply, @"lastEvalOutput\.Substring\((\d+), (\d+)\)")
        notice.Success |> Expect.isTrue (sprintf "the notice of a clipped reply names the expression: %s" clippedReply)
        let cutLength = int notice.Groups[2].Value
        let! cutRead = eval (sprintf "%s;;" notice.Value)
        (toolText cutRead) |> Expect.stringContains "the expression returns the characters that were cut" (String('x', cutLength))

        let! _ = tool TestTimeouts.patience "stop_session" (sprintf """{"session_id":"%s"}""" sessionId)
        ()
      finally
        try bridge.StandardInput.Close() with _ -> ()
        bridge.WaitForExit TestTimeouts.patience |> ignore
        match bridge.HasExited with
        | false -> (try bridge.Kill() with _ -> ())
        | true -> ()
    finally
      try
        match daemon.HasExited with
        | false -> daemon.Kill()
        | true -> ()
      with _ -> ()
  }

[<Tests>]
let tests =
  testList "Eval output between calls over the bridge" [
    Integration.hostCaseTask
      "WHY — a background thread's output written between two evals is in the second reply under a label, and the bridge's stdout stays JSON-RPC"
      runBetweenCallsTest
  ]
