module SageFs.DevReloadMiddleware

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open SageFs.Utils

/// Load the devreload.js embedded resource once at startup.
let private devReloadJsTemplate =
  let asm = Assembly.GetExecutingAssembly()
  let name =
    asm.GetManifestResourceNames()
    |> Array.find (fun n -> n.EndsWith("devreload.js", StringComparison.OrdinalIgnoreCase))
  use stream = asm.GetManifestResourceStream(name)
  use reader = new StreamReader(stream, Encoding.UTF8)
  reader.ReadToEnd()

/// Detect the user's editor from SAGEFS_EDITOR or EDITOR env vars
/// and return the URL pattern for clickable file:line:column links.
/// Defaults to VS Code scheme.
let editorUrlPattern () =
  let editor =
    match Environment.GetEnvironmentVariable("SAGEFS_EDITOR") with
    | null | "" -> Environment.GetEnvironmentVariable("EDITOR") |> Option.ofObj |> Option.defaultValue "code"
    | e -> e
  let lower = editor.ToLowerInvariant()
  match lower.Contains("rider") || lower.Contains("jetbrains") with
  | true -> "jetbrains://rider/navigate/reference?path={file}&line={line}&column={col}"
  | false ->
    match lower.Contains("cursor") with
    | true -> "cursor://file/{file}:{line}:{col}"
    | false -> "vscode://file/{file}:{line}:{col}"

/// Escape a string for safe embedding in a JavaScript string literal.
/// Prevents </script> injection and JS string escaping issues.
let internal jsStringEscape (s: string) =
  s
    .Replace("\\", "\\\\")
    .Replace("\"", "\\\"")
    .Replace("'", "\\'")
    .Replace("\r", "\\r")
    .Replace("\n", "\\n")
    .Replace("<", "\\x3C")   // prevents </script> injection

/// Generate the reload script. Port > 0 connects cross-origin to the worker's
/// SSE endpoint; port 0 falls back to a same-origin relative path (for tests).
///
/// Chesterton's fence: the script includes several deliberate design choices:
/// - Infinite-reload guard via sessionStorage prevents reload bombs when the
///   server sends malformed JSON (catch block calls reload → reconnect → same
///   bad event → infinite loop). Counter resets after 5s of stability.
/// - retry:1000 is sent by the SSE endpoint (not the script) for faster reconnect.
/// - console.debug logging aids debugging the hot-reload system itself.
/// - Error overlay shows compilation errors *in the browser* — this is what makes
///   the DX tweet-worthy. Elm and Vite do this; FsiX does not.
let reloadScript (workerPort: int) =
  let cfg = DevReload.DevReloadConfig.defaults
  let sseUrl =
    match workerPort > 0 with
    | true -> sprintf "http://127.0.0.1:%d/__sagefs__/reload" workerPort
    | false -> "/__sagefs__/reload"
  let js =
    devReloadJsTemplate
      .Replace("{{SSE_URL}}", sseUrl)
      .Replace("{{RELOAD_GUARD_THRESHOLD}}", string cfg.ReloadGuardThreshold)
      .Replace("{{RELOAD_RESET_WINDOW_MS}}", string cfg.ReloadCountResetWindowMs)
      .Replace("{{SSE_TIMEOUT_MS}}", string cfg.SseConnectionTimeoutMs)
      .Replace("{{COMPILE_TIMER_MS}}", string cfg.CompileTimerUpdateMs)
      .Replace("{{AUTO_RELOAD_THRESHOLD_MS}}", string cfg.AutoReloadThresholdMs)
      .Replace("{{LONG_COMPILE_WARNING_MS}}", string cfg.LongCompileWarningMs)
      .Replace("{{EDITOR_URL_PATTERN}}", jsStringEscape (editorUrlPattern()))
  sprintf """<script data-sagefs-injected="devreload">%s</script>""" js

let private handledKey = "SageFs.DevReload.Handled"
let private maxBufferSize = DevReload.DevReloadConfig.defaults.MaxBodyBufferSizeBytes

let private shouldInjectScript (ctx: HttpContext) =
  let ct = ctx.Response.ContentType
  not (isNull ct) &&
  ct.Contains("text/html", StringComparison.OrdinalIgnoreCase) &&
  ctx.Response.StatusCode >= 200 &&
  ctx.Response.StatusCode < 300

/// Prevent browser caching so hot reloads always fetch fresh content.
let private setNoCacheHeaders (ctx: HttpContext) =
  ctx.Response.Headers["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-store"

/// Parse encoding from Content-Type header (e.g. "text/html; charset=iso-8859-1").
/// Falls back to UTF-8 when charset is missing, empty, or unrecognized.
let internal parseEncoding (contentType: string) =
  match String.IsNullOrEmpty(contentType) with
  | true -> Encoding.UTF8
  | false ->
    let parts = contentType.Split(';', StringSplitOptions.TrimEntries)
    parts
    |> Array.tryFind (fun p -> p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
    |> Option.bind (fun p ->
      let charset = p.Substring(8).Trim().Trim('"')
      try Some (Encoding.GetEncoding(charset))
      with _ -> None)
    |> Option.defaultValue Encoding.UTF8

/// Generate a cryptographic nonce for CSP script-src.
let internal generateNonce () =
  RandomNumberGenerator.GetBytes(16) |> Convert.ToBase64String

/// Insert nonce="..." attribute into the injected script tag.
let internal injectScriptNonce (nonce: string) (script: string) =
  script.Replace("<script ", sprintf "<script nonce=\"%s\" " nonce)

/// Add 'nonce-{value}' to an existing CSP header's script-src directive.
/// Uses directive-level tokenization to avoid corrupting script-src-elem or script-src-attr.
/// If no script-src exists, appends a new script-src directive with the nonce.
let internal addNonceToCsp (nonce: string) (cspHeader: string) =
  let nonceToken = sprintf "'nonce-%s'" nonce
  let directives = cspHeader.Split(';', StringSplitOptions.TrimEntries)
  let hasScriptSrc =
    directives |> Array.exists (fun d ->
      let name = d.Split(' ').[0]
      name.Equals("script-src", StringComparison.OrdinalIgnoreCase))
  match hasScriptSrc with
  | true ->
    directives
    |> Array.map (fun d ->
      let parts = d.Split(' ')
      match parts.[0].Equals("script-src", StringComparison.OrdinalIgnoreCase) with
      | true -> sprintf "script-src %s %s" nonceToken (String.concat " " parts.[1..])
      | false -> d)
    |> String.concat "; "
  | false -> sprintf "%s; script-src %s" cspHeader nonceToken

/// Create a middleware that injects the devreload script into HTML responses.
/// SSE is served by the worker HTTP server — this middleware only does injection.
let createMiddleware (workerPort: int) =
  let script = reloadScript workerPort
  fun (next: RequestDelegate) ->
    RequestDelegate(fun ctx -> task {
      match ctx.Items.ContainsKey(handledKey) with
      | true ->
        Log.debug "[DevReload] Skipping %s %s — already handled" ctx.Request.Method ctx.Request.Path.Value
        do! next.Invoke(ctx)
      | false ->
        ctx.Items[handledKey] <- true

        let accept = ctx.Request.Headers["Accept"].ToString()

        // Body-swap: any request that MIGHT produce HTML
        let acceptsHtml =
          accept.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
          accept.Contains("*/*", StringComparison.Ordinal) ||
          String.IsNullOrEmpty(accept)

        // Compression stripping: ONLY requests that explicitly want HTML.
        // Browser navigation sends "text/html,..." — strip compression so body-swap sees raw HTML.
        // Fetch/XHR with "Accept: */*" for JSON — leave compression intact.
        let explicitlyWantsHtml =
          accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)

        match acceptsHtml with
        | false ->
          Log.debug "[DevReload] Passthrough %s %s — Accept: %s (no HTML)" ctx.Request.Method ctx.Request.Path.Value accept
          do! next.Invoke(ctx)
        | true ->
          // Only strip Accept-Encoding when the request explicitly wants HTML.
          // Without this, ResponseCompression writes gzip bytes to our MemoryStream
          // and the script injection can't find </body> in compressed bytes.
          // API calls with Accept: */* keep their compression untouched.
          match explicitlyWantsHtml with
          | true -> ctx.Request.Headers.Remove("Accept-Encoding") |> ignore
          | false -> ()

          use ms = new MemoryStream()
          let originalBody = ctx.Response.Body
          ctx.Response.Body <- ms

          do! next.Invoke(ctx)

          ms.Position <- 0L

          match shouldInjectScript ctx && ms.Length < maxBufferSize with
          | true ->
            let encoding = parseEncoding ctx.Response.ContentType
            use reader = new StreamReader(ms, encoding, leaveOpen = true)
            let! content = reader.ReadToEndAsync()
            // CSP nonce: when the response has a Content-Security-Policy header,
            // generate a per-request nonce, inject it on the script tag, and
            // add 'nonce-{value}' to the CSP's script-src directive.
            let hasCsp = ctx.Response.Headers.ContainsKey("Content-Security-Policy")
            let scriptToInject, nonce =
              match hasCsp with
              | true ->
                let n = generateNonce()
                injectScriptNonce n script, Some n
              | false -> script, None
            let injected =
              match content.Contains("</body>") with
              | true -> content.Replace("</body>", scriptToInject + "</body>")
              | false ->
                Log.debug "[DevReload] No </body> tag in %s — appending script" ctx.Request.Path.Value
                content + scriptToInject
            match nonce with
            | Some n ->
              let csp = ctx.Response.Headers["Content-Security-Policy"].ToString()
              ctx.Response.Headers["Content-Security-Policy"] <-
                Microsoft.Extensions.Primitives.StringValues (addNonceToCsp n csp)
              Log.debug "[DevReload] Injected CSP nonce for %s" ctx.Request.Path.Value
            | None -> ()
            let bytes = encoding.GetBytes(injected)
            // Prevent browser caching so reloads always fetch fresh content
            setNoCacheHeaders ctx
            ctx.Response.ContentLength <- Nullable(int64 bytes.Length)
            ctx.Response.Body <- originalBody
            do! originalBody.WriteAsync(ReadOnlyMemory bytes)
          | false ->
            match shouldInjectScript ctx with
            | false ->
              Log.debug "[DevReload] Skip inject %s %s — content-type: %s, status: %d"
                ctx.Request.Method ctx.Request.Path.Value
                (ctx.Response.ContentType |> Option.ofObj |> Option.defaultValue "null")
                ctx.Response.StatusCode
            | true ->
              Log.warn "[DevReload] Skip inject %s — body too large (%dKB > %dKB)"
                ctx.Request.Path.Value (ms.Length / 1024L) (maxBufferSize / 1024L)
            ms.Position <- 0L
            ctx.Response.Body <- originalBody
            do! ms.CopyToAsync(originalBody)
    })

/// Convenience alias with port 0 (same-origin relative SSE path, for tests).
let middleware = createMiddleware 0
