/// What an eval's reply looks like to the agent that reads it (docs/plans/eval-output-for-an-llm.md).
/// A clipped result keeps its start and its end: the end is where a final value or an error summary sits.
module SageFs.Tests.EvalOutputShapeTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.EvalResultSummary

/// A text whose every position holds a different character (CJK ideographs, no surrogates), so a test can tell which
/// part of it came back.
let private distinct (length: int) : string = System.String(Array.init length (fun i -> char (0x4E00 + i)))

/// A cap from 1 to 200 and a text that is longer than it by 1 to 500 characters.
let private clipCase (capSeed: PositiveInt) (extraSeed: PositiveInt) : int * string =
  let cap = 1 + capSeed.Get % 200
  cap, distinct (cap + 1 + extraSeed.Get % 500)

/// The notice may add this much on top of the kept characters.
let private noticeAllowance = 500

[<Tests>]
let evalOutputShapeTests =
  testList "Eval output shape" [

    testProperty "WHY - a clipped result keeps the start and the end of what was printed" <| fun (capSeed: PositiveInt) (extraSeed: PositiveInt) ->
      let cap, raw = clipCase capSeed extraSeed
      let result = bound cap raw
      let headCheck = result.Text.StartsWith(raw.Substring(0, cap / 2), System.StringComparison.Ordinal)
      let tailCheck = result.Text.EndsWith(raw.Substring(raw.Length - cap / 3), System.StringComparison.Ordinal)
      headCheck && tailCheck

    testProperty "WHY - a clipped result is no longer than the bound plus its notice" <| fun (capSeed: PositiveInt) (extraSeed: PositiveInt) ->
      let cap, raw = clipCase capSeed extraSeed
      let result = bound cap raw
      result.Text.Length <= cap + noticeAllowance

    testProperty "WHY - the notice says exactly how many characters were cut" <| fun (capSeed: PositiveInt) (extraSeed: PositiveInt) ->
      let cap, raw = clipCase capSeed extraSeed
      let result = bound cap raw
      result.Text.Contains(sprintf "cut %d characters" (result.OriginalLength - result.KeptLength))

    testProperty "WHY - text at or under the bound comes back untouched" <| fun (capSeed: PositiveInt) (lengthSeed: PositiveInt) ->
      let cap = 1 + capSeed.Get % 200
      let raw = distinct (lengthSeed.Get % (cap + 1))
      let result = bound cap raw
      result.Text = raw && not result.WasTruncated

    testProperty "WHY - the structured result says whether the reply was cut and by how much" <| fun (capSeed: PositiveInt) (extraSeed: PositiveInt) ->
      let cap, raw = clipCase capSeed extraSeed
      let meta = metadata (bound cap raw)
      let read (key: string) = meta |> Map.tryFind key
      read "resultTruncated" = Some(box true)
      && read "resultOriginalLength" = Some(box raw.Length)
      && (match read "resultKeptLength" with
          | Some kept -> unbox<int> kept = cap
          | None -> false)
  ]

/// Records nested three deep, 24 of them: the shape FsiMcp's author measured at about 21,000 characters under FSI's defaults.
let private recordSetup =
  [ "type Addr = { Street: string; City: string; Zip: int };;"
    "type Person = { Name: string; Age: int; Home: Addr; Tags: string list };;"
    "let people = [ for i in 1..24 -> { Name = sprintf \"person-%d\" i; Age = 20 + i; Home = { Street = sprintf \"%d Long Street Name\" i; City = \"Springfield\"; Zip = 10000 + i }; Tags = [ \"alpha\"; \"beta\"; \"gamma\" ] } ];;" ]

/// What an in-process FSI session prints for each submission, under `settings`.
let private printedUnder (settings: SageFs.EvalPrintSettings) (submissions: string list) : string list =
  let out = new System.IO.StringWriter()
  use session =
    FSharp.Compiler.Interactive.Shell.FsiEvaluationSession.Create(
      SageFs.FsiPrintConfig.forSession settings,
      [| "fsi"; "--noninteractive"; "--nologo"; "--readline-" |],
      new System.IO.StreamReader(System.IO.Stream.Null),
      out,
      new System.IO.StringWriter(),
      collectible = true
    )
  [ for submission in submissions do
      out.GetStringBuilder().Clear() |> ignore
      session.EvalInteractionNonThrowing submission |> ignore
      out.ToString() ]

let private itemsShown (text: string) : int = System.Text.RegularExpressions.Regex.Matches(text, "person-").Count

[<Tests>]
let printLimitTests =
  testList "Eval output print limits" [

    testCase "WHY - a bare expression prints at most the limit's length in items, where FSI's own defaults print them all" <| fun _ ->
      let limits = SageFs.EvalPrintSettings.agentLimits
      let printOf settings = printedUnder settings (recordSetup @ [ "people;;" ]) |> List.last
      let underAgent = printOf SageFs.EvalPrintSettings.forAgent
      let underDefaults = printOf SageFs.EvalPrintSettings.asFsiDefaults
      itemsShown underDefaults |> Expect.equal "the twin: FSI's defaults print all 24 records, so a limit has something to cut" 24
      (itemsShown underAgent <= limits.Length) |> Expect.isTrue (sprintf "at most %d items under the agent limits, got %d" limits.Length (itemsShown underAgent))
      (underAgent.Length < underDefaults.Length) |> Expect.isTrue "and the print is shorter"

    testCase "WHY - a let echoes its type and not its value under the agent settings, and keeps the value under FSI's defaults" <| fun _ ->
      let echoOf settings = printedUnder settings recordSetup |> List.last
      let underAgent = echoOf SageFs.EvalPrintSettings.forAgent
      let underDefaults = echoOf SageFs.EvalPrintSettings.asFsiDefaults
      underAgent |> Expect.stringContains "the type is named" "val people: Person list"
      itemsShown underAgent |> Expect.equal "no value under the agent settings" 0
      (itemsShown underDefaults > 0) |> Expect.isTrue "the twin: FSI's defaults echo the value"

    testCase "WHY - a bare expression's value is still printed under the agent settings, because TypeOnly is about a let" <| fun _ ->
      let printed = printedUnder SageFs.EvalPrintSettings.forAgent [ "1 + 2;;" ] |> List.last
      printed |> Expect.stringContains "the value of a bare expression" "val it: int = 3"
  ]
