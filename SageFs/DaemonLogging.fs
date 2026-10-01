/// The daemon's logging setup: what reaches the file, how big the file may get,
/// and where it lives.
///
/// Serilog.Extensions.Logging.File 9.0.0-dev-02303 (the package `AddFile` used to
/// come from) cannot do this job, and reading its source shows why:
///   * it registers its provider with `AddFilter<SerilogLoggerProvider>(null,
///     Trace)`. MEL picks the rules that name a provider over the rules that name
///     none, so every category rule (`AddFilter("Microsoft.AspNetCore", Warning)`)
///     was ignored for the file. That is why `Request starting` reached the log.
///   * it builds the sink with `rollOnFileSizeLimit = false`, so a file that hits
///     its size limit stops writing until midnight, with no rotation.
/// So the file sink is built here from Serilog directly (both packages are
/// already in the closure): category levels become Serilog overrides, and the
/// sink rolls by day AND by size.
module SageFs.Server.DaemonLogging

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open OpenTelemetry.Logs
open Serilog
open Serilog.Events
open Serilog.Extensions.Logging
open SageFs

/// Which categories are quiet, and how quiet. One table, applied to the file
/// (as Serilog overrides) and to every other provider (as MEL filters), so the
/// two cannot drift apart.
let categoryLevels : (string * LogLevel) list =
  [ "Microsoft.AspNetCore", LogLevel.Warning
    "Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning
    "Microsoft.Hosting", LogLevel.Warning
    "ModelContextProtocol.Server.McpServer", LogLevel.Warning
    "ModelContextProtocol.AspNetCore.SseHandler", LogLevel.Warning
    "SageFs", LogLevel.Information ]

/// One line per event: time, level, the category that wrote it, the message,
/// then the exception with its stack.
[<Literal>]
let private outputTemplate = "{Timestamp:o} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"

/// How often the file sink flushes to disk, so a crash loses seconds, not minutes.
let private flushInterval = Timeouts.logFlushInterval

/// The file logger: daily files that also roll at `bounds.MaxFileBytes`, keeping
/// `bounds.RetainedFiles` of them, at most Information, with `categoryLevels`
/// applied.
let createFileLogger (logPath: string) (bounds: LogBounds) : Serilog.Core.Logger =
  let config =
    LoggerConfiguration()
      .MinimumLevel.Is(LogEventLevel.Information)
      .Enrich.FromLogContext()
  for (category, level) in categoryLevels do
    config.MinimumLevel.Override(category, LevelConvert.ToSerilogLevel level) |> ignore
  config.WriteTo.Async(fun sink ->
    sink.File(
      logPath,
      outputTemplate = outputTemplate,
      fileSizeLimitBytes = Nullable bounds.MaxFileBytes,
      rollingInterval = RollingInterval.Day,
      rollOnFileSizeLimit = true,
      retainedFileCountLimit = Nullable bounds.RetainedFiles,
      shared = true,
      flushToDiskInterval = Nullable flushInterval)
    |> ignore)
  |> ignore
  config.CreateLogger()

/// Wire console, the bounded file, and (when configured) OpenTelemetry.
let configure (builder: WebApplicationBuilder) (logPath: string) (bounds: LogBounds) (otelConfigured: bool) =
  builder.WebHost.ConfigureLogging(fun logging ->
    logging.AddConsole() |> ignore
    logging.AddSerilog(createFileLogger logPath bounds, dispose = true) |> ignore
    for (category, level) in categoryLevels do
      logging.AddFilter(category, level) |> ignore
    match otelConfigured with
    | true ->
      logging.AddOpenTelemetry(fun otel ->
        otel.IncludeFormattedMessage <- true
        otel.IncludeScopes <- true
        otel.AddOtlpExporter() |> ignore
      ) |> ignore
    | false -> ()
  ) |> ignore
