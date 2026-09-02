namespace SageFs

/// Better error messages for FSI errors
module ErrorMessages =

  /// Categorization of FSI error types — first match wins, priority is explicit.
  [<RequireQualifiedAccess>]
  type ErrorCategory =
    | TypeLoad
    | EarlierError
    | NameError
    | TypeError
    | SyntaxError
    | Unknown

  /// Classify an FSI error string into an ErrorCategory.
  /// Only the FIRST line is classified: FSI puts the actual error message on
  /// the first line, and anything after it can be a stack dump whose frames
  /// mention "type"/"syntax"/"not found" and would skew the category.
  /// Checks are ordered most-specific-first so "not found" (a file/name
  /// resolution issue) never lands in TypeError via a later generic "type"
  /// match, and stack-frame noise can't reclassify a real error.
  let categorize (errorText: string) =
    let firstLine =
      errorText.Split([| '\n'; '\r' |], System.StringSplitOptions.RemoveEmptyEntries)
      |> Array.tryHead
      |> Option.defaultValue errorText
    match () with
    | _ when firstLine.Contains("TypeLoadException") || firstLine.Contains("type identity") -> ErrorCategory.TypeLoad
    | _ when firstLine.Contains("earlier error") -> ErrorCategory.EarlierError
    | _ when firstLine.Contains("unexpected") || firstLine.Contains("syntax") -> ErrorCategory.SyntaxError
    | _ when firstLine.Contains("not defined") || firstLine.Contains("not found") || firstLine.Contains("does not exist") -> ErrorCategory.NameError
    | _ when firstLine.Contains("type mismatch") || firstLine.Contains("expected to have type") || firstLine.Contains("but given") -> ErrorCategory.TypeError
    | _ when firstLine.Contains("type") -> ErrorCategory.TypeError
    | _ -> ErrorCategory.Unknown

  /// Generate helpful suggestion based on error category.
  let getSuggestion (category: ErrorCategory) =
    match category with
    | ErrorCategory.TypeLoad ->
      "⚠️ TypeLoadException detected — most likely cause: a '#r' directive on an assembly already " +
      "loaded by '--proj' startup (call get_startup_info to check). " +
      "PRIMARY FIX: remove the duplicate '#r' directive from your code and resubmit — this resolves it 90% of the time. " +
      "Do NOT reset the session yet. " +
      "Try submitting a trivial expression (e.g. '1 + 1;;') — if it succeeds, your session is fine. " +
      "Only if completely unrelated, trivial evals ALSO fail with TypeLoadException should you consider " +
      "hard_reset_fsi_session with rebuild=true as a last resort. " +
      "Other causes: (2) Redefining a type that collides with a project DLL type — fix 'open' collisions or rename your type."
    | ErrorCategory.EarlierError ->
      "⚠️ This 'earlier error' means a PREVIOUS statement had a compile error, so its definitions were never created. " +
      "The session is NOT corrupted — all successfully evaluated statements are still valid. " +
      "Fix the original error and re-submit that code, then retry. Do NOT reset the session."
    | ErrorCategory.NameError ->
      "💡 Tip: A name is not defined. Check: did you open the right namespace? Is there a typo? " +
      "Did a previous submission fail (leaving the definition unbound)? Fix your code and resubmit."
    | ErrorCategory.TypeError ->
      "💡 Tip: Type mismatch. Check your types carefully — F# is strict. Fix your code and resubmit."
    | ErrorCategory.SyntaxError ->
      "💡 Tip: Syntax error. Check for missing ';;', unclosed brackets, or typos. Fix and resubmit."
    | ErrorCategory.Unknown ->
      "💡 Tip: This error is in YOUR submitted code (99% of the time). " +
      "Try breaking your code into smaller pieces to isolate the issue. " +
      "Do NOT reset the session — previous definitions are still valid."

  /// Format error message in a friendly way
  let formatError (errorText: string) =
    let suggestion = errorText |> categorize |> getSuggestion
    sprintf "%s\n\n%s" errorText suggestion
