namespace SageFs

/// What the statements of one call have produced so far, which each next statement adds to. A caller folds its
/// statements into this in order: every statement runs whether or not an earlier one failed, and each starts once
/// the one before it has finished. `'outcome` is how a
/// statement ended; the caller says how two outcomes combine, because that rule belongs to the caller's outcome type.
type EvalProgress<'outcome> =
  { /// Each statement's output, the newest first.
    Outputs: string list
    Outcome: 'outcome
    Diagnostics: WorkerProtocol.WorkerDiagnostic list
    /// The classified failure of each statement that had one, the newest first: its head is the most recent failure,
    /// kept past a later statement that did not fail.
    Errors: SageFsError list
    Cost: EvalCost }

[<RequireQualifiedAccess>]
module EvalProgress =
  /// Before any statement has run, with the outcome an empty call has.
  let start (nothingRan: 'outcome) : EvalProgress<'outcome> =
    { Outputs = []
      Outcome = nothingRan
      Diagnostics = []
      Errors = []
      Cost = EvalCost.unmeasured }

  /// The progress once one more statement has finished.
  let add
    (combine: 'outcome -> 'outcome -> 'outcome)
    (output: string)
    (outcome: 'outcome)
    (diags: WorkerProtocol.WorkerDiagnostic list)
    (error: SageFsError option)
    (cost: EvalCost)
    (progress: EvalProgress<'outcome>)
    : EvalProgress<'outcome> =
    { Outputs = output :: progress.Outputs
      Outcome = combine progress.Outcome outcome
      Diagnostics = progress.Diagnostics @ diags
      Errors = Option.fold (fun errors latest -> latest :: errors) progress.Errors error
      Cost = EvalCost.sum progress.Cost cost }

  /// The most recent statement failure, when any statement failed.
  let lastError (progress: EvalProgress<'outcome>) : SageFsError option = List.tryHead progress.Errors
