namespace SageFs.Features

open System

/// Slice 3 of the progressive map (docs/progressive-map-research-2026-10-24.md section 9): the references rung.
///
/// A count of references is only worth believing when the reader knows how far the compiler looked. Every reply built
/// here carries its scope as a value, derived in one place from what the compiler was asked and what it answered
/// (`ReferenceReply.ofAnswer`), so a count cannot be written down without the scope that produced it. The pure half
/// lives in this file; the compiler call is `SymbolReferenceCheck`.
[<RequireQualifiedAccess>]
type ReferenceKind =
  | Definition
  | Use

[<RequireQualifiedAccess>]
module ReferenceKind =
  let label (kind: ReferenceKind) : string =
    match kind with
    | ReferenceKind.Definition -> "definition"
    | ReferenceKind.Use -> "use"

/// One place a name is declared or used: where the compiler resolved it, not where the text spells it.
type ReferenceSite =
  { File: string
    Line: int
    Column: int
    Kind: ReferenceKind }

/// Why a project was not checked.
[<RequireQualifiedAccess>]
type ProjectSkip =
  /// The whole-session budget ran out: `spent` is how long the checks had taken when it did.
  | BudgetSpent of budget: TimeSpan * spent: TimeSpan
  /// The compiler raised while checking the project.
  | CompilerFailed of detail: string

type SkippedProject = { Project: string; Reason: ProjectSkip }

/// What the worker found in one project of the session.
[<RequireQualifiedAccess>]
type ProjectOutcome =
  /// The project was checked. `unclean` names the files the compiler reported errors in: a use inside one may be missing.
  | Checked of project: string * sites: ReferenceSite list * unclean: string list
  | Skipped of SkippedProject

/// What the worker answers to `GetReferences`.
[<RequireQualifiedAccess>]
type ReferencesAnswer =
  /// The session loaded no project (a bare session), so there is nothing to check.
  | NoProjectLoaded
  /// One outcome per project of the session, in the order they were tried.
  | Gathered of ProjectOutcome list

/// How far a counted reply looked.
[<RequireQualifiedAccess>]
type CountedScope =
  /// Every project of the session was checked.
  | ProjectWide of projects: string list
  /// Some projects were checked and some were not: the count covers only `checkedProjects`.
  | SomeProjects of checkedProjects: string list * skipped: SkippedProject list

/// Why a reply has no count at all.
[<RequireQualifiedAccess>]
type ReferenceUnavailable =
  | NoProjectLoaded
  /// Every project was skipped.
  | NoProjectChecked of skipped: SkippedProject list
  /// The session's worker could not be asked.
  | SessionUnreachable of why: string

/// The reply as data. A count exists only in `Counted`, which cannot be built without a scope.
[<RequireQualifiedAccess>]
type ReferenceReply =
  | Counted of scope: CountedScope * sites: ReferenceSite list * unclean: string list
  | NotCounted of ReferenceUnavailable

/// What one bounded check of one project returned.
[<RequireQualifiedAccess>]
type ProjectCheck =
  | Answered of sites: ReferenceSite list * unclean: string list
  /// The time given to the check passed before it answered.
  | CutOff
  | Raised of detail: string

[<RequireQualifiedAccess>]
module ProjectSkip =
  let seconds (span: TimeSpan) : string = sprintf "%.0fs" span.TotalSeconds

  let describe (reason: ProjectSkip) : string =
    match reason with
    | ProjectSkip.BudgetSpent(budget, spent) -> sprintf "the %s budget was spent (%s elapsed)" (seconds budget) (seconds spent)
    | ProjectSkip.CompilerFailed detail -> sprintf "the compiler failed: %s" detail

[<RequireQualifiedAccess>]
module ReferenceReply =

  let projectsPhrase (n: int) : string =
    match n with
    | 1 -> "1 project"
    | _ -> sprintf "%d projects" n

  let skippedPhrase (skipped: SkippedProject list) : string =
    skipped
    |> List.groupBy (fun s -> ProjectSkip.describe s.Reason)
    |> List.map (fun (why, projects) -> sprintf "%s (%s)" (projects |> List.map (fun s -> s.Project) |> String.concat ", ") why)
    |> String.concat "; "

  /// The reply's scope, as the sentence that follows `Scope: `. One exhaustive match, so a new scope cannot reach a reader unworded.
  let describeCounted (scope: CountedScope) : string =
    match scope with
    | CountedScope.ProjectWide projects ->
      sprintf "project-wide, all %s of the session checked (%s)" (projectsPhrase projects.Length) (String.concat ", " projects)
    | CountedScope.SomeProjects(checkedProjects, skipped) ->
      sprintf
        "partial, %d of %d projects checked (%s). Not checked: %s"
        checkedProjects.Length
        (checkedProjects.Length + skipped.Length)
        (String.concat ", " checkedProjects)
        (skippedPhrase skipped)

  let describeUnavailable (why: ReferenceUnavailable) : string =
    match why with
    | ReferenceUnavailable.NoProjectLoaded ->
      "unavailable, the session loaded no project, so there is nothing to check. Create a project session first"
    | ReferenceUnavailable.NoProjectChecked skipped ->
      sprintf "unavailable, no project was checked. %s" (skippedPhrase skipped)
    | ReferenceUnavailable.SessionUnreachable why -> sprintf "unavailable, the session could not be asked: %s" why

  /// The scope line of any reply.
  let describe (reply: ReferenceReply) : string =
    match reply with
    | ReferenceReply.Counted(scope, _, _) -> describeCounted scope
    | ReferenceReply.NotCounted why -> describeUnavailable why

  let order (site: ReferenceSite) : string * int * int = site.File, site.Line, site.Column

  /// The reply for what the worker said. The scope is decided here and nowhere else: project-wide only when every
  /// project of the session was checked.
  let ofAnswer (answer: ReferencesAnswer) : ReferenceReply =
    match answer with
    | ReferencesAnswer.NoProjectLoaded -> ReferenceReply.NotCounted ReferenceUnavailable.NoProjectLoaded
    | ReferencesAnswer.Gathered outcomes ->
      let checkedOnes =
        outcomes
        |> List.choose (fun o ->
          match o with
          | ProjectOutcome.Checked(project, sites, unclean) -> Some(project, sites, unclean)
          | ProjectOutcome.Skipped _ -> None)
      let skipped =
        outcomes
        |> List.choose (fun o ->
          match o with
          | ProjectOutcome.Skipped s -> Some s
          | ProjectOutcome.Checked _ -> None)
      let names = checkedOnes |> List.map (fun (p, _, _) -> p)
      let sites = checkedOnes |> List.collect (fun (_, s, _) -> s) |> List.distinct |> List.sortBy order
      let unclean = checkedOnes |> List.collect (fun (_, _, u) -> u) |> List.distinct |> List.sort
      match checkedOnes, skipped with
      | [], [] -> ReferenceReply.NotCounted ReferenceUnavailable.NoProjectLoaded
      | [], _ :: _ -> ReferenceReply.NotCounted(ReferenceUnavailable.NoProjectChecked skipped)
      | _ :: _, [] -> ReferenceReply.Counted(CountedScope.ProjectWide names, sites, unclean)
      | _ :: _, _ :: _ -> ReferenceReply.Counted(CountedScope.SomeProjects(names, skipped), sites, unclean)

/// Checks the projects of a session one after another under ONE budget for all of them. The clock and the checker are
/// parameters, so a simulation can run it on simulated time.
[<RequireQualifiedAccess>]
module ReferenceGather =

  /// `check project remaining` checks one project and gives up when `remaining` passes. A project that starts with
  /// nothing left is skipped without being run, and so is every project after a check that was cut off.
  let run
    (budget: TimeSpan)
    (elapsed: unit -> TimeSpan)
    (check: string -> TimeSpan -> Async<ProjectCheck>)
    (projects: string list)
    : Async<ReferencesAnswer> =
    let skip (project: string) (reason: ProjectSkip) : ProjectOutcome =
      ProjectOutcome.Skipped { Project = project; Reason = reason }
    let rec go (remaining: string list) (acc: ProjectOutcome list) : Async<ProjectOutcome list> =
      async {
        match remaining with
        | [] -> return List.rev acc
        | project :: rest ->
          let left = budget - elapsed ()
          match left > TimeSpan.Zero with
          | false -> return! go rest (skip project (ProjectSkip.BudgetSpent(budget, elapsed ())) :: acc)
          | true ->
            let! verdict = check project left
            let outcome =
              match verdict with
              | ProjectCheck.Answered(sites, unclean) -> ProjectOutcome.Checked(project, sites, unclean)
              | ProjectCheck.CutOff -> skip project (ProjectSkip.BudgetSpent(budget, elapsed ()))
              | ProjectCheck.Raised detail -> skip project (ProjectSkip.CompilerFailed detail)
            return! go rest (outcome :: acc)
      }
    match projects with
    | [] -> async { return ReferencesAnswer.NoProjectLoaded }
    | _ :: _ ->
      async {
        let! outcomes = go projects []
        return ReferencesAnswer.Gathered outcomes
      }
