/// ═══════════════════════════════════════════════════════════════════════
/// NODE.JS IS A RUNTIME HERE, NEVER A TEST-AUTHORING LANGUAGE.
///
/// Everything in test-electron/ is F#, compiled to JS by Fable — the exact
/// same relationship the shipped extension (sagefs-vscode/src/) already has
/// to Node.js. A VS Code extension host IS a Node process; that dependency
/// is not something this test suite introduces, it is what is being tested.
/// What this rule forbids is hand-authored .js/.ts LOGIC: no test behavior,
/// no assertions, no control flow may be written directly in JavaScript or
/// TypeScript anywhere under test-electron/.
///
/// This file is the ONLY place that may declare new bindings into
/// @vscode/test-electron or bare Node globals (fetch, process, JSON), and
/// every binding here must be a single-purpose, one-line [<Import>] or
/// [<Emit>] naming exactly one JS value or method — matching Vscode.fs's
/// own "thin bindings, not a generated SDK" philosophy (see its header
/// comment). If @vscode/test-electron's surface grows, add one more binding
/// here in the same style; do not reach for an [<Emit>] string that embeds
/// actual logic (a loop, a conditional, a multi-step expression) instead of
/// naming a single JS member.
/// ═══════════════════════════════════════════════════════════════════════
module SageFs.VscodeTestElectron.Bindings

open Fable.Core
open Fable.Core.JsInterop
open SageFs.VscodeTestElectron.DaemonContract

// ── @vscode/test-electron ────────────────────────────────────────────────

type TestOptions =
  abstract extensionDevelopmentPath: string with get, set
  abstract extensionTestsPath: string with get, set
  abstract extensionTestsEnv: obj with get, set
  abstract launchArgs: string array with get, set
  abstract vscodeExecutablePath: string with get, set

[<Import("runTests", "@vscode/test-electron")>]
let runTests (options: TestOptions) : JS.Promise<float> = jsNative

// ── Node.js process/env — only what the launcher needs ───────────────────

module NodeProcess =
  [<Emit("process.env")>]
  let env: obj = jsNative

  [<Emit("process.env[$0]")>]
  let getEnv (name: string) : string = jsNative

  [<Emit("process.exit($0)")>]
  let exit (code: int) : unit = jsNative

  [<Emit("console.error($0)")>]
  let logError (message: string) : unit = jsNative

/// Not JsHelpers.sleep (SageFs.Vscode.JsHelpers) — that file relatively
/// imports src/sse-helpers.js, which does not exist in this project's own
/// output directory. One line is cheaper to duplicate than to drag that
/// dependency along.
[<Emit("new Promise(resolve => setTimeout(resolve, $0))")>]
let sleep (ms: int) : JS.Promise<unit> = jsNative

// ── The daemon's own HTTP API — plain fetch, no client library needed ────

[<Emit("fetch($0).then(r => r.json())")>]
let private fetchJson (url: string) : JS.Promise<LiveTestingStatusResponse> = jsNative

let getLiveTestingState (mcpPort: string) : JS.Promise<LiveTestingState> =
  fetchJson (sprintf "http://localhost:%s/api/live-testing/status" mcpPort)
  |> Promise.map ofResponse

[<Emit("fetch($0).then(r => r.json())")>]
let private fetchHotReloadJson (url: string) : JS.Promise<HotReloadStatusResponse> = jsNative

let getHotReloadWatchState (mcpPort: string) (sessionId: string) : JS.Promise<HotReloadWatchState> =
  fetchHotReloadJson (sprintf "http://localhost:%s/api/sessions/%s/hotreload" mcpPort sessionId)
  |> Promise.map ofHotReloadResponse
