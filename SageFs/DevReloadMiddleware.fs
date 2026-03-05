module SageFs.DevReloadMiddleware

open System
open System.IO
open System.Text
open Microsoft.AspNetCore.Http

/// Generate the reload script. Port > 0 connects cross-origin to the worker's
/// SSE endpoint; port 0 falls back to a same-origin relative path (for tests).
let reloadScript (workerPort: int) =
  let sseUrl =
    match workerPort > 0 with
    | true -> sprintf "http://127.0.0.1:%d/__sagefs__/reload" workerPort
    | false -> "/__sagefs__/reload"
  sprintf """<script data-sagefs-injected="devreload">
(function(){
  if (document.querySelector('script[data-sagefs-injected="devreload"]').dataset.sagefsDup) return;
  document.querySelector('script[data-sagefs-injected="devreload"]').dataset.sagefsDup = '1';
  var d = document.createElement('div');
  d.id = 'sagefs-reload-indicator';
  d.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;padding:6px 14px;border-radius:6px;font:13px/1.4 system-ui,sans-serif;color:#fff;background:#2563eb;opacity:0;pointer-events:none;transition:opacity .2s';
  document.body.appendChild(d);
  var es = new EventSource('%s');
  es.onmessage = function(e) {
    try {
      var msg = JSON.parse(e.data);
      if (msg.type === 'compiling') {
        d.textContent = '⟳ Recompiling...';
        d.style.background = '#2563eb';
        d.style.opacity = '1';
      } else if (msg.type === 'reload') {
        d.textContent = '✓ Updated';
        d.style.background = '#16a34a';
        d.style.opacity = '1';
        setTimeout(function(){ window.location.reload(); }, 150);
      }
    } catch(ex) {
      window.location.reload();
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
