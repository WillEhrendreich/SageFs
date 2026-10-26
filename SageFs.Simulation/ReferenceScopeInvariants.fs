namespace SageFs.Simulation

open SageFs.Features
open SageFs.Simulation.ReferenceScopeSim

/// Named invariants over a `ReferenceScopeSim.Trace`. Same stable-id / `Holds`-vs-`Violated` shape as the other
/// simulations.
module ReferenceScopeInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// What a counted reply claims about how far it looked.
  [<RequireQualifiedAccess>]
  type Claim =
    | WholeSession
    | SomeProjects

  let violated (t: Trace) (message: string) : Outcome =
    Outcome.Violated(sprintf "%s (seed=%d, budget=%gs, projects=%A)" message t.Scenario.Seed t.Scenario.Budget t.Scenario.Projects)

  /// The reply as (claim, checked names, skipped names, sites), or None when it carries no count.
  let counted (reply: ReferenceReply) : (Claim * string list * string list * ReferenceSite list) option =
    match reply with
    | ReferenceReply.Counted(CountedScope.ProjectWide names, sites, _) -> Some(Claim.WholeSession, names, [], sites)
    | ReferenceReply.Counted(CountedScope.SomeProjects(names, skipped), sites, _) ->
      Some(Claim.SomeProjects, names, skipped |> List.map (fun s -> s.Project), sites)
    | ReferenceReply.NotCounted _ -> None

  /// scope-names-what-was-reached: the reply claims project-wide exactly when the truth reached every project, and a
  /// partial reply names exactly the projects the truth reached and the ones it did not.
  let scopeNamesWhatWasReached : Invariant =
    { Id = "scope-names-what-was-reached"
      Description = "Project-wide only when every project was reached; a partial scope lists the reached and the skipped projects the truth says."
      Check = fun t ->
        let truth = t.Truth
        match counted t.Reply, truth.Reached, truth.NotReached with
        | None, [], _ -> Outcome.Holds
        | None, reached, _ -> violated t (sprintf "the reply carries no count but %A were reached" reached)
        | Some(_, names, _, _), [], _ -> violated t (sprintf "the reply counts %A but the truth reached nothing" names)
        | Some(Claim.WholeSession, names, _, _), reached, [] when names = reached -> Outcome.Holds
        | Some(Claim.WholeSession, names, _, _), _, notReached ->
          violated t (sprintf "the reply claims project-wide over %A but the truth did not reach %A" names notReached)
        | Some(_, names, skipped, _), reached, notReached when names = reached && skipped = notReached && not (List.isEmpty notReached) -> Outcome.Holds
        | Some(claim, names, skipped, _), reached, notReached ->
          violated t (sprintf "the %A reply says checked=%A skipped=%A but the truth is checked=%A skipped=%A" claim names skipped reached notReached) }

  /// a-count-covers-only-what-was-checked: the sites in the reply are exactly the sites of the reached projects.
  let aCountCoversOnlyWhatWasChecked : Invariant =
    { Id = "a-count-covers-only-what-was-checked"
      Description = "The sites a reply lists are the sites of the projects the truth reached, no more and no fewer."
      Check = fun t ->
        match counted t.Reply with
        | None -> Outcome.Holds
        | Some(_, _, _, sites) when sites = t.Truth.Sites -> Outcome.Holds
        | Some(_, _, _, sites) -> violated t (sprintf "the reply lists %d sites and the truth has %d" sites.Length t.Truth.Sites.Length) }

  /// the-whole-question-fits-the-budget: one budget covers every project together, so the simulated time never passes it.
  let theWholeQuestionFitsTheBudget : Invariant =
    { Id = "the-whole-question-fits-the-budget"
      Description = "The simulated clock at the end of a run is at most the budget, whatever the project count."
      Check = fun t ->
        match t.Elapsed <= t.Scenario.Budget + 1e-9 with
        | true -> Outcome.Holds
        | false -> violated t (sprintf "the run took %gs of a %gs budget" t.Elapsed t.Scenario.Budget) }

  /// the-scope-line-leads-and-agrees: the scope line a reader sees says project-wide only for a project-wide reply, and
  /// names every skipped project in a partial one.
  let theScopeLineLeadsAndAgrees : Invariant =
    { Id = "the-scope-line-leads-and-agrees"
      Description = "The scope line says project-wide only when the truth reached every project, and a partial line names the projects the truth skipped."
      Check = fun t ->
        let says (text: string) = t.ScopeLine.Contains text
        let allReached = List.isEmpty t.Truth.NotReached && not (List.isEmpty t.Truth.Reached)
        match says "project-wide", allReached with
        | true, false -> violated t (sprintf "the line says project-wide but the truth skipped %A: %s" t.Truth.NotReached t.ScopeLine)
        | false, true -> violated t (sprintf "every project was reached but the line does not say project-wide: %s" t.ScopeLine)
        | true, true
        | false, false ->
          match t.Truth.Reached, t.Truth.NotReached |> List.filter (fun n -> not (says n)) with
          | _ :: _, missing :: _ -> violated t (sprintf "the partial line does not name %s: %s" missing t.ScopeLine)
          | _ -> Outcome.Holds }

  let all : Invariant list =
    [ scopeNamesWhatWasReached; aCountCoversOnlyWhatWasChecked; theWholeQuestionFitsTheBudget; theScopeLineLeadsAndAgrees ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
