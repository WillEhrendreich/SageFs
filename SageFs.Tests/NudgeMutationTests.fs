/// If one decision in the nudge door were wrong, would anything notice? Each
/// mutant breaks exactly one decision, and its case passes only when the mutant's
/// answer fails a predicate the real answer satisfies.
module SageFs.Tests.NudgeMutationTests

open Expecto
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.TweakLog
open SageFs.Features.Tweak.NudgeIo
open SageFs.Features.Tweak.Nudge
open SageFs.Simulation
open SageFs.Simulation.NudgeDisk
open SageFs.Simulation.NudgeWorld
open MutationTestingFramework

let isError result = match result with Error _ -> true | Ok _ -> false
let isOk result = not (isError result)

// ── ownership ──

let ownedWith (disk: Disk) (config: OwnedFiles) (raw: string) = Ownership.tryOwn config disk.Steps.KindOf raw

let linkedDisk () =
  let disk = Disk()
  disk.PutText(sourcePath, "x")
  disk.Link sourcePath
  disk

let plainDisk () =
  let disk = Disk()
  disk.PutText(sourcePath, "x")
  disk.PutText(sourcePath + ".bak", "x")
  disk

let symlinkAccepted : Mutant<string -> Result<OwnedFile, NotOwnedWhy>> =
  { Name = "tryOwn_ignores_symbolic_links"
    Description = "a rename over a link would replace the link, so a link must be refused"
    Apply = fun _ raw -> Ownership.tryOwn owned (fun _ -> FileKind.RegularFile) raw }

let siblingByPrefixAccepted : Mutant<string -> Result<OwnedFile, NotOwnedWhy>> =
  { Name = "tryOwn_accepts_a_path_that_only_starts_like_a_watched_file"
    Description = "ownership is membership of the watched set, not a prefix"
    Apply = fun _ raw -> Ownership.tryOwn { owned with Paths = Set.add (sourcePath + ".bak") owned.Paths } (plainDisk ()).Steps.KindOf raw }

let relativeToTheWrongDirectory : Mutant<string -> Result<OwnedFile, NotOwnedWhy>> =
  { Name = "tryOwn_resolves_relative_paths_against_the_wrong_directory"
    Description = "a relative path is relative to the session's working directory"
    Apply = fun _ raw -> Ownership.tryOwn { owned with WorkingDirectory = "/elsewhere" } (plainDisk ()).Steps.KindOf raw }

let missingFileAccepted : Mutant<string -> Result<OwnedFile, NotOwnedWhy>> =
  { Name = "tryOwn_does_not_check_the_file_is_there"
    Description = "a watched path with nothing at it is not a file to write"
    Apply = fun _ raw -> Ownership.tryOwn owned (fun _ -> FileKind.RegularFile) raw }

let ownershipMutants =
  testList "mutants of Ownership.tryOwn" [
    detectsOutputMutant symlinkAccepted sourcePath (ownedWith (linkedDisk ()) owned) (fun r -> r = Error NotOwnedWhy.IsASymbolicLink)
    detectsOutputMutant siblingByPrefixAccepted (sourcePath + ".bak") (ownedWith (plainDisk ()) owned) isError
    detectsOutputMutant relativeToTheWrongDirectory "src/Tuning.fs" (ownedWith (plainDisk ()) owned) isOk
    detectsOutputMutant missingFileAccepted sourcePath (ownedWith (Disk()) owned) (fun r -> r = Error NotOwnedWhy.NotARegularFile)
  ]

// ── planSet ──

type SetCase =
  { Source: string
    Log: EventLog
    /// The text the caller last saw at the address; its hash is what the write carries.
    Seen: string
    Value: NudgeValue
    Address: TweakAddress }

let plan (c: SetCase) = planSet sourcePath c.Source c.Log c.Address (seenOf c.Seen) c.Value

let staleCase =
  { Source = tuningSource.Replace("9.8", "9.81"); Log = EventLog.empty; Seen = "9.8"; Value = NudgeValue.LiteralText "12.5"; Address = gravity }

let conflictedCase =
  let conflict = { Id = 1; At = 1L; Event = TweakLogEvent.ConflictRaised(gravity, "12.5", "13", "9.8") }
  { Source = tuningSource; Log = { Events = [ conflict ]; NextId = 2 }; Seen = "9.8"; Value = NudgeValue.LiteralText "12.5"; Address = gravity }

let expressionCase =
  { Source = tuningSource; Log = EventLog.empty; Seen = "gravity * 2.0"; Value = NudgeValue.ExpressionText "gravity * 3.0"; Address = jumpVelocity }

let skipsSeenCheck : Mutant<SetCase -> Result<SetPlan, NudgeRefusal>> =
  { Name = "planSet_skips_the_seen_hash_check"
    Description = "a write must never overwrite an expression that changed since it was inspected"
    Apply = fun real c ->
      let actual = (resolve c.Source c.Address |> function Ok r -> r.Text | Error _ -> "")
      real { c with Seen = actual } }

let ignoresOpenConflict : Mutant<SetCase -> Result<SetPlan, NudgeRefusal>> =
  { Name = "planSet_ignores_an_open_conflict"
    Description = "an address with an open conflict takes no further writes"
    Apply = fun real c -> real { c with Log = EventLog.empty } }

let expressionTreatedAsLiteral : Mutant<SetCase -> Result<SetPlan, NudgeRefusal>> =
  { Name = "planSet_reads_an_expression_as_a_literal"
    Description = "an expression replaces a whole formula; read as a literal it can only be refused"
    Apply = fun real c ->
      real { c with Value = (match c.Value with NudgeValue.ExpressionText t -> NudgeValue.LiteralText t | v -> v) } }

let planMutants =
  testList "mutants of planSet" [
    detectsOutputMutant skipsSeenCheck staleCase plan isError
    detectsOutputMutant ignoresOpenConflict conflictedCase plan isError
    detectsOutputMutant expressionTreatedAsLiteral expressionCase plan isOk
  ]

// ── literal kinds ──

let integerReadAsReal : Mutant<LiteralValue * string -> Result<LiteralValue, NudgeRefusal>> =
  { Name = "parseLiteralAs_reads_an_integer_as_a_float"
    Description = "a literal keeps its own kind: 150 for an integer is an integer"
    Apply = fun real (existing, given) ->
      match existing with
      | LiteralValue.Integer _ -> real (LiteralValue.Real 0.0, given)
      | _ -> real (existing, given) }

let lenientBool : Mutant<LiteralValue * string -> Result<LiteralValue, NudgeRefusal>> =
  { Name = "parseLiteralAs_accepts_yes_for_a_bool"
    Description = "only true and false are bools"
    Apply = fun real (existing, given) ->
      match given with
      | "yes" -> Ok(LiteralValue.Bool true)
      | _ -> real (existing, given) }

let lowercaseCase : Mutant<LiteralValue * string -> Result<LiteralValue, NudgeRefusal>> =
  { Name = "parseLiteralAs_accepts_a_lowercase_union_case"
    Description = "a union case starts with an uppercase letter, or it is a value, not a case"
    Apply = fun real (existing, given) ->
      match existing with
      | LiteralValue.Case _ -> Ok(LiteralValue.Case given)
      | _ -> real (existing, given) }

let literalMutants =
  testList "mutants of parseLiteralAs" [
    detectsOutputMutant integerReadAsReal (LiteralValue.Integer 100L, "150") (fun (e, g) -> parseLiteralAs e g) (fun r -> r = Ok(LiteralValue.Integer 150L))
    detectsOutputMutant lenientBool (LiteralValue.Bool false, "yes") (fun (e, g) -> parseLiteralAs e g) isError
    detectsOutputMutant lowercaseCase (LiteralValue.Case "Easy", "lower") (fun (e, g) -> parseLiteralAs e g) isError
  ]

// ── crash settling and the cursor ──

let savedEvent id after = { Id = id; At = int64 id; Event = TweakLogEvent.TweakSaved(gravity, "9.8", after, contentHash after, "h") }
let logOf events = { Events = events; NextId = (events |> List.map (fun e -> e.Id) |> List.fold max 0) + 1 }

let neverSettles : Mutant<string * EventLog -> Reconciliation> =
  { Name = "reconcile_never_settles"
    Description = "a record for a write that never landed has to be found, or undo lies about history"
    Apply = fun _ _ -> Reconciliation.Consistent }

let alwaysSettles : Mutant<string * EventLog -> Reconciliation> =
  { Name = "reconcile_always_marks_the_last_write_undone"
    Description = "a write that did land is history; marking it undone would put the file and the journal at odds"
    Apply = fun _ (_, log) ->
      match lastEffectOf log with
      | LastEffect.Effect(id, _, _, _) -> Reconciliation.WriteNeverLanded(id, TweakLogEvent.RolledBack id)
      | LastEffect.NoEffects -> Reconciliation.Consistent }

let reconcileMutants =
  testList "mutants of reconcile" [
    detectsOutputMutant neverSettles (tuningSource, logOf [ savedEvent 1 "12.5" ]) (fun (s, l) -> reconcile s l) (fun r -> match r with Reconciliation.WriteNeverLanded _ -> true | Reconciliation.Consistent -> false)
    detectsOutputMutant alwaysSettles (tuningSource.Replace("9.8", "12.5"), logOf [ savedEvent 1 "12.5" ]) (fun (s, l) -> reconcile s l) (fun r -> r = Reconciliation.Consistent)
  ]

let rolledBack id target = { Id = id; At = int64 id; Event = TweakLogEvent.RolledBack target }
let undoLog = logOf [ savedEvent 1 "12.5"; rolledBack 2 1 ]
let redoLog = logOf [ savedEvent 1 "12.5"; rolledBack 2 1; rolledBack 3 2 ]
let branchLog = logOf [ savedEvent 1 "12.5"; rolledBack 2 1; savedEvent 3 "7.5" ]

let withoutRollbacks (log: EventLog) : EventLog =
  { log with Events = log.Events |> List.filter (fun e -> match e.Event with TweakLogEvent.RolledBack _ -> false | _ -> true) }

let undosForgotten : Mutant<EventLog -> History> =
  { Name = "historyOf_forgets_undos"
    Description = "after an undo the write is no longer applied, or the next undo repeats the same step"
    Apply = fun real log -> real (withoutRollbacks log) }

let redoReadAsUndo : Mutant<EventLog -> History> =
  { Name = "historyOf_reads_a_redo_as_another_undo"
    Description = "a rollback of a rollback is a redo, and it puts the write back"
    Apply = fun real log ->
      match List.rev log.Events with
      | { Event = TweakLogEvent.RolledBack target } :: older when log.Events |> List.exists (fun e -> e.Id = target && (match e.Event with TweakLogEvent.RolledBack _ -> true | _ -> false)) ->
        real { log with Events = List.rev older }
      | _ -> real log }

let writeKeepsTheRedoStack : Mutant<EventLog -> History> =
  { Name = "historyOf_keeps_the_redo_stack_across_a_new_write"
    Description = "a write after an undo starts a new line of history, so the old undo can no longer be redone"
    Apply = fun real log -> { real log with Undone = (real undoLog).Undone } }

let historyMutants =
  testList "mutants of historyOf" [
    detectsOutputMutant undosForgotten undoLog historyOf (fun h -> h.Applied = [] && h.Undone = [ 2 ])
    detectsOutputMutant redoReadAsUndo redoLog historyOf (fun h -> h.Applied = [ 1 ] && h.Undone = [])
    detectsOutputMutant writeKeepsTheRedoStack branchLog historyOf (fun h -> h.Applied = [ 3 ] && h.Undone = [])
  ]

// ── the journal budget ──

let logOfSize n =
  { Events = [ for i in 1 .. n -> savedEvent i "9.8" ]; NextId = n + 1 }

let atBudgetAccepted : Mutant<EventLog -> Result<unit, NudgeRefusal>> =
  { Name = "journalBudget_allows_one_more_than_the_limit"
    Description = "a journal holding exactly its limit is full"
    Apply = fun real log -> match log.Events.Length = TweakLogLimits.maxEventsPerSession with true -> Ok() | false -> real log }

let belowBudgetRefused : Mutant<EventLog -> Result<unit, NudgeRefusal>> =
  { Name = "journalBudget_refuses_one_early"
    Description = "a journal one short of its limit still has room"
    Apply = fun real log ->
      match log.Events.Length >= TweakLogLimits.maxEventsPerSession - 1 with
      | true -> Error(NudgeRefusal.JournalAtBudget(log.Events.Length, TweakLogLimits.maxEventsPerSession))
      | false -> real log }

let budgetMutants =
  testList "mutants of journalBudget" [
    detectsOutputMutant atBudgetAccepted (logOfSize TweakLogLimits.maxEventsPerSession) journalBudget isError
    detectsOutputMutant belowBudgetRefused (logOfSize (TweakLogLimits.maxEventsPerSession - 1)) journalBudget isOk
  ]

// ── address text ──

let leftRightSwapped : Mutant<PathStep -> string> =
  { Name = "formatStep_swaps_the_two_sides_of_an_operator"
    Description = "the right operand is the right operand"
    Apply = fun real step ->
      match step with
      | PathStep.BinOpLeft -> real PathStep.BinOpRight
      | PathStep.BinOpRight -> real PathStep.BinOpLeft
      | other -> real other }

let addressMutants =
  testList "mutants of NudgeAddress.formatStep" [
    detectsOutputMutant leftRightSwapped PathStep.BinOpRight NudgeAddress.formatStep (fun text -> text = "BinOp.Right")
  ]

// ── atomic write ──

let tamperAndFailRename (tamper: FileSteps -> FileSteps) : string list =
  let disk = Disk()
  disk.PutText(sourcePath, "old")
  disk.Arm [ 2, Fault.FailCleanly ]
  AtomicWrite.write (tamper disk.Steps) sourcePath [| 1uy |] |> ignore
  disk.Paths

let leavesTheTempFile : Mutant<(FileSteps -> FileSteps) -> string list> =
  { Name = "AtomicWrite_leaves_its_temp_file_after_a_failed_rename"
    Description = "a failed write cleans up after itself, so no stray file sits next to the source"
    Apply = fun real _ -> real (fun steps -> { steps with Discard = ignore }) }

let atomicMutants =
  testList "mutants of AtomicWrite.write" [
    detectsOutputMutant leavesTheTempFile id tamperAndFailRename (fun paths -> paths = [ sourcePath ])
  ]

[<Tests>]
let nudgeMutationTests =
  testList "Nudge mutation tests" [ ownershipMutants; planMutants; literalMutants; reconcileMutants; historyMutants; budgetMutants; addressMutants; atomicMutants ]
