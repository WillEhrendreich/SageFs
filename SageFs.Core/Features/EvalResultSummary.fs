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
let maxResultChars = 4_000

/// What came back for a result we didn't return in full.
type BoundedResult =
  { /// What to actually send back — either the untouched `raw` text, or the
    /// kept prefix plus a notice describing what was cut and how to see more.
    Text: string
    WasTruncated: bool
    /// Length of the original, untruncated text.
    OriginalLength: int
    /// How many characters of the original are present in `Text` (excludes
    /// the notice itself).
    KeptLength: int }

/// Bound `raw` to at most `maxChars`. `maxChars <= 0` is treated as 0 (an
/// all-notice response) rather than raising — a defensive floor, since this
/// runs on the eval reply path and must never itself throw.
let bound (maxChars: int) (raw: string) : BoundedResult =
  let maxChars = max 0 maxChars
  match raw.Length <= maxChars with
  | true ->
    { Text = raw; WasTruncated = false; OriginalLength = raw.Length; KeptLength = raw.Length }
  | false ->
    let head = raw.Substring(0, maxChars)
    let notice =
      sprintf
        "\n\n… [truncated: showing the first %d of %d characters. The full value is still bound as `it` in this session — run e.g. `it.ToString().Substring(%d, 2000)`, `(sprintf \"%%A\" it).Substring(%d)`, or a targeted projection (`it |> List.truncate 20`, `it.SomeField`) to see more.]"
        maxChars raw.Length maxChars maxChars
    { Text = head + notice; WasTruncated = true; OriginalLength = raw.Length; KeptLength = maxChars }

/// The eval-reply metadata for a bounded result — empty unless truncated,
/// else the three-field record `AppState.evalFn` attaches to `EvalResponse`.
let metadata (bounded: BoundedResult) : Map<string, objnull> =
  match bounded.WasTruncated with
  | true -> Map.ofList [ "resultTruncated", box true; "resultOriginalLength", box bounded.OriginalLength; "resultKeptLength", box bounded.KeptLength ]
  | false -> Map.empty
