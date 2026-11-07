// How much of a value F# Interactive prints, sized for the agent that reads it (docs/plans/eval-output-for-an-llm.md).
//
// Nothing past FSharp.Core. This file is compiled into SageFs.Core and into the isolated FSI host, so both kinds of
// session print under the same limits from one definition. FSI's own defaults (100 items, depth 100, width 78, size
// 10,000) suit a person at a terminal; a list of 24 nested records came to about 21,000 characters under them, context
// the agent pays for on every call.
namespace SageFs

/// The four limits F# Interactive's value printer takes.
type PrintLimits =
  { /// Items shown of a list, array or sequence before `...`.
    Length: int
    /// How deep nested values are opened.
    Depth: int
    /// Columns a line wraps at.
    Width: int
    /// Nodes printed in total.
    Size: int }

/// What a `let` prints back.
type LetEcho =
  /// `val x: int = 3`, FSI's own report.
  | ValueAndType
  /// `val x: int`. The value is what the agent just wrote, or a bare expression away.
  | TypeOnly

/// Everything the session's printer is told once, at creation.
type EvalPrintSettings =
  { Limits: PrintLimits
    LetEcho: LetEcho }

[<RequireQualifiedAccess>]
module EvalPrintSettings =

  /// What F# Interactive prints with when nobody sets anything.
  let fsiDefaults : PrintLimits =
    { Length = 100
      Depth = 100
      Width = 78
      Size = 10_000 }

  /// The limits for an agent: 20 items, depth 6, width 140, size 600. FsiMcp 0.3.0 prints under the same four numbers.
  let agentLimits : PrintLimits =
    { Length = 20
      Depth = 6
      Width = 140
      Size = 600 }

  /// What a session prints with unless a directory says otherwise.
  let forAgent : EvalPrintSettings =
    { Limits = agentLimits
      LetEcho = TypeOnly }

  /// FSI's own printing, unchanged. The twin that proves a test of the limits has teeth.
  let asFsiDefaults : EvalPrintSettings =
    { Limits = fsiDefaults
      LetEcho = ValueAndType }

  /// Whether FSI should print a `let`'s value after its type. FSI's own switch is a bool, so this is the one place the closed choice becomes it.
  let showsDeclarationValues (echo: LetEcho) : bool =
    match echo with
    | ValueAndType -> true
    | TypeOnly -> false
