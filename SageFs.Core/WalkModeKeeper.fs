namespace SageFs

open SageFs.Features

/// How much of a class the live-values walk may run, for ONE worker. The worker's single source of truth: a session is told the
/// mode before it is first read, so a reset or a hard reset (which makes a new session that starts in the standard mode) never
/// silently changes it back. Until somebody chooses, nothing is sent, because a new session already starts in the standard mode.
[<Sealed>]
type WalkModeKeeper() =
  let gate = obj ()
  let mutable mode = ValueWalk.standard
  let mutable origin = LiveBindingsPane.StartedWithDefault
  let mutable told : FsiSession.IFsiSession = null

  /// Somebody chose a mode. The live session is told at its next read, and so is any session made after it.
  member _.Choose(chosen: ValueWalk) : unit =
    lock gate (fun () ->
      mode <- chosen
      origin <- LiveBindingsPane.ChosenByUser
      told <- null)

  /// Tell `session` the chosen mode if it has not been told, and say so through `warn` if it could not be.
  member _.Ensure(session: FsiSession.IFsiSession, warn: string -> unit) : unit =
    let toSend =
      lock gate (fun () ->
        match origin, obj.ReferenceEquals(told, session) with
        | LiveBindingsPane.ChosenByUser, false ->
          told <- session
          [ mode ]
        | LiveBindingsPane.ChosenByUser, true
        | LiveBindingsPane.StartedWithDefault, _ -> [])
    for chosen in toSend do
      match session.SetWalkMode(ValueWalk.toWalkMode chosen) with
      | FsiSession.WalkModeNow _ -> ()
      | FsiSession.WalkModeNotSet reason ->
        warn (sprintf "The live-values walk mode could not be set: %s" (SageFs.FsiHost.FsiProtocol.MemberClick.describeUnavailable reason))

  /// Run one clicked getter on `session` (null when there is none), as the JSON of a MemberOutcome. A click is never an
  /// exception: it is a typed outcome saying why it was not run.
  member this.EvaluateMember(session: FsiSession.IFsiSession, binding: string, path: string list, warn: string -> unit) : string =
    let unavailable (reason: SageFs.FsiHost.FsiProtocol.MemberUnavailableReason) =
      WorkerProtocol.Serialization.serialize (SageFs.FsiHost.FsiProtocol.MemberUnavailable reason)
    match session with
    | null -> unavailable (SageFs.FsiHost.FsiProtocol.HostNotRunning "the session is not active")
    | session ->
      try
        this.Ensure(session, warn)
        session.EvaluateMember(binding, path)
      with ex -> unavailable (SageFs.FsiHost.FsiProtocol.HostNotRunning ex.Message)

  /// The reading a pull answers with: the snapshot (already JSON), the mode it was walked in and who chose the mode.
  member _.ReadingJson(snapshotJson: string) : string =
    lock gate (fun () -> LiveBindingsPane.readingJson mode origin snapshotJson)

  /// The reading of a worker with no session to walk.
  member this.EmptyReadingJson() : string =
    this.ReadingJson(WorkerProtocol.Serialization.serialize (LiveValueTree.buildSnapshot "" 0L []))
