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
  const style = document.createElement('style');
  style.textContent = '@keyframes sagefs-shake{0%%,100%%{transform:translateX(0)}25%%{transform:translateX(-4px)}75%%{transform:translateX(4px)}} #sagefs-error-panel{font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace;font-size:12px;line-height:1.5} #sagefs-error-panel .sf-diag{margin:6px 0;padding:8px;background:rgba(0,0,0,.15);border-radius:4px;border-left:3px solid #f87171} #sagefs-error-panel .sf-diag-warn{border-left-color:#fbbf24} #sagefs-error-panel .sf-loc{color:#93c5fd;font-weight:600;margin-bottom:2px} #sagefs-error-panel .sf-msg{color:#fecaca} #sagefs-error-panel .sf-code{color:#a5b4fc;font-size:11px} #sagefs-error-panel .sf-close{position:absolute;top:8px;right:12px;background:none;border:none;color:#fff;font-size:18px;cursor:pointer;opacity:.7;padding:0 4px} #sagefs-error-panel .sf-close:hover{opacity:1} #sagefs-error-panel .sf-summary{padding:6px 0;color:#d1d5db;font-size:11px;border-top:1px solid rgba(255,255,255,.1);margin-top:6px}';
  document.head.appendChild(style);
  const d = document.createElement('div');
  d.id = 'sagefs-reload-indicator';
  d.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;padding:8px 16px;border-radius:8px;font:13px/1.5 system-ui,sans-serif;color:#fff;background:#2563eb;opacity:0;pointer-events:none;transition:opacity .2s;box-shadow:0 2px 12px rgba(0,0,0,.2);max-width:480px;white-space:pre-wrap;word-break:break-word';
  document.body.appendChild(d);
  let reloadCount = 0;
  let reloadTimer = null;
  let reconnectTimer = null;
  let compilingStart = null;
  let compilingTimer = null;
  let compilingLabel = '';
  try { const sy = sessionStorage.getItem('__sagefs_scrollY'); if (sy) { window.scrollTo(0, parseInt(sy)); sessionStorage.removeItem('__sagefs_scrollY'); } } catch(e) {}
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
    try { sessionStorage.setItem('__sagefs_scrollY', '' + window.scrollY); } catch(e) {}
    window.location.reload();
  };
  // Render structured diagnostics as a rich error panel (Elm/Vite quality).
  // Chesterton's fence: the panel renders source-mapped line numbers (LineOffset
  // already applied server-side) so users see their actual source positions,
  // not FSI-internal line numbers from preprocessed code.
  const renderErrorPanel = function(msg) {
    const diags = msg.diagnostics;
    if (!diags || !diags.length) {
      d.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;padding:8px 16px;border-radius:8px;font:13px/1.5 system-ui,sans-serif;color:#fff;background:#dc2626;opacity:1;pointer-events:auto;transition:opacity .2s;box-shadow:0 2px 12px rgba(0,0,0,.2);max-width:480px;white-space:pre-wrap;word-break:break-word';
      d.textContent = '✗ ' + (msg.error || 'Compilation failed');
      return;
    }
    d.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;padding:12px 16px;border-radius:8px;font:13px/1.5 system-ui,sans-serif;color:#fff;background:#1e1e2e;opacity:1;pointer-events:auto;transition:opacity .2s;box-shadow:0 4px 24px rgba(0,0,0,.4);max-width:560px;max-height:60vh;overflow-y:auto';
    d.id = 'sagefs-error-panel';
    const errors = diags.filter(function(x){ return x.Severity === 'error'; }).length;
    const warnings = diags.filter(function(x){ return x.Severity === 'warning'; }).length;
    let html = '<div style="display:flex;align-items:center;gap:8px;margin-bottom:4px"><span style="font-size:15px;font-weight:700">✗ Compilation failed</span><button class="sf-close" onclick="this.parentElement.parentElement.style.opacity=0;this.parentElement.parentElement.style.pointerEvents=\'none\'" title="Dismiss">×</button></div>';
    for (let i = 0; i < diags.length; i++) {
      const dg = diags[i];
      const cls = dg.Severity === 'warning' ? 'sf-diag sf-diag-warn' : 'sf-diag';
      const icon = dg.Severity === 'warning' ? '⚠' : '✗';
      const code = dg.DiagCode ? ' ' + dg.DiagCode : '';
      html += '<div class="' + cls + '">';
      html += '<div class="sf-loc">' + icon + ' ' + (dg.File || '') + ':' + dg.Line + ':' + dg.Column + '<span class="sf-code">' + code + '</span></div>';
      html += '<div class="sf-msg">' + dg.Message.replace(/</g, '&lt;').replace(/>/g, '&gt;') + '</div>';
      html += '</div>';
    }
    html += '<div class="sf-summary">';
    if (errors) html += errors + ' error' + (errors > 1 ? 's' : '');
    if (errors && warnings) html += ', ';
    if (warnings) html += warnings + ' warning' + (warnings > 1 ? 's' : '');
    const dur = compilingStart ? ' · ' + ((Date.now() - compilingStart) / 1000).toFixed(1) + 's' : '';
    html += dur + '</div>';
    d.innerHTML = html;
  };
  // Preserve form inputs across reloads (Carmack's pragmatic suggestion).
  // Chesterton's fence: saves/restores <input>, <textarea>, <select> values
  // via sessionStorage. Prevents the paper cut of losing form state when the
  // user saves an F# file to fix a CSS class. Limited to 15 lines, not a
  // framework — stops at form inputs per expert panel guidance.
  const saveFormState = function() {
    try {
      const inputs = {};
      document.querySelectorAll('input[name],textarea[name],select[name]').forEach(function(el) {
        if (el.type === 'checkbox' || el.type === 'radio') inputs[el.name + '__' + el.value] = el.checked;
        else inputs[el.name] = el.value;
      });
      sessionStorage.setItem('__sagefs_forms', JSON.stringify(inputs));
    } catch(e) {}
  };
  const restoreFormState = function() {
    try {
      const raw = sessionStorage.getItem('__sagefs_forms');
      if (!raw) return;
      sessionStorage.removeItem('__sagefs_forms');
      const inputs = JSON.parse(raw);
      Object.keys(inputs).forEach(function(key) {
        const isCheck = key.indexOf('__') > -1;
        if (isCheck) {
          const parts = key.split('__');
          const el = document.querySelector('[name="' + parts[0] + '"][value="' + parts[1] + '"]');
          if (el) el.checked = inputs[key];
        } else {
          const el = document.querySelector('[name="' + key + '"]');
          if (el) el.value = inputs[key];
        }
      });
    } catch(e) {}
  };
  restoreFormState();
  let connectionEstablished = false;
  const es = new EventSource('%s');
  es.onopen = function() {
    connectionEstablished = true;
    d.id = 'sagefs-reload-indicator';
    d.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;padding:8px 16px;border-radius:8px;font:13px/1.5 system-ui,sans-serif;color:#fff;background:#16a34a;opacity:1;pointer-events:none;transition:opacity .2s;box-shadow:0 2px 12px rgba(0,0,0,.2);max-width:480px;white-space:pre-wrap;word-break:break-word';
    d.textContent = '✓ SageFs connected';
    setTimeout(function(){ d.style.opacity = '0'; }, 2000);
    console.debug('[SageFs] Connected to hot-reload SSE');
  };
  d.textContent = '⟳ Connecting to SageFs...';
  d.style.opacity = '1';
  const connectionTimeout = setTimeout(function() {
    if (!connectionEstablished) {
      d.style.background = '#dc2626';
      d.style.opacity = '1';
      d.style.pointerEvents = 'auto';
      d.textContent = '⚠ Could not connect to SageFs hot-reload.';
      console.warn('[SageFs] Connection timeout — hot-reload SSE did not connect within 3 seconds');
    }
  }, 3000);
  es.onmessage = function(e) {
    clearTimeout(reconnectTimer);
    clearTimeout(connectionTimeout);
    try {
      const msg = JSON.parse(e.data);
      console.debug('[SageFs]', msg.type, msg);
      if (msg.type === 'compiling') {
        compilingLabel = msg.file ? '⟳ Recompiling ' + msg.file + '...' : '⟳ Recompiling...';
        compilingStart = Date.now();
        d.id = 'sagefs-reload-indicator';
        d.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;padding:8px 16px;border-radius:8px;font:13px/1.5 system-ui,sans-serif;color:#fff;background:#2563eb;opacity:1;pointer-events:none;transition:opacity .2s;box-shadow:0 2px 12px rgba(0,0,0,.2);max-width:480px;white-space:pre-wrap;word-break:break-word';
        d.textContent = compilingLabel;
        d.style.animation = '';
        clearInterval(compilingTimer);
        compilingTimer = setInterval(function() {
          const elapsed = ((Date.now() - compilingStart) / 1000).toFixed(1);
          d.textContent = compilingLabel + ' (' + elapsed + 's)';
        }, 200);
      } else if (msg.type === 'reload') {
        clearInterval(compilingTimer);
        const dur = compilingStart ? ' in ' + ((Date.now() - compilingStart) / 1000).toFixed(1) + 's' : '';
        d.id = 'sagefs-reload-indicator';
        d.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;padding:8px 16px;border-radius:8px;font:13px/1.5 system-ui,sans-serif;color:#fff;background:#16a34a;opacity:1;pointer-events:none;transition:opacity .2s;box-shadow:0 2px 12px rgba(0,0,0,.2);max-width:480px;white-space:pre-wrap;word-break:break-word';
        d.textContent = '✓ Updated' + dur;
        d.style.animation = '';
        saveFormState();
        safeReload();
      } else if (msg.type === 'failed') {
        clearInterval(compilingTimer);
        renderErrorPanel(msg);
        d.style.animation = 'sagefs-shake 0.3s';
        setTimeout(function(){ d.style.animation = ''; }, 300);
        console.error('[SageFs] Compilation failed:', msg.error, msg.diagnostics);
      }
    } catch(ex) {
      console.warn('[SageFs] Bad SSE payload:', e.data, ex);
      safeReload();
    }
  };
  es.onerror = function() {
    clearInterval(compilingTimer);
    d.id = 'sagefs-reload-indicator';
    d.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;padding:8px 16px;border-radius:8px;font:13px/1.5 system-ui,sans-serif;color:#fff;background:#d97706;opacity:1;pointer-events:none;transition:opacity .2s;box-shadow:0 2px 12px rgba(0,0,0,.2);max-width:480px;white-space:pre-wrap;word-break:break-word';
    d.textContent = '⚡ Reconnecting...';
    reconnectTimer = setTimeout(function(){ d.style.opacity = '0'; }, 3000);
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
            let injected =
              match content.Contains("</body>") with
              | true -> content.Replace("</body>", script + "</body>")
              | false -> content + script
            let bytes = encoding.GetBytes(injected)
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
