/// The new-session dialog's one read of the disk: which projects a directory holds, what frameworks each
/// declares, which workflow a web project would suggest, and which live sessions already work there.
///
/// Everything it needs to decide with is in `NewSessionDialog`. What is here is the reading, kept apart so the
/// decisions stay pure. It reuses the discovery the rest of the dashboard already trusts
/// (`DashboardTypes.discoverProjects`: bounded walk, noise folders pruned) and `ProjectCompatibility`'s reader
/// for target frameworks, so the dialog cannot offer a project the daemon would not see.
module SageFs.Server.NewSessionDiscovery

open System
open System.IO
open System.Xml.Linq
open SageFs
open SageFs.WorkflowTypes
open SageFs.Server.DashboardTypes
open SageFs.Server.NewSessionDialog

let private readProjectFile (path: string) : string =
  try File.ReadAllText path
  with _ -> ""

/// What a project file says about the frameworks it builds for.
let frameworksOf (projectXml: string) : Frameworks =
  match ProjectCompatibility.readTargetFrameworks projectXml with
  | Ok tfms -> Frameworks.Declared tfms
  | Error _ -> Frameworks.NamedByImports

/// The package names a project file asks for plus the markers its own XML carries (the Web SDK), which is
/// what `WorkflowDetection.suggest` reads.
let private packageNamesOf (projectXml: string) : string list =
  let declared =
    try
      XDocument.Parse(projectXml).Descendants(XName.Get "PackageReference")
      |> Seq.choose (fun el -> el.Attribute(XName.Get "Include") |> Option.ofObj |> Option.map (fun a -> a.Value))
      |> Seq.toList
    with _ -> []
  declared @ ProjectFileMarkers.parse projectXml

/// The candidates under `directory`: solutions first, then projects, each with its frameworks.
let candidatesOf (directory: string) : Candidate list * string list list =
  let discovered = discoverProjects directory
  let solutions =
    discovered.Solutions
    |> List.map (fun name -> { Path = name; Kind = CandidateKind.Solution; Frameworks = Frameworks.WholeSolution })
  let projectXml =
    discovered.Projects
    |> List.map (fun relative -> relative, readProjectFile (Path.Combine(directory, relative)))
  let projects =
    projectXml
    |> List.map (fun (relative, xml) -> { Path = relative; Kind = CandidateKind.Project; Frameworks = frameworksOf xml })
  solutions @ projects, projectXml |> List.map (snd >> packageNamesOf)

/// A web project suggests Hot Reload. It only suggests: nothing is chosen for the person.
let hintOf (packagesPerProject: string list list) : WorkflowHint =
  match WorkflowDetection.suggest (WorkflowDetection.extractPackageNames packagesPerProject) with
  | Some suggestion -> WorkflowHint.Suggested suggestion
  | None -> WorkflowHint.NoneSuggested

/// Look in `directory`. Refuses by name when there is nothing to look in; otherwise the candidates, and the
/// live sessions that already work there or in the same checkout.
let discover (live: LiveSession list) (directory: string) : Result<Found * Overlap list, Refusal> =
  match String.IsNullOrWhiteSpace directory with
  | true -> Error Refusal.NoDirectory
  | false ->
    let directory = directory.Trim()
    match Directory.Exists directory with
    | false -> Error (Refusal.DirectoryMissing directory)
    | true ->
      let candidates, packages = candidatesOf directory
      let overlaps = Overlap.decide directory (Boundary.classify directory) live
      Ok ({ Directory = directory; Candidates = candidates; Hint = hintOf packages }, overlaps)

/// The sessions that count as already here: every one that is not stopped, with the checkout it sits in.
let liveSessionsOf (sessions: WorkerProtocol.SessionInfo list) : LiveSession list =
  sessions
  |> List.filter (fun s -> s.Status <> WorkerProtocol.SessionLifecycleStatus.Stopped)
  |> List.map (fun s ->
    { Id = WorkerProtocol.SessionId.value s.Id
      WorkingDirectory = s.WorkingDirectory
      Boundary = Boundary.classify s.WorkingDirectory })

/// The event discovery's answer is, for the state machine.
let eventOf (directory: string) (outcome: Result<Found * Overlap list, Refusal>) : Event =
  match outcome with
  | Ok (found, overlaps) -> Event.Found(found, overlaps)
  | Error refusal -> Event.Missing(directory, refusal)
