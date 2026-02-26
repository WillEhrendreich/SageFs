module SageFs.Tests.HotReloadBrowserTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright
open SageFs.AppState

let evalCode (actor: AppActor) code =
  task {
    let request = { Code = code; Args = Map.empty }
    let! response = actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
    return response
  }

let evalHotReload (actor: AppActor) code =
  task {
    let request = { Code = code; Args = Map.ofList [ "hotReload", box true ] }
    let! response = actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
    return response
  }

let waitForServer (port: int) =
  task {
    use client = new System.Net.Http.HttpClient()
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable ready = false
    let mutable lastErr = ""
    while not ready && sw.ElapsedMilliseconds < 15000L do
      try
        let! _ = client.GetStringAsync(sprintf "http://localhost:%d/" port)
        ready <- true
      with ex ->
        lastErr <- ex.Message
        do! Task.Delay(500)
    if not ready then failtestf "Server did not start within 15s (last error: %s)" lastErr
  }

/// Run JS on a Playwright page, discarding the return value.
let js (page: IPage) (script: string) =
  task { let! _ = page.EvaluateAsync(script) in () }

/// Run JS with a JSON-serialized string arg injected via %s placeholder.
let jsWithArg (page: IPage) (scriptTemplate: string) (arg: string) =
  let escaped = System.Text.Json.JsonSerializer.Serialize(arg)
  let script = scriptTemplate.Replace("{0}", escaped)
  task { let! _ = page.EvaluateAsync(script) in () }

/// The demo page shell with animated code + browser split view.
let demoShell = """<!DOCTYPE html>
<html><head><style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { display: flex; width: 1200px; height: 520px; font-family: 'Segoe UI', system-ui, sans-serif; overflow: hidden; }
  .code-panel {
    width: 560px; padding: 18px 20px; background: #1e1e2e; color: #cdd6f4;
    display: flex; flex-direction: column; overflow: hidden;
    border-right: 3px solid #313244;
  }
  .code-panel .hdr {
    font-size: 13px; color: #a6adc8; margin-bottom: 10px;
    display: flex; align-items: center; gap: 8px;
  }
  .dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
  .dot-r { background: #f38ba8; } .dot-y { background: #f9e2af; } .dot-g { background: #a6e3a1; }
  #code {
    font-family: 'Cascadia Code', 'Fira Code', Consolas, monospace;
    font-size: 14px; line-height: 1.65; white-space: pre; flex: 1;
  }
  .browser-panel {
    flex: 1; padding: 18px 20px; background: #f5f5f5;
    display: flex; flex-direction: column; overflow: hidden;
  }
  .browser-panel .hdr { font-size: 13px; color: #888; margin-bottom: 10px; }
  .url-bar {
    background: white; border: 1px solid #d8d8d8; border-radius: 18px;
    padding: 5px 14px; font-size: 12px; color: #444; margin-bottom: 14px;
  }
  #page {
    background: white; border: 1px solid #e0e0e0; border-radius: 8px;
    padding: 20px; flex: 1; box-shadow: 0 1px 4px rgba(0,0,0,0.06);
  }
  #page h1 { font-size: 26px; margin-bottom: 6px; color: #222; }
  #page h2 { font-size: 16px; color: #666; margin-bottom: 4px; font-weight: 500; }
  #page pre { font-family: Consolas, monospace; font-size: 13px; color: #555; background: #f7f7f7; padding: 6px 10px; border-radius: 4px; }
  #status {
    position: absolute; bottom: 10px; left: 50%; transform: translateX(-50%);
    padding: 5px 18px; border-radius: 14px; font-size: 12px; font-weight: 600;
    box-shadow: 0 2px 8px rgba(0,0,0,0.15); color: white; background: #45475a;
    transition: background 0.3s;
  }
  .kw { color: #cba6f7; } .str { color: #a6e3a1; } .typ { color: #89b4fa; }
  .hi { background: rgba(166,227,161,0.12); display: block; margin: 0 -20px; padding: 0 20px; }
  .cur { display: inline-block; width: 2px; height: 1.1em; background: #cdd6f4; vertical-align: text-bottom; animation: blink 1s step-end infinite; }
  @keyframes blink { 50% { opacity: 0; } }
  @keyframes flash { 0%,100% { opacity:1 } 50% { opacity:0.3 } }
</style>
<script>
function hl(code, changed) {
  return code.split('\n').map((line, i) => {
    let h = line.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
    h = h.replace(/\b(let|fun|match|with|if|then|else|open|type|module)\b/g, '<span class="kw">$1</span>');
    h = h.replace(/\b(HttpHandler|HttpContext|WebApplication)\b/g, '<span class="typ">$1</span>');
    let parts = h.split('&quot;'); // won't match since we didn't escape quotes to &quot;
    // Split on actual " that survived escaping
    parts = h.split('"'); let out=''; let ins=false;
    for(let j=0;j<parts.length;j++){
      if(j>0){out+=ins?'"</span>':'<span class="str">"';ins=!ins;}
      out+=parts[j];
    }
    if(ins) out+='</span>';
    if(changed&&changed.includes(i)) return '<span class="hi">'+out+'</span>';
    return out;
  }).join('\n');
}
function setCode(code, changed) {
  document.getElementById('code').innerHTML = hl(code, changed||[]) + '<span class="cur"></span>';
}
function setPage(html) { document.getElementById('page').innerHTML = html; }
function setStatus(text, bg, fg) {
  let s = document.getElementById('status');
  s.textContent = text; s.style.background = bg;
  if(fg) s.style.color = fg; else s.style.color = 'white';
}
</script>
</head>
<body style="position: relative;">
  <div class="code-panel">
    <div class="hdr">
      <span class="dot dot-r"></span><span class="dot dot-y"></span><span class="dot dot-g"></span>
      &nbsp; SageFs &mdash; F# REPL
    </div>
    <div id="code"></div>
  </div>
  <div class="browser-panel">
    <div class="hdr">&#127760; Browser</div>
    <div class="url-bar">&#128274; localhost/</div>
    <div id="page"></div>
  </div>
  <div id="status"></div>
</body></html>"""

[<Tests>]
let tests =
  testSequenced
  <| testList "[Integration] Hot reload browser tests" [

    testCase "browser shows code change and output update when handler is hot-patched"
    <| fun _ ->
      task {
        let actor = FalcoTests.sharedActor.Value
        let port = FalcoTests.getRandomPort ()
        let demoDir =
          IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hot-reload-demo")
        IO.Directory.CreateDirectory(demoDir) |> ignore
        let videoPath = IO.Path.Combine(demoDir, "demo.webm")

        // --- Start demo Falco app ---
        let initialCode =
          sprintf
            """
let demoGreeting () = "Hello from Falco! v1"
let greetingSource () = "v1 source"

let demoHandler : HttpHandler =
  fun ctx ->
    Response.ofHtml (
      _html [] [
        _head [] [ _title [] [ _text "Hot Reload Demo" ] ]
        _body [] [
          _div [] [
            _h2 [] [ _text "source" ]
            _pre [ _id_ "source" ] [ _text (greetingSource ()) ]
          ]
          _div [] [
            _h1 [ _id_ "greeting" ] [ _text (demoGreeting ()) ]
          ]
        ]
      ]
    ) ctx

let hrDemoBuilder = WebApplication.CreateBuilder()
HostingAbstractionsWebHostBuilderExtensions.UseUrls(hrDemoBuilder.WebHost, "http://localhost:%d") |> ignore
let hrDemoApp = hrDemoBuilder.Build()
hrDemoApp.MapGet("/", demoHandler) |> ignore
let hrDemoStartTask = hrDemoApp.StartAsync()
printfn "Hot reload demo started on port %d"
"""
            port
            port

        let! r1 = evalCode actor initialCode
        match r1.EvaluationResult with
        | Error ex -> failtestf "Failed to create demo app: %s" ex.Message
        | Ok _ -> ()

        do! waitForServer port

        // --- Launch Chromium with video recording ---
        let! pw = Playwright.CreateAsync()
        let! browser = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
        let! ctx =
          browser.NewContextAsync(
            BrowserNewContextOptions(
              ViewportSize = ViewportSize(Width = 1200, Height = 520),
              RecordVideoDir = demoDir,
              RecordVideoSize = RecordVideoSize(Width = 1200, Height = 520)))
        let! page = ctx.NewPageAsync()

        try
          do! page.SetContentAsync(demoShell)

          let codeV1Lines = [|
            """let demoGreeting () = "Hello from Falco! v1" """
            """let greetingSource () = "v1 source" """
            ""
            "let demoHandler : HttpHandler ="
            "  fun ctx ->"
            "    Response.ofHtml ("
            "      _h1 [] [ _text (demoGreeting ()) ]"
            "    ) ctx"
          |]
          let codeV1 = codeV1Lines |> String.concat "\n"

          let pageV1 =
            "<h2>source</h2>" +
            "<pre>v1 source</pre>" +
            "<h1 style=\"margin-top:12px\">Hello from Falco! v1</h1>"

          // ---- SCENE 1: Type initial code line by line ----
          do! js page "setStatus('Falco app running', '#45475a')"
          do! js page (sprintf "setPage(%s)" (System.Text.Json.JsonSerializer.Serialize(pageV1)))

          for i in 0 .. codeV1Lines.Length - 1 do
            let partial = codeV1Lines.[0..i] |> String.concat "\n"
            do! jsWithArg page "setCode({0})" partial
            do! Task.Delay(100)

          // Hold on initial state
          do! Task.Delay(2200)

          // ---- SCENE 2: Erase old function lines, type new ones ----
          do! js page "setStatus('Redefining functions...', '#f38ba8')"

          let oldLine0 = codeV1Lines.[0].TrimEnd()
          let oldLine1 = codeV1Lines.[1].TrimEnd()
          let rest = codeV1Lines.[2..] |> String.concat "\n"

          // Fast erase line 1 then line 0
          for erasing in [oldLine1; oldLine0] do
            for j in erasing.Length - 1 .. -1 .. 0 do
              if j % 3 = 0 || j < 3 then // skip some frames for speed
                let remaining =
                  if erasing = oldLine1 then
                    if j > 0 then oldLine0 + "\n" + erasing.[0..j-1] + "\n" + rest
                    else oldLine0 + "\n" + rest
                  else
                    if j > 0 then erasing.[0..j-1] + "\n" + rest
                    else rest
                do! jsWithArg page "setCode({0})" remaining
                do! Task.Delay(15)

          do! Task.Delay(200)

          // Type new function definitions
          let newLine0 = """let demoGreeting () = "Hot reloaded! v2" """
          let newLine1 = """let greetingSource () = "v2 source" """

          for j in 1 .. newLine0.Length do
            if j % 2 = 0 || j = newLine0.Length then
              let full = newLine0.[0..j-1] + "\n\n" + rest
              do! jsWithArg page "setCode({0}, [0])" full
              do! Task.Delay(25)

          for j in 1 .. newLine1.Length do
            if j % 2 = 0 || j = newLine1.Length then
              let full = newLine0 + "\n" + newLine1.[0..j-1] + "\n" + rest
              do! jsWithArg page "setCode({0}, [0,1])" full
              do! Task.Delay(25)

          do! Task.Delay(600)

          // ---- SCENE 3: Eval + Harmony patching ----
          do! js page "setStatus('Ctrl+Enter — Harmony patching methods...', '#fab387')"

          let hotCode =
            """let demoGreeting () = "Hot reloaded! v2" """ + "\n" +
            """let greetingSource () = "v2 source" """

          let! r2 = evalHotReload actor hotCode
          match r2.EvaluationResult with
          | Error ex -> failtestf "Hot reload eval failed: %s" ex.Message
          | Ok _ -> ()

          Expect.isNonEmpty
            (r2.Metadata
             |> Map.tryFind "reloadedMethods"
             |> Option.map (fun v -> v :?> string list)
             |> Option.defaultValue [])
            "should have reloadedMethods in metadata"

          do! Task.Delay(300)

          // Verify server actually changed
          use httpClient = new System.Net.Http.HttpClient()
          let! updatedHtml = httpClient.GetStringAsync(sprintf "http://localhost:%d/" port)
          Expect.stringContains updatedHtml "Hot reloaded! v2" "server responds with patched content"

          // ---- SCENE 4: Page updates instantly ----
          let pageV2 =
            "<h2>source</h2>" +
            "<pre>v2 source</pre>" +
            "<h1 style=\"margin-top:12px\">Hot reloaded! v2</h1>"

          do! js page (sprintf "setPage(%s)" (System.Text.Json.JsonSerializer.Serialize(pageV2)))
          do! js page "setStatus('Page updated instantly — no restart', '#a6e3a1', '#1e1e2e')"

          // Remove change highlights
          let codeV2 = newLine0 + "\n" + newLine1 + "\n" + rest
          do! jsWithArg page "setCode({0})" codeV2

          // Hold on final state
          do! Task.Delay(2800)

          printfn "Video recorded to: %s" demoDir
        finally
          ctx.CloseAsync().GetAwaiter().GetResult()
          browser.CloseAsync().GetAwaiter().GetResult()
          pw.Dispose()

        // Rename video file
        let videoFiles =
          IO.Directory.GetFiles(demoDir, "*.webm")
          |> Array.sortByDescending IO.File.GetLastWriteTimeUtc
        if videoFiles.Length > 0 then
          let src = videoFiles.[0]
          if src <> videoPath then
            if IO.File.Exists(videoPath) then IO.File.Delete(videoPath)
            IO.File.Move(src, videoPath)
          printfn "Demo video: %s (%d bytes)" videoPath (IO.FileInfo(videoPath).Length)
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

  ]
