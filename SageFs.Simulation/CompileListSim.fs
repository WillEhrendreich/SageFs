namespace SageFs.Simulation

open System
open SageFs.Features
open SageFs.Features.ProjectFileChange

/// Deterministic simulation of the classifier that decides which files a compile-list edit can affect
/// (`SageFs.Core/Features/ProjectFileChange.fs`, `affectedFiles`), the fix for a landing that only adds a source file running the
/// whole assembly.
///
/// The world is a project: an ordered list of files, each with a content, the names it declares (a module reached by its own name),
/// the names its text mentions, and whether it is opaque (an AutoOpen module, an extension, a namespace that declares types: it can
/// change what a later file means without being named). The ground truth is F#'s own rule, written out independently of the code:
/// a file's meaning is its content and the meanings of the files compiled before it that it can see, in order. A compile-list edit
/// keeps every file's content, so the files whose meaning differs between the two lists are exactly the files the edit affected.
///
/// THE SUBJECT IS THE REAL CODE: `affectedFiles` runs over each seeded edit (an append, an insertion, a removal, a move, a new file
/// that shadows a name an existing file uses). SAFETY says it selects every file whose meaning changed. The twins are real mutants:
/// one that selects only the added files, one that drops the transitive step, one off by one at the first difference.
module CompileListSim =

  type SimFile =
    { Name: string
      Content: int
      Declares: Set<string>
      Opaque: bool
      Mentions: Set<string> }

  [<RequireQualifiedAccess>]
  type Edit =
    /// New files, each placed at its own position in the list.
    | Insert of (int * SimFile) list
    | Remove of string list
    /// Move the file at `from` to `toIndex` (indexes into the list before the move).
    | Move of fromIndex: int * toIndex: int

  type Scenario =
    { Seed: int
      Before: SimFile list
      Edit: Edit }

  let factsOfFile (file: SimFile) : FileFacts =
    { Reach = (match file.Opaque with true -> Reach.Anything | false -> Reach.ByName file.Declares)
      Mentions = Mentioned.Identifiers file.Mentions }

  /// The list after the edit.
  let applyEdit (scenario: Scenario) : SimFile list =
    match scenario.Edit with
    | Edit.Insert placements ->
      placements
      |> List.sortBy fst
      |> List.fold (fun (list: SimFile list) (index, file) ->
        let at = min index list.Length
        List.take at list @ [ file ] @ List.skip at list) scenario.Before
    | Edit.Remove names -> scenario.Before |> List.filter (fun file -> not (List.contains file.Name names))
    | Edit.Move (fromIndex, toIndex) ->
      let moved = scenario.Before[fromIndex]
      let without = scenario.Before |> List.filter (fun file -> file.Name <> moved.Name)
      let at = min toIndex without.Length
      List.take at without @ [ moved ] @ List.skip at without

  // ── The ground truth: F#'s rule, independent of the classifier ──

  let sees (file: SimFile) (other: SimFile) : bool =
    other.Opaque || (other.Declares |> Set.exists (fun name -> Set.contains name file.Mentions))

  /// What each file of the list means: its content and the meanings of the files before it that it can see, in order.
  let meanings (list: SimFile list) : Map<string, int> =
    list
    |> List.fold (fun (known: Map<string, int>, before: SimFile list) file ->
      let visible = before |> List.filter (sees file) |> List.map (fun other -> known[other.Name])
      let meaning = hash (file.Content, visible)
      known.Add (file.Name, meaning), before @ [ file ]) (Map.empty, [])
    |> fst

  /// The files of the new list the edit affected: new ones, and ones whose meaning is not what it was.
  let trulyAffected (scenario: Scenario) : string list =
    let after = applyEdit scenario
    let was = meanings scenario.Before
    let now = meanings after
    after
    |> List.filter (fun file ->
      match Map.tryFind file.Name was with
      | None -> true
      | Some earlier -> earlier <> now[file.Name])
    |> List.map _.Name

  // ── The classifier under test, and the twins ──

  type Selector = (string -> FileFacts) -> string list -> string list -> string list

  let real : Selector = affectedFiles

  /// TWIN: only the files the edit added. Right for an append nobody mentions, wrong when a file after the edit can see it.
  let addedOnlyTwin : Selector =
    fun _ before after ->
      let known = Set.ofList before
      after |> List.filter (fun file -> not (Set.contains file known))

  /// TWIN: the real rule without the transitive step: a file is selected only when what IT can see changed.
  let noTransitiveTwin : Selector =
    fun factsOf before after ->
      let beforeSet = Set.ofList before
      let visible (list: string list) (file: string) =
        let facts = factsOf file
        list |> List.takeWhile (fun other -> other <> file) |> List.filter (fun other -> canSee facts (factsOf other))
      after |> List.filter (fun file -> not (Set.contains file beforeSet) || visible before file <> visible after file)

  /// TWIN: the order-only rule one file short: it skips the first file at the first difference.
  let offByOneTwin : Selector =
    fun _ before after -> after |> List.skip (min after.Length (commonPrefixLength before after + 1))

  /// TWIN: no refinement at all, the order rule alone. Safe, and not narrow.
  let orderOnlyTwin : Selector = fun _ before after -> reshapedFiles before after

  type Trace =
    { Scenario: Scenario
      After: string list
      Selected: string list
      Truth: string list
      Reducer: string }

  let run (name: string) (selector: Selector) (scenario: Scenario) : Trace =
    let after = applyEdit scenario
    let byName = (scenario.Before @ after) |> List.map (fun file -> file.Name, file) |> Map.ofList
    let factsOf (file: string) = factsOfFile byName[file]
    { Scenario = scenario
      After = after |> List.map _.Name
      Selected = selector factsOf (scenario.Before |> List.map _.Name) (after |> List.map _.Name)
      Truth = trulyAffected scenario
      Reducer = name }

  // ── Invariants ──

  /// SAFETY: every file whose meaning the edit changed is selected, so a test covering it is run.
  let safe (trace: Trace) : bool =
    trace.Truth |> List.forall (fun file -> List.contains file trace.Selected)

  /// WITHIN THE ORDER BOUND: the classifier never selects a file the compile order alone says cannot be affected.
  let withinOrderBound (trace: Trace) : bool =
    let before = trace.Scenario.Before |> List.map _.Name
    let bound = Set.ofList (reshapedFiles before trace.After)
    let added = trace.After |> List.filter (fun file -> not (List.contains file before)) |> Set.ofList
    trace.Selected |> List.forall (fun file -> Set.contains file bound || Set.contains file added)

  /// NARROW: an insertion of plain files that no later file names selects exactly those files, wherever it goes in the list.
  let narrowForUnnamedInsertion (trace: Trace) : bool =
    match trace.Scenario.Edit with
    | Edit.Insert placements ->
      let inserted = placements |> List.map (snd >> _.Name) |> Set.ofList
      let plain = placements |> List.forall (fun (_, file) -> not file.Opaque)
      let named =
        trace.Scenario.Before
        |> List.exists (fun existing -> placements |> List.exists (fun (_, file) -> sees existing file))
      match plain && not named with
      | false -> true
      | true -> Set.ofList trace.Selected = inserted
    | _ -> true
