module SageFs.DevReloadMiddleware

open System
open System.IO
open System.Text
open Microsoft.AspNetCore.Http

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
  let sseUrl =
    match workerPort > 0 with
    | true -> sprintf "http://127.0.0.1:%d/__sagefs__/reload" workerPort
    | false -> "/__sagefs__/reload"
  sprintf """<script data-sagefs-injected="devreload">
(function(){
  const s = document.querySelector('script[data-sagefs-injected="devreload"]');
  if (s.dataset.sagefsDup) return;
  s.dataset.sagefsDup = '1';
  const d = document.createElement('div');
  d.id = 'sagefs-reload-indicator';
  d.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;padding:8px 16px;border-radius:8px;font:13px/1.5 system-ui,sans-serif;color:#fff;background:#2563eb;opacity:0;pointer-events:none;transition:opacity .2s;box-shadow:0 2px 12px rgba(0,0,0,.2);max-width:480px;white-space:pre-wrap;word-break:break-word';
  document.body.appendChild(d);
  let reloadCount = 0;
  let reloadTimer = null;
  const safeReload = function() {
    reloadCount++;
    if (reloadCount > 3) {
      d.textContent = '⚠ SageFs: too many reloads — paused. Save again to retry.';
      d.style.background = '#dc2626';
      d.style.opacity = '1';
      console.warn('[SageFs] Reload guard: stopped after ' + reloadCount + ' rapid reloads');
      return;
    }
    clearTimeout(reloadTimer);
    reloadTimer = setTimeout(function(){ reloadCount = 0; }, 5000);
    window.location.reload();
  };
  const es = new EventSource('%s');
  es.onmessage = function(e) {
    try {
      const msg = JSON.parse(e.data);
      console.debug('[SageFs]', msg.type, msg);
      if (msg.type === 'compiling') {
        const label = msg.file ? '⟳ Recompiling ' + msg.file + '...' : '⟳ Recompiling...';
        d.textContent = label;
        d.style.background = '#2563eb';
        d.style.opacity = '1';
      } else if (msg.type === 'reload') {
        d.textContent = '✓ Updated';
        d.style.background = '#16a34a';
        d.style.opacity = '1';
        setTimeout(safeReload, 80);
      } else if (msg.type === 'failed') {
        d.textContent = '✗ ' + (msg.error || 'Compilation failed');
        d.style.background = '#dc2626';
        d.style.opacity = '1';
        console.error('[SageFs] Compilation failed:', msg.error);
      }
    } catch(ex) {
      console.warn('[SageFs] Bad SSE payload:', e.data, ex);
      safeReload();
    }
  };
  es.onerror = function() {
    d.textContent = '⚡ Reconnecting...';
    d.style.background = '#d97706';
    d.style.opacity = '1';
    setTimeout(function(){ d.style.opacity = '0'; }, 3000);
  };
})();
</script>""" sseUrl

let private handledKey = "SageFs.DevReload.Handled"
let private maxBufferSize = 10L * 1024L * 1024L // 10MB

let private shouldInjectScript (ctx: HttpContext) =
  let ct = ctx.Response.ContentType
  not (isNull ct) &&
  ct.Contains("text/html", StringComparison.OrdinalIgnoreCase) &&
  ctx.Response.StatusCode >= 200 &&
  ctx.Response.StatusCode < 300

/// Create a middleware that injects the devreload script into HTML responses.
/// SSE is served by the worker HTTP server — this middleware only does injection.
let createMiddleware (workerPort: int) =
  let script = reloadScript workerPort
  fun (next: RequestDelegate) ->
    RequestDelegate(fun ctx -> task {
      match ctx.Items.ContainsKey(handledKey) with
      | true -> do! next.Invoke(ctx)
      | false ->
        ctx.Items[handledKey] <- true

        let acceptsHtml =
          let accept = ctx.Request.Headers["Accept"].ToString()
          accept.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
          String.IsNullOrEmpty(accept)

        match acceptsHtml with
        | false ->
          do! next.Invoke(ctx)
        | true ->
          use ms = new MemoryStream()
          let originalBody = ctx.Response.Body
          ctx.Response.Body <- ms

          do! next.Invoke(ctx)

          ms.Position <- 0L

          match shouldInjectScript ctx && ms.Length < maxBufferSize with
          | true ->
            use reader = new StreamReader(ms, Encoding.UTF8, leaveOpen = true)
            let! content = reader.ReadToEndAsync()
            let injected =
              match content.Contains("</body>") with
              | true -> content.Replace("</body>", script + "</body>")
              | false -> content + script
            let bytes = Encoding.UTF8.GetBytes(injected)
            ctx.Response.ContentLength <- Nullable(int64 bytes.Length)
            ctx.Response.Body <- originalBody
            do! originalBody.WriteAsync(ReadOnlyMemory bytes)
          | false ->
            ms.Position <- 0L
            ctx.Response.Body <- originalBody
            do! ms.CopyToAsync(originalBody)
    })

/// Convenience alias with port 0 (same-origin relative SSE path, for tests).
let middleware = createMiddleware 0
