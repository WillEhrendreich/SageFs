module SageFs.DevReloadMiddleware

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

let private reloadScript =
  """<script>
(function(){
  var es=new EventSource('/__sagefs__/reload');
  es.onmessage=function(){ window.location.reload(); };
  es.onerror=function(){ setTimeout(function(){ es.close(); window.location.reload(); },2000); };
})();
</script>"""

let private sseHandler (ctx: HttpContext) = task {
  ctx.Response.ContentType <- "text/event-stream"
  ctx.Response.Headers["Cache-Control"] <- "no-cache"
  ctx.Response.Headers["X-Accel-Buffering"] <- "no"
  do! ctx.Response.Body.FlushAsync()
  let id = Guid.NewGuid().ToString()
  let tcs = DevReload.registerClient id
  ctx.RequestAborted.Register(fun () -> DevReload.unregisterClient id) |> ignore
  do! tcs.Task
  let bytes = Encoding.UTF8.GetBytes("data: reload\n\n")
  do! ctx.Response.Body.WriteAsync(ReadOnlyMemory bytes)
  do! ctx.Response.Body.FlushAsync()
}

/// ASP.NET Core middleware factory.
/// Wire it up with: app.UseMiddleware<DevReloadMiddlewareImpl>()
/// or via Falco: webHost [||] { use_middleware middleware ... }
let middleware (next: RequestDelegate) =
  RequestDelegate(fun ctx -> task {
    if ctx.Request.Path.Value = "/__sagefs__/reload" then
      do! sseHandler ctx
    else
      use ms = new MemoryStream()
      let originalBody = ctx.Response.Body
      ctx.Response.Body <- ms
      do! next.Invoke(ctx)
      ms.Position <- 0L
      let responseContentType = ctx.Response.Headers["Content-Type"].ToString()
      let shouldInject =
        responseContentType.Contains("text/html") &&
        ctx.Response.StatusCode >= 200 &&
        ctx.Response.StatusCode < 300
      if shouldInject then
        let content = (new StreamReader(ms, Encoding.UTF8, leaveOpen = true)).ReadToEnd()
        let injected =
          if content.Contains("</body>") then
            content.Replace("</body>", reloadScript + "</body>")
          else
            content
        let bytes = Encoding.UTF8.GetBytes(injected)
        ctx.Response.ContentLength <- Nullable(int64 bytes.Length)
        ctx.Response.Body <- originalBody
        do! originalBody.WriteAsync(ReadOnlyMemory bytes)
      else
        ms.Position <- 0L
        ctx.Response.Body <- originalBody
        do! ms.CopyToAsync(originalBody)
  })
