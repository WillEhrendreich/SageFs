module SageFs.Vscode.JsHelpers

open Fable.Core
open Fable.Core.JsInterop
open Vscode

/// Convert a potentially-null JS value to Option
let inline tryOfObj (x: 'a) : 'a option =
  if isNull (box x) then None else Some x

/// Null-safe field extraction from a JS object
let tryField<'T> (name: string) (obj: obj) : 'T option =
  let v = obj?(name)
  if isNull (box v) then None else Some (unbox<'T> v)

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

/// Ignore a promise's result but log rejections instead of swallowing them silently.
let promiseIgnore (p: JS.Promise<_>) : unit =
  p
  |> Promise.catch (fun err -> logPromiseError err)
  |> Promise.start

// ── SSE subscribers with exponential backoff reconnect ──────────────────

/// Simple SSE subscriber: parses `data:` lines as JSON, calls onData(parsed).
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

/// Typed SSE subscriber: tracks `event:` type and `data:` payload.
/// Calls onEvent(eventType, parsedData) for each complete SSE message.
[<Emit("""(() => {
  const http = require('http');
  let req;
  let buffer = '';
  let currentEvent = 'message';
  let retryDelay = 1000;
  const maxDelay = 30000;
  const startListening = () => {
    req = http.get($0, { timeout: 0 }, (res) => {
      retryDelay = 1000;
      res.on('data', (chunk) => {
        buffer += chunk.toString();
        let lines = buffer.split('\n');
        buffer = lines.pop() || '';
        for (const line of lines) {
          if (line.startsWith('event: ')) {
            currentEvent = line.slice(7).trim();
          } else if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6));
              $1(currentEvent, data);
            } catch (_) {}
            currentEvent = 'message';
          } else if (line.trim() === '') {
            currentEvent = 'message';
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
let subscribeTypedSse (url: string) (onEvent: string -> obj -> unit) : Disposable = jsNative

// ── Timer helpers ───────────────────────────────────────────────────────

[<Emit("setInterval($0, $1)")>]
let jsSetInterval (fn: unit -> unit) (ms: int) : obj = jsNative

[<Emit("clearInterval($0)")>]
let jsClearInterval (id: obj) : unit = jsNative
