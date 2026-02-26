module SageFs.Vscode.DiagnosticsListener

open Fable.Core
open Fable.Core.JsInterop
open Vscode

[<Emit("""(() => {
  const http = require('http');
  let req;
  let buffer = '';
  let retryDelay = 1000;
  const maxDelay = 30000;
  const startListening = () => {
    req = http.get($0, { timeout: 0 }, (res) => {
      retryDelay = 1000;
      res.on('data', (chunk) => {
        buffer += chunk.toString();
        let lines = buffer.split('\\n');
        buffer = lines.pop() || '';
        for (const line of lines) {
          if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6));
              $1(data);
            } catch (_) {}
          }
        }
      });
      res.on('end', () => {
        retryDelay = Math.min(retryDelay * 2, maxDelay);
        setTimeout(startListening, retryDelay);
      });
      res.on('error', () => {
        retryDelay = Math.min(retryDelay * 2, maxDelay);
        setTimeout(startListening, retryDelay);
      });
    });
    req.on('error', () => {
      retryDelay = Math.min(retryDelay * 2, maxDelay);
      setTimeout(startListening, retryDelay);
    });
  };
  startListening();
  return { dispose: () => { if (req) req.destroy(); } };
})()""")>]
let subscribeSse (url: string) (onData: obj -> unit) : Disposable = jsNative

let start (port: int) (dc: DiagnosticCollection) =
  let url = sprintf "http://localhost:%d/diagnostics" port

  let onData (data: obj) =
    let rawDiags = data?diagnostics
    if isNull rawDiags then () else
    let diagnostics: obj array = rawDiags |> unbox
    let byFile = System.Collections.Generic.Dictionary<string, ResizeArray<Diagnostic>>()

    for diag in diagnostics do
      let file: string = diag?file |> unbox
      if isNull file then () else
      let msg = diag?message
      let message: string = if isNull msg then "" else unbox<string> msg
      let severity =
        match diag?severity |> unbox<string> with
        | "error" -> VDiagnosticSeverity.Error
        | "warning" -> VDiagnosticSeverity.Warning
        | "info" -> VDiagnosticSeverity.Information
        | _ -> VDiagnosticSeverity.Hint
      let startLine = (diag?startLine |> unbox<int>) - 1 |> max 0
      let startCol = (diag?startColumn |> unbox<int>) - 1 |> max 0
      let endLine = (diag?endLine |> unbox<int>) - 1 |> max 0
      let endCol = (diag?endColumn |> unbox<int>) - 1 |> max 0
      let range = newRange startLine startCol endLine endCol
      let d = newDiagnostic range message severity

      if not (byFile.ContainsKey file) then
        byFile.[file] <- ResizeArray()
      byFile.[file].Add(d)

    dc.clear ()
    for kv in byFile do
      let uri = uriFile kv.Key
      dc.set (uri, kv.Value)

  subscribeSse url onData
