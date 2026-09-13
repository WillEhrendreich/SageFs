/// The Agent/MCP actor (demo-actors-plan.md §2.4): drives SageFs over its
/// REAL Model Context Protocol surface — the exact tool vocabulary an AI
/// agent sees (`create_session`, `get_fsi_status`, `send_fsharp_code`, ...)
/// — against the cell's own daemon on its fixed MCP HTTP port, and renders
/// the GENUINE request/response transcript into a small, self-contained
/// on-screen page the recorder captures. No faked chat: every transcript
/// line this actor ever writes to the page came from an actual MCP
/// JSON-RPC response the daemon sent back — never scripted/canned text.
///
/// No MCP client SDK dependency: `SageFs.Demos` must not reference
/// `SageFs.Core`/`SageFs` (§4/roast §1), and this project's own `.fsproj`
/// is a shared seam this island does not touch, so no new
/// `PackageReference` either. `McpServer.fs` mounts the official
/// `ModelContextProtocol.AspNetCore` server via `app.MapMcp()` at the app
/// root — this module hand-rolls just enough of the "Streamable HTTP"
/// transport to drive it honestly: one JSON-RPC `initialize` POST, one
/// `notifications/initialized` POST, then one `tools/call` POST per tool —
/// tracking the `Mcp-Session-Id` response header the SDK issues and the
/// negotiated `MCP-Protocol-Version`, echoing both back on every later
/// request, and accepting either an `application/json` or a
/// `text/event-stream` response body (the SDK may answer a non-streaming
/// call either way).
module SageFs.Demos.Actors.Agent

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.Playwright
open SageFs.Demos.Domain
open SageFs.Demos.Actors.Actor

/// The cell's own daemon always listens on this fixed MCP port inside
/// `--unshare-net` (demo-actors-plan.md §0/§2.4 — the SAME literal
/// `Runtime.fs`'s own private `McpPort` uses for the dashboard's URL). Kept
/// as this actor's own constant rather than threading it through `Wire.fs`
/// (a seam-core file this island does not touch): the value is fixed for
/// every cell and never varies per scenario.
[<Literal>]
let private McpBaseUrl = "http://127.0.0.1:47749/"

/// ---------------------------------------------------------------------
/// A hand-rolled MCP "Streamable HTTP" JSON-RPC client. Not `private`: the
/// RED tests build an `Agent.Handle` directly (mirroring exactly how
/// `CellAgentTests.fs` already builds a `Dashboard.Handle` to prove the
/// dispatch seam without a live browser/daemon), which needs `Rpc.Session`
/// nameable from the test project.
/// ---------------------------------------------------------------------
module Rpc =

  type Session =
    { Http: HttpClient
      SessionId: string option ref
      ProtocolVersion: string option ref
      NextId: int ref }

  let create () : Session =
    { Http = new HttpClient()
      SessionId = ref None
      ProtocolVersion = ref None
      NextId = ref 1 }

  /// Recovers the JSON-RPC message from an SSE body: `\n\n`-separated
  /// events, each a run of lines; only `data:` lines carry payload (the
  /// wire format the MCP Streamable HTTP transport's SSE replies use).
  /// Takes the LAST event with a non-blank payload — a JSON-RPC response is
  /// the only message a non-streaming POST here is ever answered with.
  let private parseSse (body: string) : JsonDocument option =
    body.Replace("\r\n", "\n").Split("\n\n")
    |> Array.rev
    |> Array.tryPick (fun block ->
      let dataLines =
        block.Split('\n')
        |> Array.filter (fun l -> l.StartsWith "data:")
        |> Array.map (fun l -> l.Substring(5).TrimStart())
      let data = String.concat "\n" dataLines
      if String.IsNullOrWhiteSpace data then
        None
      else
        try
          Some(JsonDocument.Parse data)
        with _ ->
          None)

  let private post (session: Session) (jsonBody: string) : Async<Result<JsonDocument, string>> =
    async {
      use req = new HttpRequestMessage(HttpMethod.Post, McpBaseUrl)
      req.Content <- new StringContent(jsonBody, Encoding.UTF8, "application/json")
      req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
      req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "text/event-stream")

      session.SessionId.Value
      |> Option.iter (fun sid -> req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sid) |> ignore)

      session.ProtocolVersion.Value
      |> Option.iter (fun v -> req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", v) |> ignore)

      try
        let! resp = session.Http.SendAsync req |> Async.AwaitTask

        match resp.Headers.TryGetValues "Mcp-Session-Id" with
        | true, values -> session.SessionId.Value <- Seq.tryHead values
        | false, _ -> ()

        let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

        if not resp.IsSuccessStatusCode then
          return Error(sprintf "HTTP %d from %s: %s" (int resp.StatusCode) McpBaseUrl body)
        elif String.IsNullOrWhiteSpace body then
          return Ok(JsonDocument.Parse "{}")
        else
          let mediaType =
            resp.Content.Headers.ContentType
            |> Option.ofObj
            |> Option.map (fun h -> h.MediaType)
            |> Option.defaultValue ""

          if mediaType.Contains "event-stream" then
            match parseSse body with
            | Some doc -> return Ok doc
            | None -> return Error(sprintf "no JSON-RPC message in SSE body:\n%s" body)
          else
            return Ok(JsonDocument.Parse body)
      with ex ->
        return Error ex.Message
    }

  let private jsonArgs (pairs: (string * string) list) : string =
    let o = JsonObject()

    for k, v in pairs do
      o.[k] <- JsonValue.Create(v: string)

    o.ToJsonString()

  /// One-time MCP handshake: `initialize` then `notifications/initialized`
  /// — the spec's required sequence before any `tools/call`.
  let initialize (session: Session) : Async<Result<unit, string>> =
    async {
      let initBody =
        """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"sagefs-demos-agent-actor","version":"1.0.0"}}}"""

      match! post session initBody with
      | Error e -> return Error(sprintf "initialize: %s" e)
      | Ok doc ->
        match doc.RootElement.TryGetProperty "result" with
        | true, result ->
          match result.TryGetProperty "protocolVersion" with
          | true, pv -> session.ProtocolVersion.Value <- Some(pv.GetString())
          | false, _ -> ()
        | false, _ -> ()

        let notifyBody = """{"jsonrpc":"2.0","method":"notifications/initialized"}"""

        match! post session notifyBody with
        | Error e -> return Error(sprintf "notifications/initialized: %s" e)
        | Ok _ -> return Ok()
    }

  /// One `tools/call` — exactly the surface an MCP agent uses
  /// (`name`/`arguments`), returning the tool's own text content unparsed:
  /// every SageFs MCP tool already returns a plain text/JSON string, never
  /// a nested content array the caller has to unwrap further than this.
  let callTool (session: Session) (toolName: string) (arguments: (string * string) list) : Async<Result<string, string>> =
    async {
      let id = Threading.Interlocked.Increment session.NextId

      let body =
        sprintf
          """{"jsonrpc":"2.0","id":%d,"method":"tools/call","params":{"name":"%s","arguments":%s}}"""
          id
          toolName
          (jsonArgs arguments)

      match! post session body with
      | Error e -> return Error e
      | Ok doc ->
        match doc.RootElement.TryGetProperty "error" with
        | true, err -> return Error(err.ToString())
        | false, _ ->

        match doc.RootElement.TryGetProperty "result" with
        | false, _ -> return Error(sprintf "no 'result' in response: %s" (doc.RootElement.ToString()))
        | true, result ->
          match result.TryGetProperty "content" with
          | true, content when content.ValueKind = JsonValueKind.Array && content.GetArrayLength() > 0 ->
            match content.[0].TryGetProperty "text" with
            | true, t -> return Ok(t.GetString())
            | false, _ -> return Ok(content.[0].ToString())
          | _ -> return Ok(result.ToString())
    }

/// ---------------------------------------------------------------------
/// Finding the real sample project INSIDE the cell (demo-actors-plan.md
/// §10's "open a REAL project, not a bare Quick Start session" doctrine,
/// applied here too — genuineness is not just "the HTTP call is real", it
/// is also "the project this session opens is real"). `Runtime.fs`'s
/// `{{REPO_ROOT}}` token substitution (`wireStepOf`'s `resolveRepoRootToken`)
/// only rewrites a step's TYPED text — never the `Expectation` string this
/// actor's `Observe` receives (the only channel a non-input actor has on
/// today's wire, §1.2's seam) — so an MCP-driven step cannot ask the runner
/// to resolve the repo root for it, and this island does not edit
/// `Wire.fs`/`Runtime.fs`'s core to add a channel only this actor needs.
/// Instead this actor finds the root itself: `repoRoot` is RO-bound into
/// every cell at its own real, IDENTICAL absolute path (`Runtime.fs`'s
/// `cellSpec`: `repoRoot, repoRoot`) — nothing else creates that tree in
/// the cell's otherwise sealed, sparse bwrap filesystem, so locating
/// `SageFs.slnx` by walking DOWN from the filesystem root (never up —
/// `AppContext.BaseDirectory` here is the unrelated `/demos-bin` remap)
/// finds the exact same directory `Runtime.fs`'s own (upward-walking)
/// `findRepoRoot` found on the host. Bounded and defensive throughout: an
/// inaccessible or huge subtree never hangs or crashes the search — it is
/// skipped or the search gives up and reports a real "not found", never a
/// guess.
/// ---------------------------------------------------------------------
module RepoRoot =

  /// Every one of these is EITHER a kernel/device pseudo-tree, a known
  /// non-repo bind target, OR (confirmed directly against a real cell run,
  /// not assumed) the base toolchain the sandbox's OWN `Sandbox.args`
  /// unconditionally RO-binds at root (`"/usr", "/usr"` — the REAL, FULL
  /// host `/usr` tree, easily tens of thousands of directories on a real
  /// Linux box — plus `/bin`,`/sbin`,`/lib`,`/lib64` as SYMLINKS into that
  /// same tree, so each is a redundant second traversal of it). A first
  /// version of this search omitted `usr`/`lib`/`lib64`/`sbin`/`etc`/`tmp`
  /// and, measured directly against a real `record agent-mcp` run, silently
  /// exhausted its whole search budget walking `/usr` (which the top-level
  /// enumeration visits BEFORE `/home`) and returned `None` every time even
  /// though `SageFs.slnx` genuinely existed and was reachable — a `File.Exists`
  /// probe at its known real path confirmed this directly. None of these
  /// names can ever be the parent of a bind-mounted checkout, so excluding
  /// them is not a heuristic gamble: it is the reachable-tree shape this
  /// project's own `Sandbox.fs`/`Runtime.fs` actually build, confirmed live.
  let private skipNames =
    set
      [ ".nuget"
        ".git"
        "proc"
        "sys"
        "dev"
        "chrome-bin"
        "dotnet-root"
        "sagefs-bin"
        "demos-bin"
        "obj"
        "bin"
        "sbin"
        "lib"
        "lib64"
        "usr"
        "etc"
        "tmp"
        "node_modules" ]

  let rec private search (budget: int ref) (depth: int) (dir: string) : string option =
    if depth < 0 || budget.Value <= 0 then
      None
    else
      budget.Value <- budget.Value - 1

      try
        if File.Exists(Path.Combine(dir, "SageFs.slnx")) then
          Some dir
        else
          Directory.EnumerateDirectories dir
          |> Seq.filter (fun d -> not (skipNames.Contains(Path.GetFileName d)))
          |> Seq.tryPick (search budget (depth - 1))
      with _ ->
        None

  /// Searches under the filesystem root for `SageFs.slnx`, skipping the
  /// kernel/device pseudo-trees and every known non-repo bind target, capped
  /// so a misconfigured cell fails fast and loud instead of hanging.
  let find () : string option =
    let budget = ref 20000

    try
      Directory.EnumerateDirectories "/"
      |> Seq.filter (fun d -> not (skipNames.Contains(Path.GetFileName d)))
      |> Seq.tryPick (search budget 14)
    with _ ->
      None

  /// The real `.fsproj` under `dir` — never assumed by name, discovered.
  let findFsproj (dir: string) : string option =
    try
      Directory.GetFiles(dir, "*.fsproj") |> Array.tryHead
    with _ ->
      None

/// One real MCP exchange, exactly as observed on the wire — never a
/// scripted/canned line. `Response` is the tool's own raw text content.
type TranscriptEntry =
  { Step: string
    Tool: string
    Ok: bool
    Response: string }

/// A minimal, self-contained on-screen page (demo-actors-plan.md §2.4: "Do
/// NOT fake a chat UI over canned text ... Do NOT add product routes to the
/// daemon for a demo — keep the viz page inside the demos project"). No
/// server: `window.addTranscriptEntry` is called directly via
/// `Page.EvaluateAsync` every time a real MCP response comes back.
let private renderInitialHtml () : string =
  """<!doctype html>
<html><head><meta charset="utf-8">
<title>SageFs Agent/MCP transcript</title>
<style>
  html,body{margin:0;padding:0;background:#1f1f28;color:#dcd7ba;font:16px/1.4 -apple-system,"Segoe UI",sans-serif;}
  body{padding:24px;}
  h1{font-size:20px;color:#7e9cd8;margin:0 0 16px;}
  .entry{border:1px solid #54546d;border-radius:6px;padding:12px 16px;margin-bottom:12px;background:#2a2a37;}
  .entry.ok{border-left:4px solid #98bb6c;}
  .entry.err{border-left:4px solid #ff5d62;}
  .tool{font-weight:600;color:#e6c384;}
  .step{font-size:12px;color:#7e9cd8;text-transform:uppercase;letter-spacing:.05em;}
  pre{white-space:pre-wrap;word-break:break-word;margin:8px 0 0;font-size:13px;color:#dcd7ba;}
  *{cursor:none !important;}
</style></head>
<body>
  <h1>SageFs Agent/MCP session &mdash; real tool calls, real responses</h1>
  <div id="transcript" data-testid="mcp-transcript"></div>
  <script>
    window.addTranscriptEntry = function (entry) {
      var div = document.createElement('div');
      div.className = 'entry ' + (entry.ok ? 'ok' : 'err');
      div.setAttribute('data-testid', 'mcp-entry');
      var head = document.createElement('div');
      head.className = 'step';
      head.textContent = entry.step;
      var tool = document.createElement('div');
      tool.className = 'tool';
      tool.textContent = entry.tool + (entry.ok ? ' ✓' : ' ✗');
      var pre = document.createElement('pre');
      pre.textContent = entry.response;
      div.appendChild(head);
      div.appendChild(tool);
      div.appendChild(pre);
      document.getElementById('transcript').appendChild(div);
    };
  </script>
</body></html>"""

type Handle =
  { Playwright: IPlaywright
    Context: IBrowserContext
    Page: IPage
    Session: Rpc.Session
    InitResult: Result<unit, string> option ref }

/// Xvfb runs `-nocursor` — see `Actors/Dashboard.fs`'s identical doc comment
/// for why every page this project drives also forces `cursor: none`
/// itself: the synthetic-cursor design needs zero real cursors of any kind
/// in the captured frame.
let private hideRealCursor (page: IPage) : Async<unit> =
  async {
    let! _ = page.AddStyleTagAsync(PageAddStyleTagOptions(Content = "*, *::before, *::after { cursor: none !important; }")) |> Async.AwaitTask
    ()
  }

/// Launches the bundled Chromium `--app=` against a viz page THIS actor
/// writes into the cell's own writable home (`/home/demo`, already created
/// by `Runtime.fs`'s `innerScript` before any actor's prologue runs) —
/// never a static asset file (this island does not add one to the shared
/// `.fsproj`'s asset list) and never a `data:` URL (avoids Chromium's
/// `--app=data:` edge cases). `rect` places the window exactly like every
/// other actor (`Actors/Dashboard.fs`'s `launch`).
let launch (chromePath: string) (userDataDir: string) (rect: Rect) : Async<Handle> =
  async {
    let htmlPath = Path.Combine("/home/demo", "agent-viz.html")
    File.WriteAllText(htmlPath, renderInitialHtml ())

    let! playwright = Playwright.CreateAsync() |> Async.AwaitTask
    let options = BrowserTypeLaunchPersistentContextOptions()
    options.ExecutablePath <- chromePath
    options.Headless <- false

    options.Args <-
      ResizeArray
        [ "--no-sandbox"
          "--disable-gpu"
          "--ozone-platform=x11"
          sprintf "--window-position=%d,%d" rect.X rect.Y
          sprintf "--window-size=%d,%d" rect.W rect.H
          "--no-first-run"
          "--disable-features=Translate"
          "--disable-extensions"
          "--disable-infobars"
          "--no-default-browser-check"
          sprintf "--app=file://%s" htmlPath ]

    let! context = playwright.Chromium.LaunchPersistentContextAsync(userDataDir, options) |> Async.AwaitTask
    do! Async.Sleep 1000

    let page =
      if context.Pages.Count > 0 then
        context.Pages.[0]
      else
        context.WaitForPageAsync() |> Async.AwaitTask |> Async.RunSynchronously

    let! _ = page.WaitForLoadStateAsync(LoadState.Load) |> Async.AwaitTask
    do! hideRealCursor page

    return
      { Playwright = playwright
        Context = context
        Page = page
        Session = Rpc.create ()
        InitResult = ref None }
  }

let close (handle: Handle) : Async<unit> =
  async {
    do! handle.Context.CloseAsync() |> Async.AwaitTask
    handle.Playwright.Dispose()
    handle.Session.Http.Dispose()
  }

let private pushEntry (page: IPage) (entry: TranscriptEntry) : Async<unit> =
  async {
    try
      let! _ =
        page.EvaluateAsync<obj>(
          "e => window.addTranscriptEntry(e)",
          {| step = entry.Step
             tool = entry.Tool
             ok = entry.Ok
             response = entry.Response |}
        )
        |> Async.AwaitTask

      ()
    with _ ->
      // A Page-less test `Handle` (RED tests build one with
      // `Unchecked.defaultof<IPage>` to prove dispatch/parsing without a
      // live browser, mirroring `CellAgentTests.fs`'s Dashboard test)
      // cannot render into a page that does not exist — the real MCP call
      // this wraps has already genuinely happened either way, so a render
      // failure here is swallowed rather than losing that real result.
      ()
  }

let private ensureInitialized (handle: Handle) : Async<Result<unit, string>> =
  async {
    match handle.InitResult.Value with
    | Some r -> return r
    | None ->
      let! r = Rpc.initialize handle.Session
      handle.InitResult.Value <- Some r
      return r
  }

/// The convention this actor's OWN scenario (`Scenarios.Agent.fs`) uses to
/// pack a real MCP call into the one opaque string field a non-input
/// `LiveActor.Observe` receives on the wire
/// (`Wire.WireStep.ExpectSelector`, populated from
/// `Expectation.PageTextContains(selector, text)` — see `Runtime.fs`'s
/// `wireStepOf`, which only substitutes `{{REPO_ROOT}}` on `TypeText`,
/// never on this field): `selector = "<step label><tool name>"`,
/// `text` always `""` (this actor decides its own expected-response check
/// per tool internally — see `expectedSubstringFor` — rather than the wire
/// carrying a canned expected string one MCP response's own unpredictable
/// shape, e.g. a fresh session id, could never satisfy). Arguments are
/// resolved by THIS actor at call time from the real repo root it finds
/// inside the cell — nothing about WHICH real project/expression is used
/// is faked; only the wire ENCODING of "which tool, which step" is
/// unconventional, and it is confined entirely to this file and
/// `Scenarios.Agent.fs`. Not `private`: `AgentTests.fs` proves this parser
/// directly.
let parseWire (wireSelector: string) : (string * string) option =
  let suffix = ":has-text(\"\")"

  let stripped =
    if wireSelector.EndsWith suffix then
      wireSelector.Substring(0, wireSelector.Length - suffix.Length)
    else
      wireSelector

  match stripped.Split '' with
  | [| stepLabel; toolName |] -> Some(stepLabel, toolName)
  | _ -> None

let private sampleDir (repoRoot: string) : string =
  Path.Combine(repoRoot, Sample.relativePath Sample.WebappDatastar)

/// The real arguments this actor sends per tool — resolved from a real
/// `repoRoot`, never a literal/guessed path. Not `private`:
/// `AgentTests.fs` proves these are real, resolved paths (never a leaked
/// `{{REPO_ROOT}}` token) against the REAL repo root this checkout's own
/// tests run from.
let argumentsFor (repoRoot: string) (toolName: string) : Result<(string * string) list, string> =
  let dir = sampleDir repoRoot

  match toolName with
  | "create_session" ->
    match RepoRoot.findFsproj dir with
    | None -> Error(sprintf "no .fsproj found under %s" dir)
    | Some proj -> Ok [ "projects", proj; "working_directory", dir; "agentName", "sagefs-demos-agent" ]
  | "get_fsi_status" -> Ok [ "working_directory", dir ]
  | "send_fsharp_code" ->
    // Deliberately `List.sum [ 1 .. 10 ]` — the SAME expression
    // `repl-dashboard` already proves live — and NOT the project's own
    // qualified `SageFs.Samples.WebappDatastar.Program.todos.Length`
    // (evaluated successfully by dashboard-driven scenarios elsewhere).
    // Confirmed directly, live, against this exact sample+daemon: a
    // `get_fsi_status` "State: Ready" reply can land a moment BEFORE the
    // project's own compiled assembly is reliably resolvable for a
    // fully-qualified reference — a real `record agent-mcp` run reproduced
    // this once (the qualified expression failed "not defined" on every
    // retry for the full 90s ceiling), while a lighter, unsandboxed manual
    // run against the same daemon/sample sometimes resolved it instantly.
    // This is a genuine session-warmup race in the product's own auto-open
    // pipeline, not a demo-tool bug — `List.sum` never depends on that
    // namespace at all, so it proves the SAME thing this step needs (a
    // real MCP `send_fsharp_code` round trip evaluating live) without
    // gambling the recording on a race outside this island's scope to fix
    // (AGENTS.md: don't chase product bugs from a demo island — mirrors
    // `lt-dashboard`'s own documented, out-of-scope discovery race).
    Ok [ "agentName", "sagefs-demos-agent"; "code", "List.sum [ 1 .. 10 ]"; "working_directory", dir ]
  | other -> Error(sprintf "the agent-mcp scenario has no arguments recipe for tool '%s'" other)

/// The real response check per tool — a fresh `create_session` reply is an
/// unpredictable 8-hex session id, so only "the daemon answered without
/// erroring" is checked for it; `get_fsi_status`/`send_fsharp_code` have a
/// real, specific, evidence-backed expected substring (the tool's own
/// documented "State: Ready" wording; `repl-dashboard`'s own proven
/// "int = 55" for this exact expression).
let private expectedSubstringFor (toolName: string) : string =
  match toolName with
  | "get_fsi_status" -> "State: Ready"
  | "send_fsharp_code" -> "int = 55"
  | _ -> ""

let private looksLikeFailure (response: string) : bool =
  response.StartsWith "Error" || response.Contains "already exists"

let toLiveActor (handle: Handle) : LiveActor =
  let resolveRect (_selector: string) : Async<ScreenRect option> = async { return None }

  let observe (wireSelector: string) (timeoutMs: float) : Async<bool> =
    async {
      match parseWire wireSelector with
      | None -> return false
      | Some(stepLabel, toolName) ->

      match RepoRoot.find () with
      | None ->
        do!
          pushEntry
            handle.Page
            { Step = stepLabel
              Tool = toolName
              Ok = false
              Response = "could not locate the repo checkout bound into this cell (SageFs.slnx not found)" }

        return false
      | Some repoRoot ->

      match argumentsFor repoRoot toolName with
      | Error msg ->
        do! pushEntry handle.Page { Step = stepLabel; Tool = toolName; Ok = false; Response = msg }
        return false
      | Ok arguments ->

      match! ensureInitialized handle with
      | Error e ->
        do! pushEntry handle.Page { Step = stepLabel; Tool = "initialize"; Ok = false; Response = e }
        return false
      | Ok() ->

      let expected = expectedSubstringFor toolName
      let sw = Diagnostics.Stopwatch.StartNew()
      let mutable outcome = None

      // A real poll loop, not a single shot: `get_fsi_status` genuinely
      // needs to be asked more than once while a real cold FSI warmup
      // finishes (§9's own "a real session warmup can genuinely take
      // longer than a UI-click expectation ever needed to" — the exact
      // reasoning `CellAgent.fs`'s own 90s expectation ceiling documents).
      // Every iteration is a genuine, separate MCP round trip, pushed to
      // the transcript as it happens — never a fabricated "waiting..." line.
      while outcome.IsNone && float sw.ElapsedMilliseconds < timeoutMs do
        match! Rpc.callTool handle.Session toolName arguments with
        | Error e ->
          do! pushEntry handle.Page { Step = stepLabel; Tool = toolName; Ok = false; Response = e }
          outcome <- Some false
        | Ok response ->
          let matched =
            if expected = "" then
              not (looksLikeFailure response)
            else
              response.Contains expected

          do! pushEntry handle.Page { Step = stepLabel; Tool = toolName; Ok = matched; Response = response }

          if matched then
            outcome <- Some true
          else
            do! Async.Sleep 1000

      return outcome |> Option.defaultValue false
    }

  { Id = ActorId.Agent
    ResolveRect = resolveRect
    Observe = observe
    Command = fun _ -> async { return () }
    Close = fun () -> close handle }
