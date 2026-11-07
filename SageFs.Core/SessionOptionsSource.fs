namespace SageFs

open System.IO
open SageFs.Utils

/// Where a session's options come from: the `.SageFs/config.fsx` of the directory the worker runs in. The worker
/// reads it itself, so a session gets its options whichever client created it. The script is arbitrary F#, so it is
/// evaluated by `ConfigHost` in an isolated host and never in this process; an answer is cached by the script's text.
module SessionOptionsSource =

  /// The options a directory's config asks for. No config file, or one that does not evaluate, gives
  /// `SessionOptions.none` (the failure is logged, as the daemon's own read of the same file logs it). Entries the
  /// options refuse are logged with their reason and the rest are kept.
  let read (logger: ILogger) (workingDir: string) : Async<SessionOptions.Options> =
    async {
      let path = Path.Combine(workingDir, ".SageFs", "config.fsx")
      match File.Exists path with
      | false -> return SessionOptions.none
      | true ->
        match! ConfigHost.evaluateAsync workingDir (File.ReadAllText path) with
        | Error error ->
          logger.LogWarning(sprintf "  Could not read session options from %s: %s (using defaults)" path (ConfigHost.describeError error))
          return SessionOptions.none
        | Ok config ->
          let options, refusals = SessionOptions.ofConfig config
          for refusal in refusals do
            logger.LogWarning("  " + SessionOptions.describe refusal)
          return options
    }
