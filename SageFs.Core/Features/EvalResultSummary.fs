/// Bounds how much of an eval's printed result comes back through the eval
/// reply. Printing something like a `ProjectOptions` at the FSI prompt with
/// no display truncation configured produced a single ~90,000-character
/// result string — unusable as an agent's next thing to read, and a real
/// cost to ship over the wire on every such eval. This is the one place
/// that bound is enforced; `AppState.evalFn` calls `bound` on the printed
/// result before it becomes the eval's `EvaluationResult`.
///
/// The drill-in this gives the caller is a REAL next step, not a suggestion
/// to "try something smaller": F# Interactive auto-binds the last evaluated
/// expression's value to `it`, and this module never replaces the value —
/// only the TEXT of what got printed — so `it` still holds the whole thing
/// in the session. The notice tells the caller to slice or project `it`
/// directly, which is something `send_fsharp_code` can execute immediately.
module SageFs.Features.EvalResultSummary

/// How large a printed eval result can get before it's summarised instead
/// of returned whole. Comfortably above what a normal binding print looks
/// like, far below the ~90,000-char wall that motivated this module.
let maxResultChars = 4000

/// What the session keeps of output written between two calls (a background thread's `printfn`, an app's log lines), so
/// the next reply can carry it. The last part is kept: it is the part nearest the call that finds it.
let maxBetweenCallChars = 2000

/// The label the next reply puts above output that was written between calls.
[<Literal>]
let BetweenCallsLabel = "[output written since your last call]"

/// The reply text with the output written between calls after it, under its label. It goes last so the first line of the
/// reply stays the eval's own result, which is what an editor shows beside the code. Nothing written, nothing added.
let withBetweenCalls (between: string) (output: string) : string =
  match System.String.IsNullOrWhiteSpace between with
  | true -> output
  | false -> sprintf "%s\n%s\n%s" (output.TrimEnd()) BetweenCallsLabel (between.TrimEnd())

/// Of the characters a clipped result keeps, this many parts in `ShareParts` come from its start and the rest from its
/// end. Two thirds to the head is FsiMcp 0.3.0's split. The end is where a final value or an error summary sits, so a
/// clipped result never drops it.
[<Literal>]
let HeadParts = 2

[<Literal>]
let ShareParts = 3

/// How many of `maxChars` kept characters come from the start.
let headChars (maxChars: int) : int = int (int64 (max 0 maxChars) * int64 HeadParts / int64 ShareParts)

/// What came back for a result we didn't return in full.
type BoundedResult =
  { /// What to actually send back — either the untouched `raw` text, or the kept start, a notice that says what was
    /// cut and how to see more, and the kept end.
    Text: string
    WasTruncated: bool
    /// Length of the original, untruncated text.
    OriginalLength: int
    /// How many characters of the original are present in `Text` (excludes
    /// the notice itself).
    KeptLength: int
    /// How many of the kept characters are the start. The cut begins at this offset of the original.
    HeadLength: int }

/// The name the session binds a clipped reply's whole text to.
[<Literal>]
let OutputBinding = "lastEvalOutput"

/// The expression that gives back exactly the characters a clip cut: the cut starts at `headLength` and is `cut` long.
let readingTheCut (headLength: int) (cut: int) : string = sprintf "%s.Substring(%d, %d)" OutputBinding headLength cut

/// Bound `raw` to at most `maxChars` kept characters: its start, a notice that says exactly how many characters were
/// cut from the middle, and its end. `maxChars <= 0` is treated as 0 (an all-notice response) rather than raising — a
/// defensive floor, since this runs on the eval reply path and must never itself throw. A cut never splits a
/// surrogate pair, so one character fewer than `maxChars` may be kept.
///
/// `held` says the session now holds `raw` whole under `OutputBinding`: only then does the notice name that binding and an
/// expression that reads the cut. Otherwise it promises nothing it cannot keep.
let boundHolding (held: bool) (maxChars: int) (raw: string) : BoundedResult =
  let maxChars = max 0 maxChars
  match raw.Length <= maxChars with
  | true ->
    { Text = raw; WasTruncated = false; OriginalLength = raw.Length; KeptLength = raw.Length; HeadLength = raw.Length }
  | false ->
    let wantedHead = headChars maxChars
    let wantedTail = maxChars - wantedHead
    let headLength =
      match wantedHead > 0 && System.Char.IsHighSurrogate raw.[wantedHead - 1] with
      | true -> wantedHead - 1
      | false -> wantedHead
    let tailStart =
      match wantedTail > 0 && System.Char.IsLowSurrogate raw.[raw.Length - wantedTail] with
      | true -> raw.Length - wantedTail + 1
      | false -> raw.Length - wantedTail
    let cut = tailStart - headLength
    let counted = sprintf "cut %d characters from the middle: kept the first %d and the last %d of %d." cut headLength (raw.Length - tailStart) raw.Length
    let notice =
      match held with
      | true -> sprintf "\n… [%s The whole text is bound as `%s` in this session: read the middle with `%s`.]\n" counted OutputBinding (readingTheCut headLength cut)
      | false -> sprintf "\n… [%s If the last statement was an expression, its value is still bound as `it`: slice it (`it |> List.truncate 20`, `it.SomeField`).]\n" counted
    { Text = raw.Substring(0, headLength) + notice + raw.Substring tailStart
      WasTruncated = true
      OriginalLength = raw.Length
      KeptLength = headLength + (raw.Length - tailStart)
      HeadLength = headLength }

/// `boundHolding` for a text the session does not hold.
let bound (maxChars: int) (raw: string) : BoundedResult = boundHolding false maxChars raw

/// The eval-reply metadata for a bounded result — empty unless truncated,
/// else the three-field record `AppState.evalFn` attaches to `EvalResponse`.
let metadata (bounded: BoundedResult) : Map<string, objnull> =
  match bounded.WasTruncated with
  | true -> Map.ofList [ "resultTruncated", box true; "resultOriginalLength", box bounded.OriginalLength; "resultKeptLength", box bounded.KeptLength ]
  | false -> Map.empty

/// The most characters of a clipped reply's text the session is asked to hold under `OutputBinding`. A longer text is cut
/// and counted as before, and the notice does not promise a binding it did not make.
let maxHeldOutputChars = 1000000

/// F# source that binds `raw`, whole, to `OutputBinding`: one string literal, with every character that could end it or
/// change its meaning escaped, so what the session holds is `raw` exactly.
let bindingCode (raw: string) : string =
  let literal = System.Text.StringBuilder(raw.Length + 16)
  let escaped (c: char) = literal.AppendFormat("\\u{0:X4}", int c) |> ignore
  for i in 0 .. raw.Length - 1 do
    match raw.[i] with
    | '\\' -> literal.Append "\\\\" |> ignore
    | '"' -> literal.Append "\\\"" |> ignore
    | '\n' -> literal.Append "\\n" |> ignore
    | '\r' -> literal.Append "\\r" |> ignore
    | '\t' -> literal.Append "\\t" |> ignore
    | c when System.Char.IsControl c -> escaped c
    // A surrogate that has its other half beside it is written as it is; one that has not is written as its code unit.
    | c when System.Char.IsHighSurrogate c && i + 1 < raw.Length && System.Char.IsLowSurrogate raw.[i + 1] -> literal.Append c |> ignore
    | c when System.Char.IsLowSurrogate c && i > 0 && System.Char.IsHighSurrogate raw.[i - 1] -> literal.Append c |> ignore
    | c when System.Char.IsSurrogate c -> escaped c
    | c -> literal.Append c |> ignore
  sprintf "let %s = \"%s\"" OutputBinding (literal.ToString())

/// The expression that gives back exactly the characters a clipped reply cut, when there was a cut.
let suggestedExpression (bounded: BoundedResult) : string option =
  match bounded.WasTruncated with
  | true -> Some(readingTheCut bounded.HeadLength (bounded.OriginalLength - bounded.KeptLength))
  | false -> None

/// A reply's text, and the bound of the eval's own output inside it.
type ReplyText =
  { Text: string
    /// The bound of the eval's own output, which is what `resultTruncated` and the lengths describe.
    Own: BoundedResult }

/// The reply for an eval: its own output bounded first, then the output written between calls under its label. The block is
/// counted apart, so the kept end of a clipped reply is the eval's own end. When the eval's output had to be clipped,
/// `bind` is asked to hold the whole text in the session (true when it did), and the notice names the binding only then.
let compose (maxChars: int) (bind: string -> bool) (own: string) (between: string) : ReplyText =
  let plain = bound maxChars own
  let bounded =
    match plain.WasTruncated with
    | false -> plain
    | true ->
      let held = own.Length <= maxHeldOutputChars && bind own
      boundHolding held maxChars own
  { Text = withBetweenCalls between bounded.Text; Own = bounded }

/// What F# Interactive prints for a binding: `val it: unit = ()`, `val x: int = 3`. A program's own output and FSI's report share
/// one stream, and `printf "5"` writes no line break, so the stream held `5val it: unit = ()` (issue #132). The report starts
/// at the first thing on a line that looks like this, and the printed text it ran into is left where it was.
let private valueLine =
  System.Text.RegularExpressions.Regex(@"(?<=\S)[ \t]*(?=val (?:it|[A-Za-z_][A-Za-z0-9_']*)[ \t]*:)", System.Text.RegularExpressions.RegexOptions.Compiled)

/// Put a `val` report that follows printed text on the same line onto a line of its own. Text with no such run-together, and text
/// that already has its line break (LF or CRLF), comes back unchanged.
let startValueLinesOnTheirOwn (text: string) : string = valueLine.Replace(text, "\n")
