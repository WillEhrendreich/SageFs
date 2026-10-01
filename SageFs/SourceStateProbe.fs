/// The edge of `SourceState`: the file system reads that turn "a session loaded these projects" into the evidence
/// `SourceState.decide` weighs. Every failure to read becomes a case of the evidence, never an exception and never a
/// default, so a file that cannot be read is reported as one that could not be read.
module SageFs.SourceStateProbe

open System
open System.IO
open System.Xml.Linq

/// A file and its last-write time. `File.GetLastWriteTimeUtc` answers the year 1601 for a file that is not there and for one
/// behind a directory that cannot be entered, and a stamp of 1601 reads as "older than any build". `GetAttributes` throws for
/// both, so the stat that fails is the one that says why.
let private stampOf (path: string) : StampedFile =
  try
    File.GetAttributes path |> ignore
    { Path = path; Stamp = StampRead.Written (File.GetLastWriteTimeUtc path) }
  with ex -> { Path = path; Stamp = StampRead.Unreadable ex.Message }

/// What a project file says builds into its assembly.
type private Listing =
  { AssemblyName: string
    Sources: string list }

/// An item that names no single file: a wildcard, or an MSBuild property or item reference.
let private needsEvaluation (item: string) : bool =
  item.IndexOfAny [| '*'; '?'; '$'; '@'; '%' |] >= 0

/// The assembly name and the Compile items an .fsproj lists. A wildcard or an MSBuild property in a Compile item cannot be
/// listed without evaluating the project, and a guess at what the build contained is exactly what this must not make.
let private listProject (projectFile: string) : Result<Listing, string> =
  try
    let doc = XDocument.Load projectFile
    let projectDir = Path.GetDirectoryName (Path.GetFullPath projectFile)
    let named (name: string) = doc.Descendants() |> Seq.filter (fun element -> element.Name.LocalName = name)
    let includes =
      named "Compile"
      |> Seq.choose (fun element -> match element.Attribute "Include" with null -> None | attribute -> Some attribute.Value)
      |> List.ofSeq
    match includes |> List.tryFind needsEvaluation with
    | Some item -> Result.Error (sprintf "it lists a Compile item with a wildcard or an MSBuild property, which cannot be listed without evaluating the project (%s)" item)
    | None ->
      let assemblyName =
        named "AssemblyName"
        |> Seq.tryHead
        |> Option.map (fun element -> element.Value.Trim())
        |> Option.filter (fun name -> name <> "" && not (needsEvaluation name))
        |> Option.defaultValue (Path.GetFileNameWithoutExtension projectFile)
      Result.Ok
        { AssemblyName = assemblyName
          Sources = includes |> List.map (fun item -> Path.GetFullPath (Path.Combine (projectDir, item.Replace ('\\', '/')))) }
  with ex -> Result.Error ex.Message

/// What the edge read about one project: its project file and Compile items, and the build output the worker loaded.
let private evidenceFor (warmup: WarmupContext) (projectFile: string) : ProjectEvidence =
  match listProject projectFile with
  | Result.Error reason -> ProjectEvidence.NotInspectable (projectFile, reason)
  | Result.Ok listing ->
    match warmup.AssembliesLoaded |> List.tryFind (fun loaded -> String.Equals (loaded.Name, listing.AssemblyName, StringComparison.OrdinalIgnoreCase)) with
    | None -> ProjectEvidence.NotInspectable (projectFile, sprintf "the worker loaded no assembly named %s" listing.AssemblyName)
    | Some assembly ->
      ProjectEvidence.Inspected (projectFile, stampOf assembly.Path, projectFile :: listing.Sources |> List.map stampOf)

/// What a session's projects, its worker's warmup and its rebuild record say about whether the build it runs is behind the
/// files on disk. A rebuild in progress answers without reading the disk, and a worker that did not report its warmup leaves
/// nothing to compare the files to.
let probe (rebuild: LastRebuild) (projectFiles: string list) (warmup: WarmupContext option) : SourceState =
  match rebuild, warmup with
  | LastRebuild.Latest (RebuildOutcome.InProgress _), _ ->
    SourceState.decide rebuild (LoadedAt.NotReported "a rebuild is in progress") []
  | _, None ->
    let reason = "the worker did not report its warmup"
    SourceState.decide rebuild (LoadedAt.NotReported reason) (projectFiles |> List.map (fun project -> ProjectEvidence.NotInspectable (project, reason)))
  | _, Some context ->
    SourceState.decide rebuild (LoadedAt.Reported context.StartedAt.UtcDateTime) (projectFiles |> List.map (evidenceFor context))

/// The same, for a session the registry knows.
let ofSession (info: WorkerProtocol.SessionInfo) (warmup: WarmupContext option) : SourceState =
  probe info.Rebuild (info.ProjectRoles |> List.map (fun p -> p.Path)) warmup

/// The same, for a session id the registry may not know: a session with no record has no build the daemon can name.
let ofSessionRecord (info: WorkerProtocol.SessionInfo option) (warmup: WarmupContext option) : SourceState =
  match info with
  | Some session -> ofSession session warmup
  | None -> SourceState.Unknown (UnknownReason.LoadTimeNotReported "the registry has no record of the session")

/// What `targeted_verify` makes of a source state: current only when it is in sync, stale when files changed after the build,
/// and unknown otherwise (a rebuild in progress included), which it refuses just as it refuses stale. `artifact` names what
/// was loaded.
let loadedDefinitionOf (artifact: string) (source: SourceState) : Features.Verification.LoadedDefinitionState =
  match source with
  | SourceState.InSync _ -> Features.Verification.LoadedDefinitionState.ConfirmedCurrent artifact
  | SourceState.Stale (first :: _) ->
    Features.Verification.LoadedDefinitionState.ConfirmedStale (first.Path, "the build this session loaded")
  | SourceState.Stale [] -> Features.Verification.LoadedDefinitionState.UnknownLoadState "the source is stale, but no file was named"
  | SourceState.Rebuilding _
  | SourceState.Unknown _ -> Features.Verification.LoadedDefinitionState.UnknownLoadState (SourceState.describe source)

/// `list_sessions`' text: each session as `SessionOperations.formatSessionInfo` says it, with its source line under it. A
/// session the sources map has no reading for says it was not read, never that it is in sync.
let formatSessionList
  (now: DateTime)
  (occupancy: Map<string, SessionOperations.SessionOccupancy list> option)
  (sources: Map<string, SourceState>)
  (sessions: WorkerProtocol.SessionInfo list)
  : string =
  match sessions with
  | [] -> "No active sessions."
  | _ ->
    sessions
    |> List.map (fun info ->
      let sid = WorkerProtocol.SessionId.value info.Id
      let occ = occupancy |> Option.map (fun m -> m |> Map.tryFind sid |> Option.defaultValue [])
      let source = sources |> Map.tryFind sid |> Option.defaultValue (SourceState.Unknown UnknownReason.NotAssessed)
      sprintf "%s\n  %s" (SessionOperations.formatSessionInfo now occ info) (SourceState.listLine source))
    |> String.concat "\n\n"
    |> sprintf "%d active session(s):\n\n%s" sessions.Length
