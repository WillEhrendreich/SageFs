// WHY — the daemon sends `{case, message, suggestedAction}` on every
// structured error, and the remedy sentence it carries reached nobody
// (sagefs-ux-roast.md §6.4). Two sites got it wrong in opposite directions:
//
//   * the session-error dialog passed `suggestedAction` as the BUTTON CAPTION
//     (VS Code truncates those) and, when pressed, appended that same sentence
//     to an output channel the user was not looking at;
//   * the app-run failure dialog showed `message` with NO buttons and put
//     `suggestedAction` in a status-bar TOOLTIP — the actionable half on a
//     hover, the dead end in the modal.
//
// These pin the one rule, borrowed from the repo's own
// `ReloadOutcome.describeForUser`: what happened, then `→` the remedy, both in
// the message BODY. The last test is the structural half — it reads every
// `src/*.fs` and fails if `suggestedAction` ever appears inside a button array
// again, which is the only thing that stops this exact defect returning.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring AppRunContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/ErrorPresentationPure.fs"

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs.Vscode.ErrorPresentationPure

let private err case msg action : StructuredError =
  { Case = case; Message = msg; SuggestedAction = action }

let tests =
  testList "VS Code structured-error presentation" [

    testCase "WHY - the remedy lands in the body, because it used to be a button caption" <| fun _ ->
      let text = describe (err "EvalFailed" "Evaluation failed: FS0039" "Check the SageFs log for details")
      text |> Expect.stringContains "what happened" "Evaluation failed: FS0039"
      text |> Expect.stringContains "and what to do" "Check the SageFs log for details"
      text |> Expect.stringContains "joined the way ReloadOutcome joins them" "→ "

    testCase "WHY - an error with no remedy is still a whole sentence, not a trailing arrow" <| fun _ ->
      describe (err "EvalFailed" "Evaluation failed." "")
      |> Expect.equal "no dangling arrow" "Evaluation failed."
      describe (err "EvalFailed" "Evaluation failed." "   ")
      |> Expect.equal "whitespace is not a remedy" "Evaluation failed."

    testCase "WHY - a message-less error still says something, because an empty dialog is worse than a vague one" <| fun _ ->
      describe (err "WorkerUnreachable" "" "")
      |> Expect.stringContains "names the case" "WorkerUnreachable"
      describe (err "" "" "")
      |> Expect.isNotEmpty "never empty"

    testCase "WHY - the inline form carries the same facts on one line, for a status bar" <| fun _ ->
      let one = describeInline (err "X" "Session faulted." "Run a hard reset.")
      one |> Expect.stringContains "what happened" "Session faulted."
      one |> Expect.stringContains "and what to do" "Run a hard reset."
      one.Contains "\n" |> Expect.isFalse "single line"

    testCase "WHY - a remedy sentence is recognisably prose, which is why it made a terrible button" <| fun _ ->
      looksLikeProseNotAButton "Check the SageFs log for details." |> Expect.isTrue "ends in a stop"
      looksLikeProseNotAButton "Run `dotnet build` on it, then hard_reset_fsi_session" |> Expect.isTrue "too long for a button"
      looksLikeProseNotAButton "Show Output" |> Expect.isFalse "a real button caption"
      looksLikeProseNotAButton "Restart Session" |> Expect.isFalse "a real button caption"

    testCase "WHY - no source file passes suggestedAction as a dialog button ever again" <| fun _ ->
      // The structural gate. `Window.show*Message <body> [| ...buttons... |]` —
      // if `suggestedAction` appears anywhere inside that button array, the
      // remedy is being rendered as a control label again.
      let offenders =
        Directory.GetFiles(Path.Combine(__SOURCE_DIRECTORY__, "..", "src"), "*.fs")
        |> Array.collect (fun f ->
          let text = File.ReadAllText f
          Regex.Matches(text, @"\[\|[^\]]*suggestedAction[^\]]*\|\]")
          |> Seq.map (fun m -> sprintf "%s: %s" (Path.GetFileName f) (m.Value.Trim()))
          |> Array.ofSeq)
        |> Array.sort
      offenders
      |> Expect.isEmpty (sprintf "suggestedAction used as a button caption: %s" (String.concat " | " offenders))

    testCase "WHY - the regex above still finds button arrays, so the gate cannot pass vacuously" <| fun _ ->
      let sample = """Window.showErrorMessage msg [| err.suggestedAction; "Show Output" |]"""
      Regex.IsMatch(sample, @"\[\|[^\]]*suggestedAction[^\]]*\|\]")
      |> Expect.isTrue "the historical defect shape is detected"
      let fixedUp = """Window.showErrorMessage (describeSessionError err) [| "Restart Session"; "Show Output" |]"""
      Regex.IsMatch(fixedUp, @"\[\|[^\]]*suggestedAction[^\]]*\|\]")
      |> Expect.isFalse "the corrected shape is not flagged"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
