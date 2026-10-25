module SageFs.Tests.CompletionAnswerTests

/// The completion answer: candidates cut to a limit with the size of the set they came from, or the
/// reason nobody could look. These are the rules behind the 50-item cap that made `list_members`
/// report exactly 50 for `System.IO` and `List`, and behind the empty list that stood for every failure.
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.AutoCompletion
open SageFs.Tests.CompletionFixtures

let private candidate (name: string) : CompletionItem =
  { DisplayText = name; ReplacementText = name; Kind = CompletionKind.Variable; GetDescription = None }

let private numbered (count: int) : CompletionItem list =
  [ for i in 1 .. count -> candidate (sprintf "n%05d" i) ]

let private itemsAndTotal (answer: CompletionAnswer<'item>) : 'item list * CompletionTotal =
  match answer with
  | CompletionAnswer.Answered(items, total) -> items, total
  | CompletionAnswer.Unavailable reason -> failtestf "expected an answer, got %A" reason

let private everyUnavailable : CompletionUnavailable list =
  [ CompletionUnavailable.SessionNotActive
    CompletionUnavailable.CompilerError "c"
    CompletionUnavailable.HostLost "h"
    CompletionUnavailable.CheckThrew "t"
    CompletionUnavailable.NoSession "n"
    CompletionUnavailable.WorkerUnreachable "w"
    CompletionUnavailable.NotOffered "o" ]

[<Tests>]
let tests =
  testList "completion answers" [

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY — an editor's limit cuts the list but the total is the true size of the set, never the cut" <|
      fun (known: NonNegativeInt) (limit: NonNegativeInt) ->
        let items, total = select (CompletionLimit.UpTo limit.Get) (numbered (known.Get % 3000)) |> itemsAndTotal
        let size = known.Get % 3000
        items.Length = min limit.Get size && total = CompletionTotal.Exact size

    testCase "WHY — the 50-item cap no longer hides the size: 60 candidates under UpTo 50 give 50 items and an exact 60" <| fun _ ->
      let items, total = select (CompletionLimit.UpTo EditorCandidates) (numbered 60) |> itemsAndTotal
      items |> Expect.hasLength "the editor's cap" 50
      total |> Expect.equal "the true size" (CompletionTotal.Exact 60)

    testCase "WHY — EveryMember under the bound returns every member with an exact total, which is what the ladder needs for System.IO" <| fun _ ->
      let items, total = select CompletionLimit.EveryMember (numbered 212) |> itemsAndTotal
      items |> Expect.hasLength "all of them" 212
      total |> Expect.equal "exact" (CompletionTotal.Exact 212)

    testCase "WHY — EveryMember past the wire bound returns the bound and a floor, never an exact total it did not deliver" <| fun _ ->
      let items, total = select CompletionLimit.EveryMember (numbered (WireCeiling + 500)) |> itemsAndTotal
      items |> Expect.hasLength "the bound" WireCeiling
      total |> Expect.equal "a floor at the bound" (CompletionTotal.AtLeast WireCeiling)

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY — a total never overstates what the reply holds: an exact total is the real size, and a floor equals the items delivered" <|
      fun (size: NonNegativeInt) (limit: NonNegativeInt) (everyMember: bool) ->
        let n = size.Get % 3000
        let limitCase = if everyMember then CompletionLimit.EveryMember else CompletionLimit.UpTo limit.Get
        let items, total = select limitCase (numbered n) |> itemsAndTotal
        match total with
        | CompletionTotal.Exact t -> t = n
        | CompletionTotal.AtLeast t -> t = items.Length && t <= n

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "WHY — the items delivered are the best-ranked prefix, in order" <|
      fun (size: NonNegativeInt) (limit: NonNegativeInt) ->
        let all = numbered (size.Get % 500)
        let items, _ = select (CompletionLimit.UpTo limit.Get) all |> itemsAndTotal
        (items |> List.map (fun i -> i.DisplayText)) = (List.truncate limit.Get all |> List.map (fun i -> i.DisplayText))

    testCase "WHY — a source that failed is an unavailable answer, not an empty one, so no caller reads a failure as 'no members'" <| fun _ ->
      let failing _ _ _ = Result.Error(CompletionUnavailable.CompilerError "boom")
      match getCompletionsWith failing CompletionLimit.EveryMember "System.IO.Path." 15 "" with
      | CompletionAnswer.Unavailable(CompletionUnavailable.CompilerError "boom") -> ()
      | other -> failtestf "expected the failure to surface, got %A" other

    testCase "WHY — a source that listed nothing is an exact zero, because nobody failed to look" <| fun _ ->
      let empty _ _ _ = Result.Ok Seq.empty
      match getCompletionsWith empty CompletionLimit.EveryMember "System.IO.Path.Combine." 23 "" with
      | CompletionAnswer.Answered([], CompletionTotal.Exact 0) -> ()
      | other -> failtestf "expected an empty exact answer, got %A" other

    testCase "WHY — getCompletionsWith ranks everything before it cuts, so the limit takes the best and the total counts all" <| fun _ ->
      let source _ _ _ = Result.Ok(numbered 120 |> Seq.ofList)
      let items, total = getCompletionsWith source (CompletionLimit.UpTo 10) "x." 2 "" |> itemsAndTotal
      items |> Expect.hasLength "ten shown" 10
      total |> Expect.equal "all counted" (CompletionTotal.Exact 120)

    testCase "WHY — directive input never reaches the F# source and still states a total" <| fun _ ->
      let source _ _ _ = Result.Error(CompletionUnavailable.CheckThrew "must not be called")
      match getCompletionsWith source (CompletionLimit.UpTo EditorCandidates) "#" 1 "" with
      | CompletionAnswer.Answered(items, CompletionTotal.Exact total) -> total |> Expect.equal "every directive is counted" items.Length
      | other -> failtestf "expected the directive list, got %A" other

    testCase "WHY — every unavailable reason says what is true and what to do, and names itself with a stable word" <| fun _ ->
      for reason in everyUnavailable do
        CompletionUnavailable.describe reason |> Expect.isNotEmpty "describes itself"
        CompletionUnavailable.label reason |> Expect.isNotEmpty "has a wire word"
      everyUnavailable
      |> List.map CompletionUnavailable.label
      |> List.distinct
      |> Expect.hasLength "each reason has its own word" everyUnavailable.Length

    testCase "WHY — a total reads as a count when exact and as 'at least N' when it is a floor" <| fun _ ->
      CompletionTotal.describe (CompletionTotal.Exact 212) |> Expect.equal "exact" "212"
      CompletionTotal.describe (CompletionTotal.AtLeast 2000) |> Expect.equal "floor" "at least 2000"
      CompletionTotal.label (CompletionTotal.Exact 1) |> Expect.equal "exact word" "exact"
      CompletionTotal.label (CompletionTotal.AtLeast 1) |> Expect.equal "floor word" "atLeast"

    testCase "WHY — an entry and an item convert to each other without losing the kind, which is what crosses the wire" <| fun _ ->
      let item = { candidate "Combine" with Kind = CompletionKind.Method }
      let entry = CompletionEntry.ofItem item
      entry.Kind |> Expect.equal "kind survives" CompletionKind.Method
      (CompletionEntry.toItem entry).DisplayText |> Expect.equal "name survives" "Combine"
      (CompletionEntry.toItem entry).GetDescription.IsNone |> Expect.isTrue "the description does not cross the wire"
  ]
