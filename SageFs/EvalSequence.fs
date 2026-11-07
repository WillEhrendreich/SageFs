namespace SageFs

open System
open System.Threading.Tasks

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

  /// What a person is told when a statement of several failed: which one, what it was, what it said, and what did not run.
  let describeStop (position: int) (total: int) (statement: string) (reason: string) : string =
    let skipped = total - position
    let after =
      match skipped with
      | 0 -> ""
      | 1 -> "\nThe 1 statement after it did not run."
      | n -> sprintf "\nThe %d statements after it did not run." n
    sprintf "Statement %d of %d failed: %s\nThe error: %s%s" position total (statement.Trim()) reason after
