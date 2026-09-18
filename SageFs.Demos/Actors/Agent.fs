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
open System.Runtime.CompilerServices
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

/// ---------------------------------------------------------------------
/// Locating the cohort demo's git+Expecto FIXTURE inside the cell — the
/// SAME "walk down from the filesystem root looking for a marker file"
/// technique `RepoRoot` above uses, applied to a different tree for a
/// different reason (cohort-demo-scenario-plan.md's phase B' dual-session
/// correction: `land_and_wait` below needs to `git rev-parse` the fixture's
/// `alice-good`/`bob-break` branches to real commit shas at call time,
/// since only the HOST-side `Runtime.Cohort.prepareFixture` — a phase A
/// module this island does not touch or link against — ever sees the
/// `FixtureResult` value; the wire (`Wire.fs`, also untouched) has no
/// channel to hand this actor that value directly, so it must be
/// rediscovered the same way `RepoRoot.find` rediscovers the repo root).
///
/// `Runtime.Cohort.prepareFixture` builds its throwaway git repo under
/// `Directory.CreateTempSubdirectory()` — i.e. under the HOST's own
/// `/tmp` — and `Runtime.Cohort.actorBinds` RW-binds it into the cell at
/// the IDENTICAL absolute path (`Sandbox.fs`'s `(host, cell)` bind-pair
/// convention, mirroring `cellSpec`'s existing `sampleDir, sampleDir`
/// RW-bind `Runtime.Cohort.fs`'s own doc comment points at). Unlike
/// `RepoRoot.skipNames`, this module's skip set deliberately does NOT
/// exclude `"tmp"`: `Sandbox.args` gives every cell a bare `--tmpfs /tmp`
/// (a fresh, EMPTY tmpfs — confirmed directly against `Sandbox.fs`'s own
/// flag list, `--tmpfs "/tmp"`) with the fixture's one subdirectory
/// bind-mounted on top of it, so `/tmp` inside the cell holds nothing but
/// that one directory (plus whatever else the sandbox itself mounts
/// there) — never the large, unrelated tree `RepoRoot`'s own doc comment
/// found under `/usr` when it first tried an unbounded walk. Excluding
/// `"tmp"` here would make the fixture, which lives ONLY under `/tmp`,
/// permanently unfindable.
/// ---------------------------------------------------------------------
module FixtureRoot =

  /// Same rationale as `RepoRoot.skipNames` for every entry EXCEPT
  /// `"tmp"`, which is deliberately absent — see this module's own doc
  /// comment above.
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
        "node_modules" ]

  let rec private search (budget: int ref) (depth: int) (dir: string) : string option =
    if depth < 0 || budget.Value <= 0 then
      None
    else
      budget.Value <- budget.Value - 1

      try
        if File.Exists(Path.Combine(dir, "Fixture.fsproj")) then
          Some dir
        else
          Directory.EnumerateDirectories dir
          |> Seq.filter (fun d -> not (skipNames.Contains(Path.GetFileName d)))
          |> Seq.tryPick (search budget (depth - 1))
      with _ ->
        None

  /// Searches under the filesystem root for the cohort fixture's own
  /// `Fixture.fsproj` marker (written by `Runtime.Cohort.writeFixtureSources`
  /// — a project name no real repo project or sample uses), bounded the
  /// same way `RepoRoot.find` is so a misconfigured cell fails fast and
  /// loud instead of hanging.
  let find () : string option =
    let budget = ref 20000

    try
      Directory.EnumerateDirectories "/"
      |> Seq.filter (fun d -> not (skipNames.Contains(Path.GetFileName d)))
      |> Seq.tryPick (search budget 14)
    with _ ->
      None

  /// Resolves `gitRef` (a branch name, tag, or sha) to its commit sha
  /// inside the fixture repo at `fixtureDir` via a raw `git rev-parse`
  /// process — `land_and_wait` below uses this to turn the wire's
  /// `shaTag=alice-good`/`shaTag=bob-break` into the real commit sha
  /// `request_landing`'s `commits` argument needs, without this actor
  /// ever being handed the sha directly (see this module's own doc
  /// comment on why). Mirrors `Runtime.Cohort.fs`'s own `git` helper
  /// exactly (same `ProcessStartInfo` shape, same stdout-trim-on-success/
  /// stderr-on-failure contract) — this island does not reference that
  /// module (a phase A file this island does not link against), so the
  /// process-spawning is duplicated here rather than shared, same as
  /// every other actor in this project spawning its own processes.
  let resolveGitRefSha (fixtureDir: string) (gitRef: string) : Async<Result<string, string>> =
    async {
      try
        let psi =
          Diagnostics.ProcessStartInfo(
            "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = fixtureDir)

        psi.ArgumentList.Add "rev-parse"
        psi.ArgumentList.Add gitRef
        use proc = new Diagnostics.Process(StartInfo = psi)
        proc.Start() |> ignore
        let! stdout = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
        let! stderr = proc.StandardError.ReadToEndAsync() |> Async.AwaitTask
        do! proc.WaitForExitAsync() |> Async.AwaitTask

        if proc.ExitCode = 0 then
          return Ok(stdout.Trim())
        else
          return Error(sprintf "git rev-parse %s exited %d in %s: %s" gitRef proc.ExitCode fixtureDir stderr)
      with ex ->
        return Error ex.Message
    }

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

/// ---------------------------------------------------------------------
/// Per-Handle COHORT state — the dual-session correction (cohort-demo-
/// scenario-plan.md finding #2, verified live against `SageFs/Mcp.fs`'s
/// `memberIdFor`: cohort member identity is bound to the MCP TRANSPORT
/// SESSION, not the `agentName` argument, so `alice` and `bob` collapse
/// to one member unless each runs its own `initialize` handshake on its
/// own `Rpc.Session` — see `sessionFor` below).
///
/// Deliberately NOT new fields on `Handle` above: `Handle` is a public
/// record `SageFs.Demos.Tests.AgentTests`'s
/// "a malformed wire selector fails closed" test constructs directly with
/// a positional field literal (`Playwright = ...; Context = ...; Page =
/// ...; Session = ...; InitResult = ...`), mirroring `CellAgentTests.fs`'s
/// own `Dashboard.Handle` test — a REQUIRED field added to `Handle` here
/// would break that pre-existing, out-of-scope test file's compile for
/// every future actor-state addition, which is exactly the kind of
/// ripple a wire-widening correction should not cause (`parseCohortWire`
/// itself took the same care: a NEW sibling function, `parseWire` left
/// byte-for-byte unchanged). A `ConditionalWeakTable` keyed on
/// `handle.Session` (a reference type, freshly allocated once per
/// `launch` call, so it is a stable, unique-per-Handle key) gives every
/// `Handle` its own private dual-session/claims state without widening
/// the record at all: entries are created lazily on first use and are
/// naturally garbage-collected alongside their owning `Handle`/`Session`,
/// so nothing here needs explicit teardown beyond disposing the cached
/// `HttpClient`s in `close` below.
/// ---------------------------------------------------------------------
module private CohortState =

  /// One extra `Rpc.Session` per cohort `agentName` this Handle has
  /// driven a cohort-wire step for (`alice`, `bob`, ...) — each gets its
  /// OWN `initialize` handshake (`sessionFor`), so each is a genuinely
  /// distinct daemon member.
  let private sessionsByHandle =
    ConditionalWeakTable<Rpc.Session, Collections.Generic.Dictionary<string, Rpc.Session>>()

  /// The (claimId, fence) an `agentName` most recently got back from a
  /// successful `acquire_claim` cohort-wire call — `land_and_wait` (beats
  /// 7/8 of the plan) needs these to build `request_landing`'s
  /// `"claimId:fence"` `claims` argument without the wire having to carry
  /// a claim id/fence it cannot know ahead of a live `acquire_claim`
  /// response.
  let private claimsByHandle =
    ConditionalWeakTable<Rpc.Session, Collections.Generic.Dictionary<string, string * int64>>()

  let sessionsFor (handle: Handle) : Collections.Generic.Dictionary<string, Rpc.Session> =
    sessionsByHandle.GetValue(handle.Session, (fun _ -> Collections.Generic.Dictionary()))

  let claimsFor (handle: Handle) : Collections.Generic.Dictionary<string, string * int64> =
    claimsByHandle.GetValue(handle.Session, (fun _ -> Collections.Generic.Dictionary()))

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

    // Dispose every per-agentName cohort session's own HttpClient too
    // (CohortState above) — each one is a real, separately-allocated
    // `Rpc.Session` from `sessionFor`, never covered by the legacy
    // `handle.Session.Http.Dispose()` above.
    for kv in CohortState.sessionsFor handle do
      kv.Value.Http.Dispose()
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

/// Get-or-create THIS `Handle`'s own `Rpc.Session` for `agentName` — the
/// dual-session correction (cohort-demo-scenario-plan.md finding #2): a
/// FRESH `Rpc.Session` runs its own `initialize` handshake, which the MCP
/// SDK answers with its own `Mcp-Session-Id` header, which
/// `SageFs/Mcp.fs`'s `memberIdFor` binds distinct cohort membership to
/// (`MemberId.Mcp transportSessionId`) — so `alice` and `bob`, driven
/// through two different sessions from this one function, are two real,
/// distinct cohort members, never the same `agentName` string collapsed
/// onto one shared connection (verified empirically live before this
/// function existed — see the plan's finding #2: two `agentName`s on ONE
/// connection both resolved to the SAME member and the second
/// `join_cohort` failed with "already a member"). Idempotent per
/// `agentName`: a later cohort step for an already-seen agent reuses its
/// already-initialized session, exactly like `ensureInitialized` above
/// does for the legacy path's single session.
let private sessionFor (handle: Handle) (agentName: string) : Async<Result<Rpc.Session, string>> =
  async {
    let sessions = CohortState.sessionsFor handle

    match sessions.TryGetValue agentName with
    | true, session -> return Ok session
    | false, _ ->
      let session = Rpc.create ()

      match! Rpc.initialize session with
      | Error e -> return Error(sprintf "initialize (%s): %s" agentName e)
      | Ok() ->
        sessions.[agentName] <- session
        return Ok session
  }

/// Parses `acquireClaim`'s own real success text (`SageFs/Mcp.fs`:
/// `sprintf "Acquired claim %s over %s (fence=%d)." cid scope (int64
/// fence)`) to recover the claim id and fence `land_and_wait` (beats 7/8)
/// needs to build `request_landing`'s `"claimId:fence"` argument. Manual
/// string search rather than `Regex` — mirrors this file's own existing
/// parsers (`parseWire`/`parseCohortWire`/`parseArgsEncoded`), all of
/// which fail closed (`None`) on anything that doesn't look exactly like
/// the expected shape rather than guessing.
let private tryParseAcquiredClaim (response: string) : (string * int64) option =
  let prefix = "Acquired claim "
  let overMarker = " over "
  let fenceMarker = "(fence="

  if not (response.StartsWith prefix) then
    None
  else
    let afterPrefix = response.Substring prefix.Length
    let overIdx = afterPrefix.IndexOf overMarker

    if overIdx < 0 then
      None
    else
      let claimId = afterPrefix.Substring(0, overIdx)
      let fenceIdx = response.IndexOf fenceMarker

      if fenceIdx < 0 then
        None
      else
        let afterFence = response.Substring(fenceIdx + fenceMarker.Length)
        let closeIdx = afterFence.IndexOf ')'

        if closeIdx < 0 then
          None
        else
          match Int64.TryParse(afterFence.Substring(0, closeIdx)) with
          | true, fence -> Some(claimId, fence)
          | false, _ -> None

/// Captures a successful `acquire_claim` cohort-wire response's
/// (claimId, fence) for `agentName` (`CohortState.claimsFor`) so a LATER
/// `land_and_wait` step for the SAME agent can present it to
/// `request_landing`. Best-effort: a response that doesn't parse (should
/// never happen for a genuinely successful `acquire_claim` call, since
/// `acquireClaim`'s own success text is fixed — see
/// `tryParseAcquiredClaim`'s doc comment) leaves nothing recorded, and
/// `land_and_wait` reports that plainly rather than this function ever
/// throwing.
let private recordClaim (handle: Handle) (agentName: string) (response: string) : unit =
  match tryParseAcquiredClaim response with
  | Some(claimId, fence) -> (CohortState.claimsFor handle).[agentName] <- (claimId, fence)
  | None -> ()

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

/// WIDENED wire encoding, added alongside (never replacing) `parseWire`
/// above: a sibling parser for a 4-field form that can drive an ARBITRARY
/// MCP tool with its own agent identity and a wire-carried expected
/// substring -- the vocabulary cohort scenarios need (`join_cohort`,
/// `acquire_claim`, `set_integration_ref`, `request_landing`,
/// `get_cohort_status`, ...), none of which fit the legacy form's
/// per-tool `argumentsFor`/`expectedSubstringFor` lookup tables (those are
/// keyed on a small, fixed tool set and have no notion of "which agent").
/// `parseWire` itself is left byte-for-byte unchanged -- its 2-field
/// contract (and its exact `(string * string) option` return shape) is
/// exercised directly by `AgentTests.fs`, which this island does not
/// edit -- so this is a NEW function, detected purely by field count
/// after splitting on the SAME `\u001e` (RS) separator and stripping the
/// SAME synthetic `:has-text("")` suffix `Runtime.fs`'s `wireStepOf`
/// always appends onto a `PageTextContains` selector. A selector can only
/// ever match one of the two field counts, so trying both parsers is
/// unambiguous and neither can accidentally shadow the other.
///
/// EXACT WIRE FORMAT (this is the contract a phase-C `cohortStep`
/// scenario helper must produce -- see also `parseArgsEncoded` below):
///
///   "<agentName>\u001e<toolName>\u001e<argsEncoded>\u001e<expected>"
///
/// - `agentName`   -- the cohort member making this call (e.g.
///                   "alice", "bob"). Prepended as
///                   `("agentName", agentName)` onto the MCP `arguments`
///                   list sent to `toolName` -- every cohort tool
///                   takes `agentName` as a required argument, so callers
///                   never repeat it inside `argsEncoded`.
/// - `toolName`    -- any MCP tool name, passed straight to
///                   `Rpc.callTool` (already fully generic: toolName +
///                   `(string * string) list` args -> response text --
///                   no per-tool arguments recipe is needed here, unlike
///                   the legacy form's `argumentsFor`).
/// - `argsEncoded` -- the REMAINING tool arguments (beyond
///                   `agentName`), as `;;`-delimited `key=value` pairs,
///                   e.g. `"role=Implementer;;working_directory=/fixture"`.
///                   `;;` was chosen because it collides with neither the
///                   field separator (`\u001e`) nor the `=` inside a
///                   pair. Each pair splits on its FIRST `=` only, so a
///                   value that itself contains `=` (a commit
///                   `statement`, say) survives intact. An empty string
///                   decodes to `[]` -- for a tool that needs only
///                   `agentName`, e.g. `get_cohort_status`.
/// - `expected`    -- the WIRE-CARRIED substring the poll loop checks
///                   for in the tool's raw text response, taking the
///                   place the legacy form's fixed per-tool
///                   `expectedSubstringFor` lookup plays. This is
///                   essential, not optional convenience: cohort beats
///                   need DIFFERENT expected wording from the SAME tool
///                   depending on the beat -- e.g. `acquire_claim`
///                   expects a claim id on a clean claim, but must expect
///                   the SPECIFIC conflict wording (naming the current
///                   holder, e.g. "already claimed by") on a
///                   deliberately-rejected one; the generic "doesn't look
///                   like a failure" sniff (`looksLikeFailure`) would
///                   wrongly FAIL that rejected-claim beat, since a
///                   conflict response legitimately contains no "Error"
///                   text of its own. An empty `expected` still falls
///                   back to that same generic
///                   `not (looksLikeFailure response)` check, for a
///                   cohort tool call with nothing tool-specific to
///                   assert.
///
/// Not `private`: mirrors `parseWire` being non-private for
/// `AgentTests.fs` -- a cohort-focused test file can round-trip this
/// the same way, against the real
/// (agentName, toolName, argsEncoded, expected) tuple, without touching
/// `parseWire`'s own existing contract.
let parseCohortWire (wireSelector: string) : (string * string * string * string) option =
  let suffix = ":has-text(\"\")"

  let stripped =
    if wireSelector.EndsWith suffix then
      wireSelector.Substring(0, wireSelector.Length - suffix.Length)
    else
      wireSelector

  match stripped.Split '' with
  | [| agentName; toolName; argsEncoded; expected |] -> Some(agentName, toolName, argsEncoded, expected)
  | _ -> None

/// Decodes `parseCohortWire`'s `argsEncoded` field -- see that
/// function's doc comment for the exact format: `;;`-delimited
/// `key=value` pairs, each split on its FIRST `=` only. An
/// empty/whitespace-only input, or a pair with no `=` at all, contributes
/// nothing (never a crash on a malformed pair -- this actor fails a
/// step by returning `false`/an error transcript entry, never by
/// throwing).
let parseArgsEncoded (argsEncoded: string) : (string * string) list =
  if String.IsNullOrEmpty argsEncoded then
    []
  else
    argsEncoded.Split([| ";;" |], StringSplitOptions.None)
    |> Array.filter (fun pair -> pair <> "")
    |> Array.choose (fun pair ->
      match pair.IndexOf '=' with
      | -1 -> None
      | i -> Some(pair.Substring(0, i), pair.Substring(i + 1)))
    |> Array.toList

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

/// Shared poll loop for BOTH wire forms once `stepLabel`/`toolName`/
/// `arguments`/`expected` have been resolved (the legacy form resolves
/// them from `RepoRoot`/`argumentsFor`/`expectedSubstringFor`; the
/// widened cohort form resolves them directly from the wire — see
/// `parseCohortWire`'s doc comment). Repeatedly calls the tool over a
/// real MCP round trip, pushes every real response to the transcript, and
/// succeeds as soon as the response contains `expected` — or, when
/// `expected = ""`, as soon as it doesn't look like a failure
/// (`looksLikeFailure`, the legacy form's own fallback, preserved
/// exactly). Never a fabricated "waiting..." line: a real poll loop, not
/// a single shot, because `get_fsi_status` genuinely needs to be asked
/// more than once while a real cold FSI warmup finishes (§9's own "a real
/// session warmup can genuinely take longer than a UI-click expectation
/// ever needed to" — the exact reasoning `CellAgent.fs`'s own 90s
/// expectation ceiling documents), and a cohort `request_landing`/
/// `get_cohort_status` pair needs the identical polling shape while the
/// daemon runs the landing's test matrix.
///
/// Takes an explicit `session` (dual-session correction — the legacy
/// caller passes `handle.Session`, a cohort caller passes its own
/// agent-specific session from `sessionFor`) rather than deriving one
/// from `handle` itself, and an `onSuccess` callback fired with the
/// MATCHING response text (the legacy caller passes `ignore`; the cohort
/// caller uses it to capture a successful `acquire_claim`'s claim id/
/// fence via `recordClaim`). Session initialization is now the CALLER's
/// responsibility (`ensureInitialized`/`sessionFor` both already return
/// an initialized session), not this function's — it no longer touches
/// `handle.Session`/`handle.InitResult` at all, only `handle.Page` to
/// render the transcript.
let private pollTool
  (page: IPage)
  (session: Rpc.Session)
  (stepLabel: string)
  (toolName: string)
  (arguments: (string * string) list)
  (expected: string)
  (timeoutMs: float)
  (onSuccess: string -> unit)
  : Async<bool> =
  async {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable outcome = None

    while outcome.IsNone && float sw.ElapsedMilliseconds < timeoutMs do
      match! Rpc.callTool session toolName arguments with
      | Error e ->
        do! pushEntry page { Step = stepLabel; Tool = toolName; Ok = false; Response = e }
        outcome <- Some false
      | Ok response ->
        let matched =
          if expected = "" then
            not (looksLikeFailure response)
          else
            response.Contains expected

        do! pushEntry page { Step = stepLabel; Tool = toolName; Ok = matched; Response = response }

        if matched then
          onSuccess response
          outcome <- Some true
        else
          do! Async.Sleep 1000

    return outcome |> Option.defaultValue false
  }

/// The "land and wait" pseudo-tool (`land_and_wait` on the wire, wired
/// from `Scenarios.Cohort.fs`'s beats 7/8 — cohort-demo-scenario-plan.md's
/// "alice's good change lands" / "bob's breaking change is BLOCKED").
/// `land_and_wait` is NEVER sent to the daemon as an MCP tool name — it is
/// this actor's OWN two-call recipe, because a real landing genuinely
/// needs two distinct MCP calls with different retry shapes: ONE
/// `request_landing` (queues the landing — calling it again would queue a
/// SECOND, duplicate landing, so it must never be inside a retry loop),
/// then a POLL of `get_cohort_status` until the queued landing's own
/// state — asynchronously advanced by the daemon's landing performer —
/// reaches `expected` (`"Landed"` for beat 7, `"FailingTests"` for
/// beat 8, both real substrings of `Cohort.LandingState`'s `%A` rendering
/// in `SageFs/Mcp.fs`'s `renderCohortFrame`). `pollTool`'s single-tool
/// retry-until-match loop cannot express "call tool A once, then poll
/// tool B" — hence this dedicated function instead of trying to shoehorn
/// it through `pollTool` alone (it still uses `pollTool` for its own
/// `get_cohort_status` half).
///
/// `argsEncoded` carries `shaTag=<git ref in the fixture repo>` (required
/// — resolved to a real commit sha via `FixtureRoot.resolveGitRefSha`,
/// never a sha this actor is handed directly, see `FixtureRoot`'s own
/// doc comment on why) and an optional `statement=<landing statement>`
/// (defaults to a generic one naming the agent and ref). The claim
/// (`claimId:fence`) `request_landing` needs comes from
/// `CohortState.claimsFor handle`, populated by an EARLIER cohort-wire
/// `acquire_claim` step for the SAME `agentName` via `recordClaim` — never
/// carried on this step's own wire, since it cannot be known until that
/// earlier call's real response comes back.
let private landAndWait
  (handle: Handle)
  (session: Rpc.Session)
  (stepLabel: string)
  (agentName: string)
  (argsEncoded: string)
  (expected: string)
  (timeoutMs: float)
  : Async<bool> =
  async {
    let args = parseArgsEncoded argsEncoded
    let tryArg key = args |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd

    match tryArg "shaTag" with
    | None ->
      do!
        pushEntry
          handle.Page
          { Step = stepLabel
            Tool = "land_and_wait"
            Ok = false
            Response = "land_and_wait requires 'shaTag=<git ref in the fixture repo>' in its argsEncoded" }

      return false
    | Some shaTag ->

    match (CohortState.claimsFor handle).TryGetValue agentName with
    | false, _ ->
      do!
        pushEntry
          handle.Page
          { Step = stepLabel
            Tool = "land_and_wait"
            Ok = false
            Response = sprintf "no claim recorded for %s — an acquire_claim cohort-wire step for %s must run (and succeed) first" agentName agentName }

      return false
    | true, (claimId, fence) ->

    match FixtureRoot.find () with
    | None ->
      do!
        pushEntry
          handle.Page
          { Step = stepLabel
            Tool = "land_and_wait"
            Ok = false
            Response = "could not locate the cohort fixture inside this cell (Fixture.fsproj not found)" }

      return false
    | Some fixtureDir ->

    match! FixtureRoot.resolveGitRefSha fixtureDir shaTag with
    | Error e ->
      do!
        pushEntry
          handle.Page
          { Step = stepLabel
            Tool = "land_and_wait"
            Ok = false
            Response = sprintf "could not resolve '%s' to a commit sha in %s: %s" shaTag fixtureDir e }

      return false
    | Ok sha ->

    let statement = tryArg "statement" |> Option.defaultValue (sprintf "%s lands %s" agentName shaTag)

    let landingArgs =
      [ "agentName", agentName
        "claims", sprintf "%s:%d" claimId fence
        "commits", sha
        "statement", statement ]

    match! Rpc.callTool session "request_landing" landingArgs with
    | Error e ->
      do! pushEntry handle.Page { Step = stepLabel; Tool = "request_landing"; Ok = false; Response = e }
      return false
    | Ok response when looksLikeFailure response ->
      do! pushEntry handle.Page { Step = stepLabel; Tool = "request_landing"; Ok = false; Response = response }
      return false
    | Ok response ->
      do! pushEntry handle.Page { Step = stepLabel; Tool = "request_landing"; Ok = true; Response = response }
      // request_landing only QUEUES the landing — the daemon's own landing
      // performer advances it through Rebasing/Verifying to Landed/Blocked
      // asynchronously (McpTools.fs's own `request_landing` doc: "this
      // queues the request AND the pipeline runs it; watch its progress
      // via get_cohort_status"). `get_cohort_status` takes no arguments.
      return! pollTool handle.Page session stepLabel "get_cohort_status" [] expected timeoutMs ignore
  }

let toLiveActor (handle: Handle) : LiveActor =
  let resolveRect (_selector: string) : Async<ScreenRect option> = async { return None }

  let observe (wireSelector: string) (timeoutMs: float) : Async<bool> =
    async {
      // Try the WIDENED 4-field cohort form first (an arbitrary tool +
      // agentName + wire-carried expected). It can never collide with the
      // legacy 2-field form — a selector's field count picks exactly one
      // parser — so trying it first costs nothing on a legacy selector
      // (`parseCohortWire` returns None immediately on a 2-field split).
      match parseCohortWire wireSelector with
      | Some(agentName, toolName, argsEncoded, expected) ->
        let stepLabel = sprintf "%s -> %s" agentName toolName

        // Dual-session correction: every cohort-wire call routes through
        // THIS agent's own `Rpc.Session` (`sessionFor`), never the legacy
        // `handle.Session` — see `sessionFor`'s and `CohortState`'s doc
        // comments for why this is what makes alice/bob genuinely distinct
        // cohort members.
        match! sessionFor handle agentName with
        | Error e ->
          do! pushEntry handle.Page { Step = stepLabel; Tool = "initialize"; Ok = false; Response = e }
          return false
        | Ok session ->

        if toolName = "land_and_wait" then
          return! landAndWait handle session stepLabel agentName argsEncoded expected timeoutMs
        else
          let arguments = ("agentName", agentName) :: parseArgsEncoded argsEncoded
          // Only `acquire_claim` needs its successful response captured
          // (a later `land_and_wait` step's `request_landing` call needs
          // the claim id/fence it returns) — every other cohort tool has
          // nothing later steps read back off this actor's own state.
          let onSuccess = if toolName = "acquire_claim" then recordClaim handle agentName else ignore
          return! pollTool handle.Page session stepLabel toolName arguments expected timeoutMs onSuccess
      | None ->

      // Fall back to the ORIGINAL `agent-mcp` encoding, unchanged: same
      // checks, same order, same error text as before this widening —
      // still routed through the single legacy `handle.Session`/
      // `ensureInitialized`, never a per-agent cohort session.
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
      return! pollTool handle.Page handle.Session stepLabel toolName arguments expected timeoutMs ignore
    }

  { Id = ActorId.Agent
    ResolveRect = resolveRect
    Observe = observe
    Command = fun _ -> async { return () }
    Close = fun () -> close handle }
