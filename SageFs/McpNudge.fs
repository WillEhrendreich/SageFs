namespace SageFs

open System.Threading.Tasks
open SageFs.McpTools
open SageFs.Features.Tweak.Nudge

/// The MCP side of `nudge_value`: which files the call's session owns, how a
/// result is told to the caller, and the production wiring of the nudge door
/// (the real disk, the shared per-file locks, the journals under the daemon's
/// data directory). The decisions are `SageFs.Features.Tweak.Nudge`; this adds
/// only what the daemon alone knows.
module McpNudge =

  /// The reply for a run: a JSON object with an `outcome` token, the facts of the
  /// outcome, and `notes`. A refusal is `outcome: "Refused"` with the case token,
  /// the rule it broke and the next action.
  let render (result: Result<Ran, NudgeRefusal>) : string = failwith "not built yet"

  /// Parse `raw` against the files `owned`, run it, and render the reply. No session
  /// lookup: the caller has already said which files the session owns.
  let nudgeWith (owned: OwnedFiles) (ports: Ports) (locks: FileLocks) (raw: RawNudge) : Task<string> =
    failwith "not built yet"

  /// What a worker's `GET /hotreload` says, as the files a session owns: every project file it lists,
  /// and the ones it marks watched. `session` and `workingDirectory` are the call's own.
  let ownedFilesFromJson (session: string) (workingDirectory: string) (hotReloadJson: string) : Result<OwnedFiles, NudgeRefusal> =
    failwith "not built yet"

  /// The files the session a call resolves to owns: its projects' files, and which of
  /// them hot reload is watching.
  let ownedFilesOf (ctx: McpContext) (workingDirectory: string option) : Task<Result<OwnedFiles, NudgeRefusal>> =
    failwith "not built yet"

  /// Where the daemon keeps nudge journals.
  let defaultTweaksDir () : string = failwith "not built yet"

  /// `nudge_value` with journals under `tweaksDir`: resolve the session, then `nudgeWith`
  /// the real disk and the daemon's shared locks.
  let nudgeValueIn (tweaksDir: string) (ctx: McpContext) (workingDirectory: string) (raw: RawNudge) : Task<string> =
    failwith "not built yet"

  /// `nudge_value`: `nudgeValueIn` the daemon's own tweaks directory.
  let nudgeValue (ctx: McpContext) (workingDirectory: string) (raw: RawNudge) : Task<string> =
    nudgeValueIn (defaultTweaksDir ()) ctx workingDirectory raw
