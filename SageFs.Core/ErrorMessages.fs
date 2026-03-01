namespace SageFs

/// Better error messages for FSI errors
module ErrorMessages =

  /// Parse FSI error and extract useful information
  let parseError (errorText: string) = {|
    Message = errorText
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
        IsTypeError: bool
        IsSyntaxError: bool
        IsNameError: bool
        Line: int option
        Column: int option
      |})
    =
    let isEarlierError = error.Message.Contains("earlier error")
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
