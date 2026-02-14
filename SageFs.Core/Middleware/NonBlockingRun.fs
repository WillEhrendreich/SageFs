module SageFs.Middleware.NonBlockingRun

open System
open SageFs.AppState
open SageFs.Utils

// Middleware to automatically wrap blocking .Run() calls in non-blocking wrappers
// Works for any application: ASP.NET, Aspire, or any app with a .Run() method
let nonBlockingRunMiddleware next (request, st: AppState) =
  let code = request.Code.Trim()
  
  // Detect patterns like: app.Run(), builder.Build().Run(), etc.
  let hasBlockingRun = 
    code.Contains(".Run()") && 
    not (code.Contains(".RunAsync()")) &&
    not (code.Contains("Task.Run")) &&
    not (code.Contains("runFromRepl"))
  
  if hasBlockingRun then
    st.Logger.LogDebug "Detected blocking .Run() call, converting to non-blocking execution..."
    
    // Check if this is a multi-line file (contains newlines) with .Build().Run() at the end
    let lines = code.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    let isMultiLine = lines.Length > 1
    
    let rewrittenCode =
      if isMultiLine && code.TrimEnd().EndsWith(".Build().Run()") then
        // For multi-line files, only replace the last .Build().Run()
        let lastRunIndex = code.LastIndexOf(".Build().Run()")
        let beforeLastRun = code.Substring(0, lastRunIndex)
        
        // Check if this is an Aspire app (contains DistributedApplication)
        let isAspire = code.Contains("DistributedApplication")
        
        let configInjection = 
          if isAspire then
            // Inject Aspire DCP and Dashboard paths from environment
            """
// SageFs: Auto-configure Aspire paths
let dcpPath = System.Environment.GetEnvironmentVariable("DcpPublisherSettings__CliPath")
let dashPath = System.Environment.GetEnvironmentVariable("DcpPublisherSettings__DashboardPath")
if not (isNull dcpPath) && dcpPath <> "" then
  builder.Configuration["DcpPublisher:CliPath"] <- dcpPath
if not (isNull dashPath) && dashPath <> "" then
  builder.Configuration["DcpPublisher:DashboardPath"] <- dashPath
"""
          else
            ""
            
        $"""{beforeLastRun}{configInjection}

let __app = builder.Build()
printfn "Starting application in background..."
let __appTask = System.Threading.Tasks.Task.Run(fun () -> 
  try 
    __app.Run() 
  with ex -> 
    eprintfn "Application error: %%s" ex.Message
)
// Don't wait - return immediately so REPL is responsive
printfn "✓ Application starting in background."
printfn "  Use '__app.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously' to stop"
__app
"""
      elif not isMultiLine && code.Contains(".Build().Run()") then
        // Single-line command: builder.Build().Run()
        let beforeRun = code.Replace(".Build().Run()", ".Build()")
        $"""
let __app = {beforeRun}
printfn "Starting application in background..."
let __appTask = System.Threading.Tasks.Task.Run(fun () -> 
  try 
    __app.Run() 
  with ex -> 
    eprintfn "Application error: %%s" ex.Message
)
printfn "✓ Application starting in background."
printfn "  Use '__app.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously' to stop"
__app
"""
      elif code.EndsWith(".Run()") then
        // Pattern: someApp.Run()
        let appVar = code.Replace(".Run()", "")
        $"""
printfn "Starting application in background..."
let __appTask = System.Threading.Tasks.Task.Run(fun () -> 
  try 
    ({appVar}).Run() 
  with ex -> 
    eprintfn "Application error: %%s" ex.Message
)
printfn "✓ Application running in background"
{appVar}
"""
      else
        code
    
    let newRequest = { request with Code = rewrittenCode }
    next (newRequest, st)
  else
    next (request, st)
