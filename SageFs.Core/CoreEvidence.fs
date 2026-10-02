namespace SageFs

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq

/// Does the project a session builds bring its own SageFs.Core?
///
/// The daemon injects ITS SageFs.Core into a build so a user project can use the holder API
/// without writing a HintPath (`SessionBuild.coreReferenceTargetsContent`). That is right for a
/// project that has no SageFs.Core of its own. It is wrong for a project in a repo that BUILDS
/// SageFs.Core (a worktree of SageFs itself): the project, and every project it references, then
/// compiles against the daemon's older Core metadata, and every API the worktree's Core added is
/// "not defined".
///
/// This module reads the evidence, and only the evidence that cannot lie:
///   * the project IS SageFs.Core (its file name or its AssemblyName);
///   * the project, or a project it references, has a ProjectReference to SageFs.Core;
///   * the project wrote its own `<Reference Include="SageFs.Core">`.
/// A `SageFs.Core.dll` sitting in the project's bin is NOT evidence: an earlier injected build
/// copies the daemon's Core there (`Private=true`), so the file would vouch for itself.
///
/// Limits, stated so they are not discovered later: a ProjectReference written with an MSBuild
/// property other than MSBuildThisFileDirectory or MSBuildProjectDirectory is read for its name
/// but not followed, and references added by an imported props file are not seen.
[<RequireQualifiedAccess>]
module CoreEvidence =

  /// What the evidence says.
  [<RequireQualifiedAccess>]
  type OwnCore =
    /// The project being built IS SageFs.Core.
    | IsCore of project: string
    /// `declaredIn` has a ProjectReference to the SageFs.Core project `core`.
    | ProjectReference of declaredIn: string * core: string
    /// `declaredIn` wrote a Reference to SageFs.Core itself.
    | ExplicitReference of declaredIn: string

  /// What was found. A project that cannot be read is its own answer: guessing "none" would
  /// inject the daemon's Core over a project that may bring a newer one.
  [<RequireQualifiedAccess>]
  type Evidence =
    | BringsOwnCore of OwnCore
    /// Nothing brings a SageFs.Core; these are the project files that were read.
    | BringsNone of read: string list
    | Unreadable of project: string * reason: string

  let private coreName = "SageFs.Core"

  let private isCoreName (name: string) : bool =
    String.Equals(name.Trim(), coreName, StringComparison.OrdinalIgnoreCase)

  /// A project file's stem, whichever slash the author used.
  let private stemOf (include': string) : string =
    Path.GetFileNameWithoutExtension(include'.Replace('\\', '/'))

  /// What one project file says, before any of it is followed.
  type private Declared =
    { /// `<AssemblyName>` values.
      AssemblyNames: string list
      /// `<ProjectReference Include>` values as written.
      ProjectReferences: string list
      /// `<Reference Include>` values as written.
      References: string list }

  let private attributeValues (document: XDocument) (element: string) (attribute: string) : string list =
    document.Descendants()
    |> Seq.filter (fun e -> e.Name.LocalName = element)
    |> Seq.choose (fun e ->
      match e.Attribute(XName.Get attribute) with
      | null -> None
      | a -> Some a.Value)
    |> List.ofSeq

  let private declaredIn (document: XDocument) : Declared =
    { AssemblyNames =
        document.Descendants()
        |> Seq.filter (fun e -> e.Name.LocalName = "AssemblyName")
        |> Seq.map (fun e -> e.Value.Trim())
        |> List.ofSeq
      ProjectReferences = attributeValues document "ProjectReference" "Include"
      References = attributeValues document "Reference" "Include" }

  /// `<Reference Include="SageFs.Core, Version=...">` names the assembly before the first comma.
  let private referenceNames (declared: Declared) : string list =
    declared.References |> List.map (fun r -> (r.Split(',')[0]).Trim())

  let private projectDirectoryOf (path: string) : string =
    match Path.GetDirectoryName path with
    | null -> ""
    | dir -> dir

  /// An Include, made into a path next to the project that wrote it, or the property it could not expand.
  [<RequireQualifiedAccess>]
  type private Resolved =
    | Path of string
    | NotExpanded of include': string

  let private resolveInclude (declaredIn: string) (include': string) : Resolved =
    let dir = projectDirectoryOf declaredIn
    let withDir = dir.TrimEnd('/') + "/"
    let expanded =
      include'
        .Replace("$(MSBuildThisFileDirectory)", withDir)
        .Replace("$(MSBuildProjectDirectory)", withDir)
        .Replace('\\', '/')
    match expanded.Contains "$(" with
    | true -> Resolved.NotExpanded include'
    | false ->
      match Path.IsPathRooted expanded with
      | true -> Resolved.Path (Path.GetFullPath expanded)
      | false -> Resolved.Path (Path.GetFullPath(Path.Combine(dir, expanded)))

  /// What a solution file listed.
  [<RequireQualifiedAccess>]
  type private Members =
    | Listed of projects: string list
    | CouldNotRead of reason: string

  /// The projects a solution lists, so a solution build is judged by what it builds.
  let private solutionMembers (solution: string) : Members =
    try
      let dir = projectDirectoryOf solution
      let text = File.ReadAllText solution
      let raw =
        match Path.GetExtension(solution).ToLowerInvariant() with
        | ".slnx" ->
          (XDocument.Parse text |> fun d -> attributeValues d "Project" "Path")
        | _ ->
          [ for m in Regex.Matches(text, "\"([^\"]+\\.(?:fs|cs|vb)proj)\"", RegexOptions.IgnoreCase) -> m.Groups[1].Value ]
      raw
      |> List.map (fun p -> Path.GetFullPath(Path.Combine(dir, p.Replace('\\', '/'))))
      |> Members.Listed
    with ex -> Members.CouldNotRead ex.Message

  let private isSolution (path: string) : bool =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".sln" | ".slnx" -> true
    | _ -> false

  /// What one project says and everything it references, read depth first. A project that was
  /// already read says nothing new, which also ends a reference cycle.
  let private inspectProject (start: string) : Evidence =
    let visited = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)

    let rec inspect (path: string) : Evidence =
      match visited.Add path with
      | false -> Evidence.BringsNone []
      | true ->
        match File.Exists path with
        | false -> Evidence.Unreadable(path, "the project file does not exist")
        | true ->
          match (try Result.Ok (XDocument.Load path) with ex -> Result.Error ex.Message) with
          | Result.Error reason -> Evidence.Unreadable(path, reason)
          | Result.Ok document ->
            let declared = declaredIn document
            match isCoreName (Path.GetFileNameWithoutExtension path) || (declared.AssemblyNames |> List.exists isCoreName) with
            | true -> Evidence.BringsOwnCore (OwnCore.IsCore path)
            | false ->
              match referenceNames declared |> List.exists isCoreName with
              | true -> Evidence.BringsOwnCore (OwnCore.ExplicitReference path)
              | false ->
                let followed =
                  declared.ProjectReferences
                  |> List.map (fun include' ->
                    match isCoreName (stemOf include') with
                    | true ->
                      let core =
                        match resolveInclude path include' with
                        | Resolved.Path p -> p
                        | Resolved.NotExpanded written -> written
                      Evidence.BringsOwnCore (OwnCore.ProjectReference(path, core))
                    | false ->
                      match resolveInclude path include' with
                      | Resolved.Path next -> inspect next
                      | Resolved.NotExpanded _ -> Evidence.BringsNone [])
                combine path followed

    and combine (path: string) (parts: Evidence list) : Evidence =
      let found = parts |> List.tryPick (function Evidence.BringsOwnCore own -> Some own | _ -> None)
      match found with
      | Some own -> Evidence.BringsOwnCore own
      | None ->
        match parts |> List.tryPick (function Evidence.Unreadable(p, r) -> Some (p, r) | _ -> None) with
        | Some (p, r) -> Evidence.Unreadable(p, r)
        | None ->
          let read = parts |> List.collect (function Evidence.BringsNone r -> r | _ -> [])
          Evidence.BringsNone (path :: read)

    inspect start

  /// The evidence for what a session builds: one project, or every project of a solution.
  let evidenceFor (buildTarget: string) : Evidence =
    match isSolution buildTarget with
    | false -> inspectProject buildTarget
    | true ->
      match solutionMembers buildTarget with
      | Members.CouldNotRead reason -> Evidence.Unreadable(buildTarget, reason)
      | Members.Listed members ->
        let parts = members |> List.map inspectProject
        match parts |> List.tryPick (function Evidence.BringsOwnCore own -> Some own | _ -> None) with
        | Some own -> Evidence.BringsOwnCore own
        | None ->
          match parts |> List.tryPick (function Evidence.Unreadable(p, r) -> Some (p, r) | _ -> None) with
          | Some (p, r) -> Evidence.Unreadable(p, r)
          | None -> Evidence.BringsNone (parts |> List.collect (function Evidence.BringsNone r -> r | _ -> []))

  /// Every project file that `start` references, directly or through other projects, that exists on
  /// disk. `start` itself is not in the list. A project that cannot be read ends that branch and
  /// nothing more: this is for explaining a failed build, not for deciding one.
  let referencedProjects (start: string) : string list =
    let visited = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
    visited.Add start |> ignore
    let found = System.Collections.Generic.List<string>()
    let rec walk (path: string) =
      match (try Result.Ok (XDocument.Load path) with ex -> Result.Error ex.Message) with
      | Result.Error _ -> ()
      | Result.Ok document ->
        for include' in (declaredIn document).ProjectReferences do
          match resolveInclude path include' with
          | Resolved.NotExpanded _ -> ()
          | Resolved.Path next ->
            match File.Exists next && visited.Add next with
            | true ->
              found.Add next
              walk next
            | false -> ()
    walk start
    List.ofSeq found

  /// The evidence in words, for a log line or a refusal.
  let describeOwnCore (own: OwnCore) : string =
    match own with
    | OwnCore.IsCore project -> sprintf "'%s' is SageFs.Core itself" project
    | OwnCore.ProjectReference(declaredIn, core) -> sprintf "'%s' has a ProjectReference to the SageFs.Core project '%s'" declaredIn core
    | OwnCore.ExplicitReference declaredIn -> sprintf "'%s' references SageFs.Core itself" declaredIn
