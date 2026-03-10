module SageFs.Vscode.JsHelpers

open Fable.Core
open Fable.Core.JsInterop
open Vscode

/// Convert a potentially-null/undefined JS value to Option
let inline tryOfObj (x: 'a) : 'a option =
  if SafeInterop.jsIsNullOrUndefined (box x) then None else Some x

/// DEPRECATED: Use SafeInterop.fieldString/fieldInt/fieldBool/fieldFloat/fieldArray/fieldObj instead.
/// This function uses unbox<'T> which Fable erases to a no-op — no runtime type checking.
[<System.Obsolete("Use SafeInterop typed field accessors instead")>]
let tryField<'T> (name: string) (obj: obj) : 'T option =
  match SafeInterop.jsIsNullOrUndefined obj with
  | true -> None
  | false ->
    let v = obj?(name)
    match SafeInterop.jsIsNullOrUndefined v with
    | true -> None
    | false -> Some (unbox<'T> v)

// ── JSON ────────────────────────────────────────────────────────────────

[<Emit("JSON.parse($0)")>]
let jsonParse (s: string) : obj = jsNative

[<Emit("JSON.stringify($0)")>]
let jsonStringify (o: obj) : string = jsNative

// ── Timers ──────────────────────────────────────────────────────────────

[<Emit("setTimeout($0, $1)")>]
let jsSetTimeout (fn: unit -> unit) (ms: int) : obj = jsNative

[<Emit("performance.now()")>]
let performanceNow () : float = jsNative

[<Emit("new Promise(resolve => setTimeout(resolve, $0))")>]
let sleep (ms: int) : JS.Promise<unit> = jsNative

// ── Promise helpers ─────────────────────────────────────────────────────

[<Emit("console.error('[SageFs] unhandled promise rejection:', $0)")>]
let private logPromiseError (err: obj) : unit = jsNative

/// Ignore a promise's result but log rejections to console.error.
/// Prefer promiseIgnoreLog when an output channel is available.
let promiseIgnore (p: JS.Promise<_>) : unit =
  p
  |> Promise.map ignore
  |> Promise.catch (fun err -> logPromiseError err)
  |> Promise.start

/// Ignore a promise's result, logging rejections to the provided sink (e.g. outputChannel.appendLine).
let promiseIgnoreLog (log: string -> unit) (p: JS.Promise<_>) : unit =
  p
  |> Promise.map ignore
  |> Promise.catch (fun err -> log (sprintf "[error] Unhandled promise rejection: %O" err))
  |> Promise.start

// ── SSE subscribers with exponential backoff reconnect ──────────────────

[<Import("createSseSubscriber", "./sse-helpers.js")>]
let private createSseSubscriber (url: string) (onMessage: string -> obj -> unit) (onReconnect: (unit -> unit) option) (logger: (string -> unit) option) (onDisconnect: (unit -> unit) option) : Disposable = jsNative

/// Simple SSE subscriber: parses `data:` lines as JSON, calls onData(parsed).
let subscribeSse (url: string) (onData: obj -> unit) : Disposable =
  createSseSubscriber url (fun _eventType data -> onData data) None None None

/// Simple SSE subscriber with optional logger for lifecycle events.
let subscribeSseWithLogger (url: string) (onData: obj -> unit) (logger: (string -> unit) option) : Disposable =
  createSseSubscriber url (fun _eventType data -> onData data) None logger None

/// Typed SSE subscriber: tracks `event:` type and `data:` payload.
/// Calls onEvent(eventType, parsedData) for each complete SSE message.
let subscribeTypedSse (url: string) (onEvent: string -> obj -> unit) : Disposable =
  createSseSubscriber url onEvent None None None

/// Typed SSE subscriber with reconnection callback and logger.
/// onReconnect fires when the SSE connection is re-established after a drop.
/// onDisconnect fires immediately when the SSE connection drops (before backoff).
/// logger routes SSE lifecycle messages to the VS Code output channel.
let subscribeTypedSseWithReconnect (url: string) (onEvent: string -> obj -> unit) (onReconnect: unit -> unit) (onDisconnect: unit -> unit) (logger: string -> unit) : Disposable =
  createSseSubscriber url onEvent (Some onReconnect) (Some logger) (Some onDisconnect)

// ── Timer helpers ───────────────────────────────────────────────────────

[<Emit("setInterval($0, $1)")>]
let jsSetInterval (fn: unit -> unit) (ms: int) : obj = jsNative

[<Emit("clearInterval($0)")>]
let jsClearInterval (id: obj) : unit = jsNative

[<Emit("clearTimeout($0)")>]
let jsClearTimeout (id: obj) : unit = jsNative

/// Install a global unhandled rejection handler that logs stack traces.
[<Emit("process.on('unhandledRejection', (reason) => { const msg = reason && reason.stack ? reason.stack : String(reason); $0('[SageFs] Unhandled rejection: ' + msg); })")>]
let installRejectionHandler (log: string -> unit) : unit = jsNative
