namespace SageFs

open SageFs.EdgeCredential

/// How `operator.token` comes to exist, as a pure state machine. The daemon needs one operator credential
/// per data directory, kept in a file only its owner can read, and two daemons that start on one directory
/// must end holding the same credential. The shell around this (SageFs/OperatorStore.fs) does the file
/// work; this module decides every step, so a simulation can interleave two daemons, crash one between
/// steps and still fold the real decisions.
///
/// The protocol, which is what Jupyter's runtime files learned the hard way (jupyter_server#77):
///   1. read the target. A good file is the credential. A file that is not the shape of one is refused as
///      corrupt, and one that others can read is refused, and neither is ever used or overwritten.
///   2. no file: write a credential to a temporary file beside it, with owner-only mode.
///   3. read the temporary file's mode back. A filesystem that cannot hold owner-only is refused, with
///      the fix in the message.
///   4. publish by moving the temporary file to the target WITHOUT replacing. The loser of a race finds
///      the target taken, discards its own credential and reads the winner's.
/// The target therefore appears whole or not at all, and there is no step at which it holds half a credential.
module OperatorFile =

  /// What a file's mode says about who can read it.
  [<RequireQualifiedAccess>]
  type FileMode =
    | OwnerOnly
    /// Anyone else can read or write it. `octal` is the mode as the shell would show it.
    | TooWide of octal: string

  [<RequireQualifiedAccess>]
  type Observation =
    | Missing
    | Present of text: string * mode: FileMode

  [<RequireQualifiedAccess>]
  type Refusal =
    /// The file is there and is not the shape of an operator credential. It is left as it is.
    | Corrupt
    /// The file is there and others can read it. It is left as it is.
    | ModeTooWide of octal: string
    /// The filesystem did not keep owner-only on the file just written.
    | FilesystemCannotHoldOwnerOnly of octal: string
    /// A step failed.
    | Io of reason: string
    /// The target kept appearing and disappearing.
    | GaveUp

  /// What the machine asks the world to do next.
  [<RequireQualifiedAccess>]
  type Effect =
    | ReadTarget
    /// Create the temporary file with owner-only mode and write the credential into it.
    | WriteTemp of credential: string
    | ReadTempMode
    /// Move the temporary file to the target, failing if the target exists.
    | PublishExclusive
    | DiscardTemp

  /// What the world answered.
  [<RequireQualifiedAccess>]
  type Answer =
    | Observed of Observation
    | TempWritten
    | TempModeIs of FileMode
    | Published
    | TargetAlreadyExists
    | Discarded
    | Failed of reason: string

  [<RequireQualifiedAccess>]
  type State =
    | Reading of attempt: int
    | Writing of credential: string * attempt: int
    | CheckingMode of credential: string * attempt: int
    | Publishing of credential: string * attempt: int
    | LostRace of attempt: int
    | Cleaning of Refusal
    | Finished of Result<OperatorHash, Refusal>

  /// How many times the target may be found taken and then gone again before the machine gives up.
  let maxAttempts : int = 3

  let start : State * Effect option = State.Reading 1, Some Effect.ReadTarget

  let finish (result: Result<OperatorHash, Refusal>) : State * Effect option = State.Finished result, None

  let cleanUp (refusal: Refusal) : State * Effect option = State.Cleaning refusal, Some Effect.DiscardTemp

  let ioRefusal (what: string) (answer: Answer) : Refusal =
    match answer with
    | Answer.Failed reason -> Refusal.Io(sprintf "%s: %s" what reason)
    | Answer.Observed _
    | Answer.TempWritten
    | Answer.TempModeIs _
    | Answer.Published
    | Answer.TargetAlreadyExists
    | Answer.Discarded -> Refusal.Io(sprintf "%s: the world answered with something the step did not ask for" what)

  /// The next state and effect. `entropy` is drawn by the shell from a cryptographic source and is read only
  /// when a credential is written. Total: an answer the state did not ask for finishes with a named refusal.
  let next (entropy: byte[]) (state: State) (answer: Answer) : State * Effect option =
    match state, answer with
    | State.Reading attempt, Answer.Observed Observation.Missing ->
      let credential = OperatorCredential.ofEntropy entropy
      State.Writing(credential, attempt), Some(Effect.WriteTemp credential)
    | State.Reading _, Answer.Observed(Observation.Present(text, mode)) ->
      let credential = text.Trim()
      match OperatorCredential.isWellFormed credential, mode with
      | false, _ -> finish (Error Refusal.Corrupt)
      | true, FileMode.TooWide octal -> finish (Error(Refusal.ModeTooWide octal))
      | true, FileMode.OwnerOnly -> finish (Ok(OperatorHash.ofCredential credential))
    | State.Reading _, other -> finish (Error(ioRefusal "reading operator.token" other))
    | State.Writing(credential, attempt), Answer.TempWritten -> State.CheckingMode(credential, attempt), Some Effect.ReadTempMode
    | State.Writing _, other -> cleanUp (ioRefusal "writing the temporary credential file" other)
    | State.CheckingMode(credential, attempt), Answer.TempModeIs FileMode.OwnerOnly -> State.Publishing(credential, attempt), Some Effect.PublishExclusive
    | State.CheckingMode _, Answer.TempModeIs(FileMode.TooWide octal) -> cleanUp (Refusal.FilesystemCannotHoldOwnerOnly octal)
    | State.CheckingMode _, other -> cleanUp (ioRefusal "reading the temporary file's mode" other)
    | State.Publishing(credential, _), Answer.Published -> finish (Ok(OperatorHash.ofCredential credential))
    | State.Publishing(_, attempt), Answer.TargetAlreadyExists -> State.LostRace attempt, Some Effect.DiscardTemp
    | State.Publishing _, other -> cleanUp (ioRefusal "publishing operator.token" other)
    | State.LostRace attempt, (Answer.Discarded | Answer.Failed _) ->
      // A temporary file that will not go away is only litter. The credential that matters is the winner's.
      match attempt < maxAttempts with
      | true -> State.Reading(attempt + 1), Some Effect.ReadTarget
      | false -> finish (Error Refusal.GaveUp)
    | State.LostRace _, _ -> finish (Error(Refusal.Io "discarding the losing credential: the world answered with something the step did not ask for"))
    | State.Cleaning refusal, _ -> finish (Error refusal)
    | State.Finished result, _ -> State.Finished result, None

  /// The sentence that says what happened and how to fix it.
  let describe (refusal: Refusal) : string =
    match refusal with
    | Refusal.Corrupt ->
      "operator.token exists and is not an operator credential. Delete it and restart the daemon to make a new one."
    | Refusal.ModeTooWide octal ->
      sprintf "operator.token is readable by others (mode %s). Run 'chmod 600' on it, or delete it and restart the daemon to make a new one." octal
    | Refusal.FilesystemCannotHoldOwnerOnly octal ->
      sprintf "The filesystem holding the data directory kept mode %s on operator.token instead of owner-only. Point SAGEFS_DATA_DIR at a filesystem that keeps Unix permissions (a mounted share often does not)." octal
    | Refusal.Io reason -> sprintf "operator.token could not be made: %s" reason
    | Refusal.GaveUp -> "operator.token kept appearing and disappearing while the daemon tried to read it. Is another process deleting it?"
