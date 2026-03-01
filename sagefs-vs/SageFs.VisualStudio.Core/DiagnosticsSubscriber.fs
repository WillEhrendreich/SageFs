namespace SageFs.VisualStudio.Core

open System
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Connects to SageFs daemon's SSE diagnostics endpoint and
/// forwards events through a callback.
type DiagnosticsSubscriber(port: int) =
  let handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
  let http = new HttpClient(handler)
  let mutable cts: CancellationTokenSource option = None

  let tryStr (el: JsonElement) (prop: string) (fb: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.String then v.GetString() else fb

  let tryInt (el: JsonElement) (prop: string) (fb: int) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.Number then v.GetInt32() else fb

  let tryArr (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.Array then Some v else None

  member _.Start(onDiagnostic: Action<string, string, int, int, int, int, string>) =
    let newCts = new CancellationTokenSource()
    cts <- Some newCts
    let url = sprintf "http://localhost:%d/diagnostics" port
    let mutable retryDelay = 1000

    let rec loop () = task {
      try
        let! resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, newCts.Token)
        use! stream = resp.Content.ReadAsStreamAsync(newCts.Token)
        use reader = new IO.StreamReader(stream)
        retryDelay <- 1000
        while not (reader.EndOfStream || newCts.Token.IsCancellationRequested) do
          let! line = reader.ReadLineAsync(newCts.Token)
          if line <> null && line.StartsWith("data: ") then
            try
              let json = line.Substring(6)
              use doc = JsonDocument.Parse(json)
              let root = doc.RootElement
              match tryArr root "diagnostics" with
              | Some arr ->
                for d in arr.EnumerateArray() do
                  let file = tryStr d "file" ""
                  let message = tryStr d "message" ""
                  let startLine = tryInt d "startLine" 1
                  let startCol = tryInt d "startColumn" 1
                  let endLine = tryInt d "endLine" 1
                  let endCol = tryInt d "endColumn" 1
                  let severity = tryStr d "severity" "error"
                  onDiagnostic.Invoke(file, message, startLine, startCol, endLine, endCol, severity)
              | None -> ()
            with _ -> ()
      with
      | :? OperationCanceledException -> ()
      | _ ->
        retryDelay <- min (retryDelay * 2) 30000
        do! Task.Delay(retryDelay, newCts.Token)
        if not newCts.Token.IsCancellationRequested then
          do! loop ()
    }
    Task.Run(fun () -> loop () :> Task) |> ignore

  member _.Stop() =
    match cts with
    | Some c -> c.Cancel(); c.Dispose(); cts <- None
    | None -> ()

  interface IDisposable with
    member this.Dispose() =
      this.Stop()
      http.Dispose()
