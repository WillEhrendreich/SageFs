/// Trying a value in the running session without writing the file: the DECISION, pure. Given the row, how its value is reached
/// (`PushVia`), the literal the file holds and what the person typed, answer with the statement to evaluate in the session and the
/// text the live-values walk will print for the new value (which is what makes the row `tweaked`), or with the reason the typed text
/// is not a value this literal can take. Nothing here evaluates anything: the daemon sends the statement to the session, as the
/// Evaluate box does, and records the printed text against the row.
///
/// The value is held to the literal's own kind and type exactly as a write to the file is (`Nudge.parseLiteralAs`,
/// `LiteralEdit.formatLiteral`), so a value that could not be saved cannot be tried either.
module SageFs.Features.Tweak.PushCode

open System
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak

/// What a push does to the row's relation with its file.
[<RequireQualifiedAccess>]
type PushEffect =
  /// The session will hold a value the file does not: the row reads tweaked, and the push is remembered.
  | StartsTweak
  /// The session will hold the value the file holds, so the row has nothing left to say about a tweak and the push is forgotten.
  | MatchesFile

/// A statement to evaluate, and the text the walk prints once the session holds the value.
type PushPlan =
  { Code: string
    Printed: string
    Effect: PushEffect }

/// `{ accessor with Label = value }`, nested for a path of labels: `{ feel with A = { feel.A with B = value } }`.
let rec updateIn (accessor: string) (labels: string list) (value: string) : string =
  match labels with
  | [] -> value
  | [ label ] -> sprintf "{ %s with %s = %s }" accessor label value
  | label :: rest -> sprintf "{ %s with %s = %s }" accessor label (updateIn (accessor + "." + label) rest value)

/// The statement that puts `literal` (already spelled the way the file spells it) into the row, the way `via` says.
let statement (via: PushVia) (key: RowKey) (literal: string) : string =
  let path = String.concat "." (key.Binding :: key.Labels)
  match via with
  | PushVia.AssignBinding -> sprintf "%s <- %s;;" key.Binding literal
  | PushVia.AssignField -> sprintf "%s <- %s;;" path literal
  | PushVia.RebindBinding -> sprintf "let %s = %s;;" key.Binding literal
  | PushVia.RebindWithCopy -> sprintf "let %s = %s;;" key.Binding (updateIn key.Binding key.Labels literal)
  | PushVia.AssignBindingWithCopy -> sprintf "%s <- %s;;" key.Binding (updateIn key.Binding key.Labels literal)

/// Several statements as ONE interaction: each loses its own `;;` and the last carries the only one. The batch is then one evaluation
/// in the session, one line of output and one read of the session's values afterwards, and it changes every row or none. (The
/// dashboard's shared eval also runs separate `;;` statements one at a time, so this is a choice, not a workaround.)
let oneInteraction (statements: string list) : string =
  let bare (statement: string) =
    let text = statement.TrimEnd()
    match text.EndsWith(";;", StringComparison.Ordinal) with
    | true -> text.Substring(0, text.Length - 2).TrimEnd()
    | false -> text
  (statements |> List.map bare |> String.concat "\n") + ";;"

/// What the person typed, as a value of the file's literal. A refusal is the door's own (`NudgeRefusal`), the one a write of the same
/// text would have got, so the row says the same thing whether the value was tried or saved.
let plan (via: PushVia) (key: RowKey) (fileText: string) (typed: string) : Result<PushPlan, NudgeRefusal> =
  match readLiteralText fileText with
  | Error _ -> Error(NudgeRefusal.NotALiteral fileText)
  | Ok(existing, style) ->
    match parseLiteralAs existing typed with
    | Error refusal -> Error refusal
    | Ok value ->
      match formatLiteral style value with
      | Error(SetLiteralError.KindMismatch reason) -> Error(NudgeRefusal.ValueKindMismatch reason)
      | Error(SetLiteralError.OutOfRange reason) -> Error(NudgeRefusal.ValueOutOfRange reason)
      | Error(SetLiteralError.NotALiteral shown) -> Error(NudgeRefusal.NotALiteral shown)
      | Error(SetLiteralError.Gone _) -> Error(NudgeRefusal.NotALiteral fileText)
      | Ok literal ->
        Ok { Code = statement via key literal
             Printed = LiveShape.printed value
             Effect =
               match LiveShape.printed existing = LiveShape.printed value with
               | true -> PushEffect.MatchesFile
               | false -> PushEffect.StartsTweak }

/// Put the file's own literal back into the session: a revert of a tweak, or a reset of a value the program moved. The literal is the
/// file's text, so the session holds exactly what the file says.
let revert (via: PushVia) (key: RowKey) (fileText: string) : Result<PushPlan, NudgeRefusal> =
  match readLiteralText fileText with
  | Error _ -> Error(NudgeRefusal.NotALiteral fileText)
  | Ok(value, _) -> Ok { Code = statement via key (fileText.Trim()); Printed = LiveShape.printed value; Effect = PushEffect.MatchesFile }

/// The text a write to the file takes for a value the walk printed: a string and a character lose their quotes, because the door reads
/// the value, not its spelling. Every other kind is the same text.
let doorText (fileText: string) (printed: string) : string =
  match readLiteralText fileText with
  | Ok(LiteralValue.Text _, _) when printed.Length >= 2 && printed.StartsWith "\"" && printed.EndsWith "\"" -> printed.Substring(1, printed.Length - 2)
  | Ok(LiteralValue.Char _, _) when printed.Length >= 3 && printed.StartsWith "'" && printed.EndsWith "'" -> printed.Substring(1, printed.Length - 2)
  | Ok _
  | Error _ -> printed
