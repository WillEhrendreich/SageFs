/// A clipped reply tells the truth about what it cut. The roast of 2026-11-08 found two lies: the notice suggested
/// `(sprintf "%A" it).Substring(headLength, 2000)`, an offset into the whole printed text applied to a value that holds none
/// of the printfn output, and the kept end of a clipped reply was the between-calls block, because that block was appended
/// before the bound. The eval's own output is bounded first and the between-calls block follows it, counted apart, and the
/// notice names a binding that holds the whole text, with an expression these cases RUN.
module SageFs.Tests.EvalOutputClipTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.EvalResultSummary

/// A text full of what breaks a naive literal: quotes, backslashes, line breaks, tabs, controls, percent signs, braces, a
/// surrogate pair and wide characters. Seeded, so a failure repeats.
let private trickyText (seed: int) (length: int) : string =
  let alphabet = [| "a"; "b"; "Z"; "0"; " "; "\""; "\\"; "\n"; "\r\n"; "\t"; "\u0001"; "%"; "%A"; "{"; "}"; "é"; "日"; "😀"; "\"\"\""; "\\n" |]
  let random = Random seed
  let builder = Text.StringBuilder()
  while builder.Length < length do
    builder.Append alphabet.[random.Next alphabet.Length] |> ignore
  builder.ToString()

let private newSession () =
  FSharp.Compiler.Interactive.Shell.FsiEvaluationSession.Create(
    FSharp.Compiler.Interactive.Shell.FsiEvaluationSession.GetDefaultConfiguration(),
    [| "fsi"; "--noninteractive"; "--nologo"; "--readline-"; "--multiemit-" |],
    new StreamReader(Stream.Null),
    TextWriter.Null,
    TextWriter.Null,
    collectible = true
  )

[<Tests>]
let clipTests =
  testList "Eval output clip" [

    testProperty "WHY - the eval's own output is bounded first, so the kept end of a clipped reply is its own end and the between-calls block follows intact" <| fun (ownSeed: PositiveInt) (extra: PositiveInt) (betweenSeed: PositiveInt) ->
      let own = trickyText ownSeed.Get (maxResultChars + 1 + extra.Get % 3000)
      let between = "BG:" + trickyText betweenSeed.Get (1 + betweenSeed.Get % 900)
      let reply = compose maxResultChars (fun _ -> true) own between
      let blockAtEnd = reply.Text.EndsWith(BetweenCallsLabel + "\n" + between.TrimEnd(), StringComparison.Ordinal)
      // The reply joins the block to the clipped text with its trailing white space trimmed, so the check reads the last
      // characters of the own text that are not white space; the kept end is a third of the bound, far longer than that.
      let ownEnd = own.TrimEnd()
      let ownEndBeforeBlock = reply.Text.Contains(ownEnd.Substring(ownEnd.Length - 100))
      blockAtEnd && ownEndBeforeBlock

    testProperty "WHY - the lengths and the truncated flag describe the eval's own output alone, not the between-calls block" <| fun (ownSeed: PositiveInt) (extra: PositiveInt) ->
      let own = trickyText ownSeed.Get (maxResultChars + 1 + extra.Get % 3000)
      let withBlock = compose maxResultChars (fun _ -> true) own (String.replicate 50 "BG\n")
      let without = compose maxResultChars (fun _ -> true) own ""
      withBlock.Own = without.Own && withBlock.Own.OriginalLength = own.Length && withBlock.Own.WasTruncated

    testCase "WHY - a reply that fits is its own output and the block, and binds nothing" <| fun _ ->
      let mutable bound = false
      let reply = compose maxResultChars (fun _ -> bound <- true; true) "val it: int = 2\n" "tick\n"
      bound |> Expect.isFalse "nothing was bound for a reply that was not clipped"
      reply.Text |> Expect.equal "the output, the label, the block" (withBetweenCalls "tick\n" "val it: int = 2\n")
      (compose maxResultChars (fun _ -> true) "val it: int = 2\n" "").Text |> Expect.equal "no block, no change" "val it: int = 2\n"

    testCase "WHY - the clipped text is handed to be bound, whole, and the notice names the binding only when the session now holds it" <| fun _ ->
      let own = trickyText 7 (maxResultChars + 500)
      let seen = Collections.Generic.List<string>()
      let held = compose maxResultChars (fun raw -> seen.Add raw; true) own ""
      Seq.toList seen |> Expect.equal "the whole own text, once" [ own ]
      held.Text |> Expect.stringContains "the binding is named" OutputBinding
      let notHeld = compose maxResultChars (fun _ -> false) own ""
      notHeld.Text.Contains OutputBinding |> Expect.isFalse "a binding that was not made is not promised"
      notHeld.Text |> Expect.stringContains "but the cut is still counted" (sprintf "cut %d characters" (own.Length - notHeld.Own.KeptLength))

    testCase "WHY - the suggested expression RUNS in a session and returns exactly the characters the reply cut" <| fun _ ->
      use session = newSession ()
      for seed in 1..12 do
        let own = trickyText seed (maxResultChars + 100 + seed * 613)
        let reply = compose maxResultChars (fun _ -> true) own ""
        let expression =
          match suggestedExpression reply.Own with
          | Some expression -> expression
          | None -> failtestf "seed %d: no expression was suggested for a clipped reply" seed
        reply.Text |> Expect.stringContains (sprintf "seed %d: the notice shows the expression it means" seed) expression
        let _, bindDiagnostics = session.EvalInteractionNonThrowing(bindingCode own)
        bindDiagnostics |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
        |> Expect.isEmpty (sprintf "seed %d: the binding code compiles" seed)
        match session.EvalExpressionNonThrowing expression with
        | Choice1Of2(Some value), _ ->
          let cut = own.Substring(reply.Own.HeadLength, own.Length - reply.Own.KeptLength)
          (value.ReflectionValue :?> string) |> Expect.equal (sprintf "seed %d: the expression returns what was cut" seed) cut
        | other, _ -> failtestf "seed %d: the suggestion did not run: %A" seed other
  ]
