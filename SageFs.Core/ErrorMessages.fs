namespace SageFs

/// Better error messages for FSI errors
module ErrorMessages =

  /// Parse FSI error and extract useful information
  let parseError (errorText: string) = {|
    Message = errorText
    IsTypeLoadException = errorText.Contains("TypeLoadException") || errorText.Contains("type identity")
    IsTypeError = errorText.Contains("type")
    IsSyntaxError = errorText.Contains("syntax") || errorText.Contains("unexpected")
    IsNameError = errorText.Contains("not defined") || errorText.Contains("not found")
    Line = None
    Column = None
  |}

  /// Generate helpful suggestion based on error type
  let getSuggestion
    (error:
      {|
        Message: string
        IsTypeLoadException: bool
        IsTypeError: bool
        IsSyntaxError: bool
        IsNameError: bool
        Line: int option
        Column: int option
      |})
    =
    let isTypeLoad = error.IsTypeLoadException
    let isEarlierError = error.Message.Contains("earlier error")
    match isTypeLoad with
    | true ->
      "⛔ TypeLoadException detected. This almost always means you used '#r' on an assembly that was already loaded by the '--proj' session startup. " +
      "Do NOT reset the session — it is NOT corrupted. " +
      "1. Call get_startup_info to see which assemblies are already loaded via the project graph. " +
      "2. Remove the '#r' directive — do NOT '#r' any assembly listed there. " +
      "3. If the duplicate context is stuck, use hard_reset_fsi_session with rebuild=false (not reset_fsi_session) to clear it. " +
      "4. Reference project types directly without '#r'. The types are already in scope."
    | false ->
    match isEarlierError with
    | true ->
      "⚠️ This 'earlier error' means a PREVIOUS statement had a compile error, so its definitions were never created. " +
      "The session is NOT corrupted — all successfully evaluated statements are still valid. " +
      "Fix the original error and re-submit that code, then retry. Do NOT reset the session."
    | false ->
      match error.IsNameError with
      | true ->
        "💡 Tip: A name is not defined. Check: did you open the right namespace? Is there a typo? " +
        "Did a previous submission fail (leaving the definition unbound)? Fix your code and resubmit."
      | false ->
        match error.IsTypeError with
        | true ->
          "💡 Tip: Type mismatch. Check your types carefully — F# is strict. Fix your code and resubmit."
        | false ->
          match error.IsSyntaxError with
          | true ->
            "💡 Tip: Syntax error. Check for missing ';;', unclosed brackets, or typos. Fix and resubmit."
          | false ->
            "💡 Tip: This error is in YOUR submitted code (99% of the time). " +
            "Try breaking your code into smaller pieces to isolate the issue. " +
            "Do NOT reset the session — previous definitions are still valid."

  /// Format error message in a friendly way
  let formatError (errorText: string) =
    let error = parseError errorText
    let suggestion = getSuggestion error
    sprintf "%s\n\n%s" errorText suggestion
