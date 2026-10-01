/// The edge of `SourceState`: the file system reads that turn "a session loaded these projects" into the evidence
/// `SourceState.decide` weighs. Every failure to read becomes a case of the evidence, never an exception and never a
/// default, so a file that cannot be read is reported as one that could not be read.
module SageFs.SourceStateProbe

/// What a session's projects, its worker's warmup and its rebuild record say about whether the build it runs is behind
/// the files on disk. Skeleton: it answers nothing yet.
let probe (rebuild: LastRebuild) (projectFiles: string list) (warmup: WarmupContext option) : SourceState =
  SourceState.Unknown UnknownReason.NotAssessed

/// The same, for a session the registry knows.
let ofSession (info: WorkerProtocol.SessionInfo) (warmup: WarmupContext option) : SourceState =
  probe info.Rebuild (info.ProjectRoles |> List.map (fun p -> p.Path)) warmup
