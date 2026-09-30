module SageFs.Tests.DaemonLogFileTests

open System
open System.IO
open System.Net.Http
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open SageFs
open SageFs.Server

/// The ASP.NET hosting line every request writes at Information. It is the
/// exact noise that filled the user's disk: 83,349 of these in one 50 MB window.
let private aspNetRequestLine = "Request starting"

/// A category no filter rule mentions, so a line under it proves the file
/// sink is alive rather than silently dropping everything.
let private probeCategory = "SageFs.Tests.DaemonLogFileProbe"
let private probeLine = "probe line the file must keep"

let private tempLogDir () =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-daemon-log-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  dir

let private readAllLogText (dir: string) =
  Directory.GetFiles(dir, "*.log")
  |> Array.sort
  |> Array.map (fun f ->
    use fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    use reader = new StreamReader(fs)
    reader.ReadToEnd())
  |> String.concat "\n"

/// Boot a real Kestrel host wired by `configure`, make one request, log one
/// probe line, then dispose the host so the async file sink flushes. Returns
/// everything the daemon wrote into `dir`.
let private runHostAndReadLog (dir: string) (configure: WebApplicationBuilder -> unit) : Task<string> = task {
  let builder = WebApplication.CreateBuilder([||])
  builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
  configure builder
  let app = builder.Build()
  app.MapGet("/api/sessions", RequestDelegate(fun ctx -> ctx.Response.WriteAsync("[]"))) |> ignore
  do! app.StartAsync()
  let address = app.Urls |> Seq.head
  use client = new HttpClient()
  let! _ = client.GetStringAsync(address + "/api/sessions")
  let logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(probeCategory)
  logger.LogInformation(probeLine)
  do! app.StopAsync()
  do! app.DisposeAsync().AsTask()
  return readAllLogText dir
}

/// Run one request through a host wired by the daemon's configureLogging and
/// return everything that landed in a fresh, private log directory.
let private logTextAfterRequest () : Task<string> = task {
  let dir = tempLogDir ()
  try
    return!
      runHostAndReadLog dir (fun b ->
        DaemonLogging.configure b (DaemonLog.sinkPath dir) DaemonLog.defaultBounds false)
  finally
    try Directory.Delete(dir, true) with _ -> ()
}

/// A tiny bound, so a few dozen lines exercise rolling and retention.
let private tinyBounds : LogBounds = { MaxFileBytes = 2000L; RetainedFiles = 3 }

let private noisyLineCount = 300

/// Serilog.Sinks.File checks the size BEFORE it writes an event, so a file can
/// end one event past its limit. One test line is about 150 bytes.
let private oneEventSlackBytes = 400L

/// Write `noisyLineCount` numbered lines through a file logger bounded by
/// `bounds`, dispose it (which flushes), and return the directory's log files.
let private writeNoisyLog (dir: string) (bounds: LogBounds) : string list =
  let logger = DaemonLogging.createFileLogger (DaemonLog.sinkPath dir) bounds
  for i in 1 .. noisyLineCount do
    logger.Information("line {Number} {Padding}", i, String('x', 100))
  logger.Dispose()
  Directory.GetFiles(dir, "*.log") |> Array.sort |> Array.toList

[<Tests>]
let daemonLogFileTests =
  testList "Daemon log file, booted through the real configureLogging" [
    testTask "WHY — the AspNetCore Warning filter must actually reach the file, so a polled endpoint cannot bury the log" {
      let! text = logTextAfterRequest ()
      text
      |> Expect.stringContains "the file sink is alive, so an absent request line is a real filter" probeLine
      text.Contains aspNetRequestLine
      |> Expect.isFalse "Information-level 'Request starting' lines must not reach the file"
    }

    testCase "WHY — the log is bounded in count and size, so a chatty day cannot become an 11 GB directory" <| fun _ ->
      let dir = tempLogDir ()
      try
        let files = writeNoisyLog dir tinyBounds
        (files.Length <= tinyBounds.RetainedFiles)
        |> Expect.isTrue (sprintf "at most %d files kept, found %d" tinyBounds.RetainedFiles files.Length)
        (files.Length > 1)
        |> Expect.isTrue "the day's file rolled at the size limit instead of stopping"
        for f in files do
          (FileInfo(f).Length <= tinyBounds.MaxFileBytes + oneEventSlackBytes)
          |> Expect.isTrue (sprintf "%s stays within its %d byte limit plus the one event that crosses it" (Path.GetFileName f) tinyBounds.MaxFileBytes)
      finally
        try Directory.Delete(dir, true) with _ -> ()

    testCase "WHY — hitting the size limit must never silence the log: the newest line is always kept" <| fun _ ->
      let dir = tempLogDir ()
      try
        writeNoisyLog dir tinyBounds |> ignore
        readAllLogText dir
        |> Expect.stringContains "the last line written survives rolling" (sprintf "line %d " noisyLineCount)
      finally
        try Directory.Delete(dir, true) with _ -> ()

    testCase "WHY — two daemons on different SAGEFS_DATA_DIR each write only their own directory" <| fun _ ->
      let dirA = tempLogDir ()
      let dirB = tempLogDir ()
      try
        let loggerFor dir = DaemonLogging.createFileLogger (DaemonLog.sinkPath dir) DaemonLog.defaultBounds
        let a = loggerFor dirA
        let b = loggerFor dirB
        a.Information("only-in-a")
        b.Information("only-in-b")
        a.Dispose()
        b.Dispose()
        let textA = readAllLogText dirA
        let textB = readAllLogText dirB
        textA |> Expect.stringContains "A has its own line" "only-in-a"
        textB |> Expect.stringContains "B has its own line" "only-in-b"
        textA.Contains "only-in-b" |> Expect.isFalse "A never sees B's line"
        textB.Contains "only-in-a" |> Expect.isFalse "B never sees A's line"
      finally
        try Directory.Delete(dirA, true) with _ -> ()
        try Directory.Delete(dirB, true) with _ -> ()
  ]
