module SageFs.Middleware.EvaluableSubmission

#nowarn "57"

open Fantomas.FCS.Syntax
open SageFs.AppState

/// Why a code submission has nothing for FSI to execute.
[<RequireQualifiedAccess>]
type NothingToEvaluateReason =
  /// Only a `module X.Y` and/or `namespace X.Y` declaration — no binding,
  /// expression, type, or directive underneath it.
  | ModuleOrNamespaceHeaderOnly
  /// Nothing but whitespace and/or comments — no declaration at all.
  | BlankOrCommentsOnly

/// Whether a code submission actually has something for FSI to run.
///
/// A DU rather than a bool: "nothing to run" must carry WHY, because the two
/// reasons need two different, honest messages, and because the caller must
/// never have to re-derive "why" by re-inspecting the source text once this
/// has already parsed it once.
[<RequireQualifiedAccess>]
type EvaluableSubmission =
  | Executable
  | NothingToEvaluate of NothingToEvaluateReason

module EvaluableSubmission =

  /// True when this declaration list has anything FSI would actually run: a
  /// binding, an expression, a type, a hash directive (#r/#load/#I/#time),
  /// or an `open` (an open is inert but not an error, so it must stay
  /// Executable — see EvalSubmissionTests, "open System stays executable").
  /// Recurses into nested modules — a header wrapping real code is
  /// Executable; only an EMPTY declaration list at every level is not.
  let rec private hasContent (decls: SynModuleDecl list) : bool =
    decls
    |> List.exists (function
      | SynModuleDecl.NestedModule(decls = inner) -> hasContent inner
      | _ -> true)

  let private isNamedHeader (kind: SynModuleOrNamespaceKind) =
    match kind with
    | SynModuleOrNamespaceKind.AnonModule -> false
    | _ -> true

  /// Classify one eval submission — the raw code an editor or MCP call sends
  /// for a single eval, with no FSI `;;` in it (that is appended later on
  /// the eval path; see AppState.fs). Decided by PARSING with FCS (the same
  /// `Fantomas.FCS.Parse.parseFile` entry point `noInliningTargets` already
  /// uses just above this module, in CompilationContext.fs) rather than
  /// scanning source text, so a string literal or comment that merely
  /// mentions "module" can never be misread as a real header.
  ///
  /// Conservative by construction: any parse error, or any AST shape this
  /// function doesn't recognize as one of the two well-understood "nothing
  /// here" shapes, falls back to `Executable`. A wrong "nothing to
  /// evaluate" would silently swallow real code; a wrong "Executable" just
  /// forwards to FSI exactly like today. The risk is asymmetric, so the
  /// fallback direction is deliberate.
  let classify (code: string) : EvaluableSubmission =
    match code.Trim() with
    // Cheap and unambiguous: a submission that is nothing but whitespace has
    // nothing to parse in the first place. No FCS call needed, and no risk —
    // this can never match real code. (FCS itself does not treat an
    // all-whitespace program as decl-free the same way it treats "": it can
    // report a parse error instead, which the fallback below would read as
    // Executable — harmless, since blank input already no-ops in FSI today,
    // but this keeps blank and whitespace-only consistent with each other.)
    | "" -> EvaluableSubmission.NothingToEvaluate NothingToEvaluateReason.BlankOrCommentsOnly
    | _ ->
    try
      let input, diagnostics =
        Fantomas.FCS.Parse.parseFile false (Fantomas.FCS.Text.SourceText.ofString code) []
      let hasErrors = diagnostics |> List.exists (fun d -> d.Severity.IsError)
      match hasErrors, input with
      | false, ParsedInput.ImplFile(ParsedImplFileInput(contents = contents)) ->
        let anyContent =
          contents
          |> List.exists (fun (SynModuleOrNamespace(decls = decls)) -> hasContent decls)
        match anyContent with
        | true -> EvaluableSubmission.Executable
        | false ->
          let anyNamedHeader =
            contents
            |> List.exists (fun (SynModuleOrNamespace(kind = kind)) -> isNamedHeader kind)
          match anyNamedHeader with
          | true -> EvaluableSubmission.NothingToEvaluate NothingToEvaluateReason.ModuleOrNamespaceHeaderOnly
          | false -> EvaluableSubmission.NothingToEvaluate NothingToEvaluateReason.BlankOrCommentsOnly
      | _ -> EvaluableSubmission.Executable
    with _ ->
      EvaluableSubmission.Executable

  /// The plain-language message shown in place of sending the submission to
  /// FSI. Names the situation and the next action; never implies the user's
  /// code is broken, because it isn't.
  let message (reason: NothingToEvaluateReason) : string =
    match reason with
    | NothingToEvaluateReason.ModuleOrNamespaceHeaderOnly ->
      "Nothing to evaluate: that's a module/namespace header, with no code under it. " +
      "Put the cursor on a binding or expression, or use \"Evaluate Entire File\" to run the whole file."
    | NothingToEvaluateReason.BlankOrCommentsOnly ->
      "Nothing to evaluate: the selection is blank and/or comments only. Select a binding or expression to run."

/// Short-circuits a submission that has nothing for FSI to run — a bare
/// module/namespace header, or blank/comment-only text — into a plain
/// success instead of sending it to FSI. FSI cannot accept a module header
/// as a standalone interactive submission; it fails with "Operation could
/// not be completed due to earlier error" (see ErrorMessages.fs's
/// EarlierError case), a compile error that reads as if the user's code is
/// broken when it never was — this was measured live against a daemon: a
/// session's FIRST eval, "module Foo.Bar.Baz", fails that way.
///
/// Runs FIRST in commonMiddleware (see ActorCreation.fs): it never rewrites
/// code, it only ever short-circuits, so putting it ahead of every
/// code-rewriting middleware (FsiCompatibility, ViBind, OpenDirective,
/// ComputationExpression, NonBlockingRun, HotReloading) means an Executable
/// submission passes through completely untouched — `next` is called with
/// the request unmodified — and a NothingToEvaluate submission skips all of
/// that work instead of being rewritten and still failing at FSI.
let nothingToEvaluateMiddleware next (request, st) =
  match EvaluableSubmission.classify request.Code with
  | EvaluableSubmission.NothingToEvaluate reason ->
    { EvaluationResult = Ok (EvaluableSubmission.message reason)
      Diagnostics = [||]
      EvaluatedCode = request.Code
      Metadata = Map.empty },
    st
  | EvaluableSubmission.Executable -> next (request, st)
