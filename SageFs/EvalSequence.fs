namespace SageFs

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

/// One submission at a time for each session. A submission of several statements is one thing a person did: another
/// submission (a Try, a Revert, a second paste) that ran between its statements would run against half of it. A gate
/// is a semaphore per session key, made on first use, so a session never waits for another session's submission.
type SubmissionGates() =
  let gates = ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal)

  /// Run `work` when no other submission to `sessionKey` is running, and let the next one in after it.
  member _.Run(sessionKey: string, work: unit -> Task<'T>) : Task<'T> =
    task {
      let gate = gates.GetOrAdd(sessionKey, fun _ -> new SemaphoreSlim(1, 1))
      do! gate.WaitAsync()
      try
        return! work ()
      finally
        gate.Release() |> ignore
    }

/// A submission of several statements, evaluated one at a time. The worker runs the first `;;` interaction of the text it is given and
/// answers Ok, so a submission handed over whole drops every later statement without a word. The dashboard's shared eval (the
/// Evaluate box, eval-file, and the live bindings' Try, Reset and Revert) splits the text at `;;` (`McpAdapter.splitStatements`, the
/// same split the MCP tools use), runs the statements in order, and when one fails says which statement it was and what it was.
module EvalSequence =

  [<RequireQualifiedAccess>]
  type SequenceOutcome =
    /// Every statement ran, and these are the answers, in order.
    | Ran of messages: string list
    /// The statement at `position` (of `total`) failed with `reason`, so the ones after it did not run.
    | StoppedAt of position: int * total: int * statement: string * reason: string * ranBefore: string list

  /// A statement ends with `;;`, which is what makes it one interaction.
  let withTerminator (statement: string) : string =
    let text = statement.TrimEnd()
    match text.EndsWith(";;", StringComparison.Ordinal) with
    | true -> text
    | false -> text + ";;"

  /// Evaluate the statements in order, each as its own interaction, and stop at the first one that fails.
  let evalInOrder (evalOne: string -> Task<Result<string, string>>) (statements: string list) : Task<SequenceOutcome> =
    task {
      let total = statements.Length
      let mutable ran = []
      let mutable stopped = None
      let mutable position = 0
      for statement in statements do
        position <- position + 1
        match stopped with
        | Some _ -> ()
        | None ->
          let! answer = evalOne (withTerminator statement)
          match answer with
          | Ok message -> ran <- ran @ [ message ]
          | Error reason -> stopped <- Some(position, statement, reason)
      return
        match stopped with
        | None -> SequenceOutcome.Ran ran
        | Some(at, statement, reason) -> SequenceOutcome.StoppedAt(at, total, statement, reason, ran)
    }

  /// Whether any statement of the outcome ran, which is whether anything a reader of the session's values could see has changed.
  let ranAnything (outcome: SequenceOutcome) : bool =
    match outcome with
    | SequenceOutcome.Ran messages -> not (List.isEmpty messages)
    | SequenceOutcome.StoppedAt(_, _, _, _, ranBefore) -> not (List.isEmpty ranBefore)

  /// A submission of several statements for one session: the statements run in order while no other submission to that session
  /// runs, and `pullBindings` is called once, after the last statement, when any of them ran.
  let submit
    (gates: SubmissionGates)
    (sessionKey: string)
    (pullBindings: unit -> unit)
    (evalOne: string -> Task<Result<string, string>>)
    (statements: string list)
    : Task<SequenceOutcome> =
    gates.Run(sessionKey, fun () ->
      task {
        let! outcome = evalInOrder evalOne statements
        match ranAnything outcome with
        | true -> pullBindings ()
        | false -> ()
        return outcome
      })

  /// Where a submission stopped: the 1-based position of the failed statement, and how many statements the submission had.
  type StopPoint = { Position: int; Total: int }

  /// The statement that failed, as the person wrote it, and what the worker said about it.
  type StatementFailure = { Statement: string; Reason: string }

  /// What a person is told when a statement of several failed: which one, what it was, what it said, and what did not run.
  let describeStop (stop: StopPoint) (failure: StatementFailure) : string =
    let position = stop.Position
    let total = stop.Total
    let statement = failure.Statement
    let reason = failure.Reason
    let skipped = total - position
    let after =
      match skipped with
      | 0 -> ""
      | 1 -> "\nThe 1 statement after it did not run."
      | n -> sprintf "\nThe %d statements after it did not run." n
    sprintf "Statement %d of %d failed: %s\nThe error: %s%s" position total (statement.Trim()) reason after
