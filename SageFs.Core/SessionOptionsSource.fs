namespace SageFs

open SageFs.Utils

/// Where a worker gets its session's options: the one environment variable the daemon started it with
/// (`SessionOptions.carrierVariable`). The worker does not read `.SageFs/config.fsx`: the daemon resolved the options when the
/// session was created, and the builds the worker starts and its project loader read the same variable, so all of them agree.
module SessionOptionsSource =

  /// The options this worker was started with. No carrier is no options. A carrier that cannot be read is logged with the reason
  /// and the session runs with the defaults, so a damaged variable is said once at start, never silently.
  let fromCarrier (logger: ILogger) : Async<SessionOptions.Options> =
    async {
      match SessionOptions.fromEnvironment System.Environment.GetEnvironmentVariable with
      | Ok options -> return options
      | Error error ->
        logger.LogWarning(sprintf "  Could not read the session options this worker was started with: %s (using defaults)" (JsonError.describe error))
        return SessionOptions.none
    }
