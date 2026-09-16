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

  /// Classify a compiler diagnostic's stable `FSharpDiagnostic.ErrorNumber`
  /// (the "39" in "FS0039") into an ErrorCategory. Numbers are locale-proof
  /// and unambiguous where the message text is not (a translated FSI, or an
  /// atypical phrasing, defeats `categorize`'s substring matching).
  ///
  /// `SageFs.Core/Features/Diagnostics.fs`'s `Diagnostic` and
  /// `SageFs.Core/WorkerProtocol.fs`'s `WorkerDiagnostic` both carry
  /// `ErrorNumber` end to end (FCS diagnostic → worker → daemon), and
  /// `check_fsharp_code` (`SageFs/Mcp.fs`'s `checkFSharpCode`) classifies its
  /// diagnostics through this function instead of `categorize`'s substring
  /// matching (roast-5 item #10). The flattened exception/error-string paths
  /// in `SageFs/Mcp.fs` (`formatWorkerEvalResult`, eval-failure tracking)
  /// still call `categorize` directly — they have no structured diagnostic to
  /// pull a number from, only composed text, so `categorizeByNumber None`
  /// would just fall back to the same text classification anyway.
  let categorizeByNumber (errorNumber: int option) (errorText: string) : ErrorCategory =
    match errorNumber with
    | Some 39 -> ErrorCategory.NameError // FS0039: the value/name is not defined
    | Some 1 -> ErrorCategory.TypeError // FS0001: type mismatch — the general type-error number
    | Some 10 -> ErrorCategory.SyntaxError // FS0010: unexpected token/keyword in binding
    | Some _ | None -> categorize errorText

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

  /// Path fragments that mark a stack frame as framework/runtime noise rather
  /// than the user's own code — used to find the first USER source frame in a
  /// runtime exception's stack (roast UX-3). Matched case-insensitively.
  let private frameworkFrameFragments =
    [| "/src/fsharp/"; "\\src\\fsharp\\"        // FSharp.Core / F# compiler build paths
       "/_work/"; "\\_work\\"; "/_/src/"          // dotnet CI build-machine paths
       "microsoft.fsharp."; "system."; "microsoft."
       "fsi_"; "startupcode$fsi"                   // FSI dynamic wrappers
       "/expecto/"; "\\expecto\\" |]

  /// The first "at ... in <file>:line N" frame whose file is the user's own
  /// source (a real .fs/.fsx path, not a framework/FSI frame), formatted as
  /// "File.fs(N)". None when the stack has no such frame (e.g. a bare REPL eval
  /// with only FSI/framework frames).
  let firstUserSourceFrame (stackTrace: string) : string option =
    match System.String.IsNullOrEmpty stackTrace with
    | true -> None
    | false ->
      stackTrace.Split([| '\n'; '\r' |], System.StringSplitOptions.RemoveEmptyEntries)
      |> Array.tryPick (fun raw ->
        let line = raw.Trim()
        let lower = line.ToLowerInvariant()
        let looksLikeSourceFrame =
          line.Contains(" in ") && (lower.Contains(".fs:line ") || lower.Contains(".fsx:line "))
        match looksLikeSourceFrame && not (frameworkFrameFragments |> Array.exists lower.Contains) with
        | false -> None
        | true ->
          let afterIn = line.Substring(line.IndexOf(" in ") + 4)
          match afterIn.LastIndexOf(":line ") with
          | idx when idx > 0 ->
            let filePath = afterIn.Substring(0, idx)
            let lineNo = afterIn.Substring(idx + ":line ".Length).Trim()
            Some (sprintf "%s(%s)" (System.IO.Path.GetFileName filePath) lineNo)
          | _ -> None)

  /// Summarize a runtime exception from a failed eval so the ACTIONABLE bits —
  /// the exception type, its message, and the first line of the USER's own code
  /// in the stack — lead, instead of being buried under framework frames (roast
  /// UX-3). `full` is the raw exception text (ex.ToString()); it is kept below
  /// the summary so nothing is lost.
  let runtimeExceptionSummary (typeName: string) (message: string) (full: string) : string =
    let head =
      match firstUserSourceFrame full with
      | Some frame -> sprintf "%s: %s\n  ↳ in your code at %s" typeName message frame
      | None -> sprintf "%s: %s" typeName message
    sprintf "%s\n\n%s" head full
