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
      "⛔ TypeLoadException detected — the session's type identity is now compromised. " +
      "All subsequent evals referencing the affected type will fail. " +
      "Common causes: (1) You used '#r' on an assembly already loaded by the '--proj' session startup — " +
      "call get_startup_info to check which assemblies are loaded and remove the '#r'. " +
      "(2) You redefined a type in the REPL that collides with a type in the project DLL — " +
      "this happens when your .fs source has uncommitted/unbuilt type changes that differ from the loaded DLL. " +
      "Recovery: use hard_reset_fsi_session with rebuild=true to rebuild the project and get a fresh session " +
      "with up-to-date DLLs. Do NOT use soft reset (reset_fsi_session) — it cannot fix type identity issues."
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
