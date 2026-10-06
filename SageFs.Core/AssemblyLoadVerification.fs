/// Verify that a session's project assemblies actually loaded into the FSI AppDomain, and name the
/// ones that did not.
///
/// WHY THIS EXISTS. FSI surfaces `-r` load failures only as init stderr warnings, which previously
/// produced "Ready" sessions where every project open failed with 'not defined' while `get_fsi_status`
/// claimed warmup was complete (friction report 2026-08). A session with zero project assemblies is
/// dead; reporting it Ready destroys agent trust in every downstream signal.
///
/// WHY IT LIVES IN ITS OWN FILE. `AppState.fs` is ratcheted by line count and was at 1998 of 2000, so
/// any addition to it is a budget decision. The repo's own precedent — the comments above that file's
/// `<Compile>` entry — is that a coherent block comes out here rather than the budget being raised
/// over it. This is that block, moved verbatim apart from becoming a `Result` so the caller decides
/// what failing means.
///
/// `Error` is fatal: the caller raises, because a session that loaded nothing must never report Ready.
/// A partial load only WARNS and names what is missing — a session missing two projects out of ten is
/// still useful, and saying which two is more use than refusing to run.
module SageFs.AssemblyLoadVerification

open SageFs.Utils
open SageFs.WarmUp

/// `fsiErrors` is read only on the fatal path, so the caller passes a thunk that costs nothing unless
/// something actually went wrong.
///
/// The loaded-names argument is deliberately unannotated: the match below is what pins its type, so
/// this file need not track a union defined elsewhere. `ILogger` is `SageFs.Utils.ILogger`, NOT
/// `Microsoft.Extensions.Logging.ILogger` — naming the wrong one compiles the call site into a type
/// error at the other end.
let verify
  (logger: ILogger)
  (fsiErrors: unit -> string)
  (expectedAssemblies: string list)
  loadedNames
  : Result<unit, string> =
  match loadedNames with
  | HostAgent.AgentUnavailable reason ->
    let msg = sprintf "Warmup verification failed: the session could not report what it loaded: %s" reason
    logger.LogError(sprintf "  ❌ %s" msg)
    Error msg
  | HostAgent.AgentAnswered names ->
    match WarmUp.classifyAssemblyLoad expectedAssemblies names with
    | WarmUp.AllExpectedLoaded -> Ok()
    | WarmUp.PartiallyLoaded missing ->
      logger.LogWarning(
        sprintf
          "  ⚠️ Assembly verification: %d/%d project assemblies loaded; MISSING: %s — code touching these will fail with 'not defined'"
          (expectedAssemblies.Length - missing.Length)
          expectedAssemblies.Length
          (String.concat ", " missing))
      Ok()
    | WarmUp.NothingLoaded ->
      let fsiErrorText = fsiErrors ()
      let msg =
        sprintf
          "Warmup verification failed: NONE of %d project assemblies loaded into FSI (expected: %s).%s"
          expectedAssemblies.Length
          (String.concat ", " expectedAssemblies)
          (match fsiErrorText.Length > 0 with
           | true -> sprintf " FSI init errors: %s" fsiErrorText
           | false -> "")
      logger.LogError(sprintf "  ❌ %s" msg)
      Error msg
