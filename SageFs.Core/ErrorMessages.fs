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
  let categorize (errorText: string) =
    match () with
    | _ when errorText.Contains("TypeLoadException") || errorText.Contains("type identity") -> ErrorCategory.TypeLoad
    | _ when errorText.Contains("earlier error") -> ErrorCategory.EarlierError
    | _ when errorText.Contains("not defined") || errorText.Contains("not found") -> ErrorCategory.NameError
    | _ when errorText.Contains("syntax") || errorText.Contains("unexpected") -> ErrorCategory.SyntaxError
    | _ when errorText.Contains("type") -> ErrorCategory.TypeError
    | _ -> ErrorCategory.Unknown

  /// Generate helpful suggestion based on error category.
  let getSuggestion (category: ErrorCategory) =
    match category with
    | ErrorCategory.TypeLoad ->
      "⚠️ TypeLoadException detected — this MAY indicate a type identity conflict. " +
      "Do NOT immediately reset — try fixing your code first. " +
      "Remove duplicate '#r' directives, fix 'open' collisions, " +
      "or resubmit without the offending code. If a subsequent eval succeeds, the session is fine. " +
      "If ALL subsequent evals fail with TypeLoadException, the session is genuinely poisoned — " +
      "use hard_reset_fsi_session with rebuild=true to recover. " +
      "Common causes: (1) '#r' on an assembly already loaded by '--proj' startup " +
      "(call get_startup_info to check). (2) Redefining a type that collides with a project DLL type."
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
