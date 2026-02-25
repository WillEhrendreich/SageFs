namespace SageFs

open System
open System.Net.Http
open System.Text
open SageFs.WorkerProtocol

/// HTTP client for communicating with a worker's Kestrel server.
/// Lives in SageFs.Core so SessionManager can create proxies.
module HttpWorkerClient =

  /// Map WorkerMessage → (httpMethod, path, bodyJson option).
  let toRoute (msg: WorkerMessage) : string * string * string option =
    match msg with
    | WorkerMessage.GetStatus rid ->
      "GET", sprintf "/status?replyId=%s" (Uri.EscapeDataString rid), None
    | WorkerMessage.EvalCode(code, rid) ->
      "POST", "/eval",
      Some (Serialization.serialize {| code = code; replyId = rid |})
    | WorkerMessage.CheckCode(code, rid) ->
      "POST", "/check",
      Some (Serialization.serialize {| code = code; replyId = rid |})
    | WorkerMessage.TypeCheckWithSymbols(code, filePath, rid) ->
      "POST", "/typecheck-symbols",
      Some (Serialization.serialize {| code = code; filePath = filePath; replyId = rid |})
    | WorkerMessage.GetCompletions(code, cursorPos, rid) ->
      "POST", "/completions",
      Some (Serialization.serialize {| code = code; cursorPos = cursorPos; replyId = rid |})
    | WorkerMessage.CancelEval ->
      "POST", "/cancel", None
    | WorkerMessage.LoadScript(filePath, rid) ->
      "POST", "/load-script",
      Some (Serialization.serialize {| filePath = filePath; replyId = rid |})
    | WorkerMessage.ResetSession rid ->
      "POST", "/reset",
      Some (Serialization.serialize {| replyId = rid |})
    | WorkerMessage.HardResetSession(rebuild, rid) ->
      "POST", "/hard-reset",
      Some (Serialization.serialize {| rebuild = rebuild; replyId = rid |})
    | WorkerMessage.RunTests(tests, maxParallelism, rid) ->
      "POST", "/run-tests",
      Some (Serialization.serialize {| tests = tests; maxParallelism = maxParallelism; replyId = rid |})
    | WorkerMessage.Shutdown ->
      "POST", "/shutdown", None

  /// Create a SessionProxy backed by HTTP to the given base URL.
  let httpProxy (baseUrl: string) : SessionProxy =
    let client = new HttpClient(BaseAddress = Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30.0))
    fun msg ->
      async {
        let method, path, body = toRoute msg
        let! resp =
          match method with
          | "GET" ->
            client.GetAsync(path) |> Async.AwaitTask
          | _ ->
            let content =
              body
              |> Option.map (fun b ->
                new StringContent(b, Encoding.UTF8, "application/json") :> HttpContent)
              |> Option.defaultValue null
            client.PostAsync(path, content) |> Async.AwaitTask
        resp.EnsureSuccessStatusCode() |> ignore
        let! json = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        return Serialization.deserialize<WorkerResponse> json
      }
