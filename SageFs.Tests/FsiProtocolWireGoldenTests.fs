module SageFs.Tests.FsiProtocolWireGoldenTests

/// The FSI host protocol's wire bytes, pinned. The codec in SageFs.FsiHost/FsiProtocol.fs is generic over the
/// protocol's own F# types, and it is the hot path of every eval and every live-testing cycle, so it gets
/// rewritten for speed. This file is what makes "the wire did not change" a test instead of a hope.
///
/// Two golden files, both produced by the codec itself:
///   * `wire-golden.tsv`   : one line per sample value, `name<TAB>json`. The samples are built from the types by
///                           reflection (every case of `Request` and `Response`, with every case of every union they
///                           carry reached), so a new case or field is covered without touching this file. Each
///                           line pins the ENCODING (byte for byte) and the DECODING (the json reads back as the
///                           value).
///   * `errors-golden.tsv` : one line per malformed or odd input, `kind<TAB>input<TAB>result`. Pins which error comes
///                           back for which input, and which odd-but-valid inputs are still accepted.
///
/// A deliberate protocol change (a field or case added) changes the encoding of the samples that mention it, and
/// the failure says which line. Re-pin with `SAGEFS_UPDATE_FSI_WIRE_GOLDEN=1`, run this list once, and review the diff.

open System
open System.Collections.Generic
open System.IO
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs.FsiHost.FsiProtocol

let private repoRoot = RepoPaths.repoPathFull [||]

let private goldenDir = Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "FsiProtocol")

[<Literal>]
let private UpdateVariable = "SAGEFS_UPDATE_FSI_WIRE_GOLDEN"

let private updateRequested () =
  match Environment.GetEnvironmentVariable UpdateVariable with
  | null -> false
  | value -> value = "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)

let private texts =
  [| "plain"
     "line1\nline2\r\nline3"
     "quote \" backslash \\ slash /"
     "\u0000\u0001\u001f control"
     "日本語 🚀 émoji"
     "<tag a='b'> & </tag>"
     ""
     "{\"case\":\"Shutdown\"}"
     "tab\there" |]

let private ints = [| 0; 1; -1; 42; Int32.MaxValue; Int32.MinValue; 7 |]

let private int64s = [| 0L; 1L; -1L; 9007199254740993L; Int64.MaxValue; Int64.MinValue; 123456789012L |]

let private moments =
  [| DateTimeOffset(2026, 10, 17, 12, 30, 45, TimeSpan(2, 0, 0)).AddTicks 1234L
     DateTimeOffset.UnixEpoch
     DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan(-5, -30, 0)) |]

let private durations = [| TimeSpan.Zero; TimeSpan.FromTicks 15000000L; TimeSpan.FromTicks -1L; TimeSpan.FromTicks 123456789L |]

/// The lengths lists and arrays are given, in turn.
let private listLengths = [| 2; 1; 3; 0; 2 |]

/// How deep a list or array is still filled; below it they are empty, so a recursive type ends.
let private fillDepth = 5

/// How deep a union still picks by rarity; below it, the case with the fewest fields, so a recursive union ends.
let private unionDepth = 12

/// Builds sample values from types. Deterministic: every leaf cycles through a fixed table, and a union picks the case
/// it has produced FEWEST times (ties to the lowest tag), so across enough samples every case of every union is reached.
type private Sampler() =
  let visited = Dictionary<string, int>()
  let mutable textCounter = 0
  let mutable intCounter = 0
  let mutable int64Counter = 0
  let mutable boolCounter = 0
  let mutable listCounter = 0
  let mutable momentCounter = 0
  let mutable durationCounter = 0

  member _.Visited = visited

  member this.Sample(depth: int, t: Type) : obj =
    if t = typeof<string> then
      textCounter <- textCounter + 1
      box texts.[textCounter % texts.Length]
    elif t = typeof<int> then
      intCounter <- intCounter + 1
      box ints.[intCounter % ints.Length]
    elif t = typeof<int64> then
      int64Counter <- int64Counter + 1
      box int64s.[int64Counter % int64s.Length]
    elif t = typeof<bool> then
      boolCounter <- boolCounter + 1
      box (boolCounter % 2 = 0)
    elif t = typeof<DateTimeOffset> then
      momentCounter <- momentCounter + 1
      box moments.[momentCounter % moments.Length]
    elif t = typeof<TimeSpan> then
      durationCounter <- durationCounter + 1
      box durations.[durationCounter % durations.Length]
    elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then
      let elementType = t.GetGenericArguments().[0]
      let items = this.Items(depth, elementType)
      let listType = typedefof<list<_>>.MakeGenericType elementType
      let cons = listType.GetMethod("Cons")
      let empty = listType.GetProperty("Empty").GetValue null
      Array.foldBack (fun item tail -> cons.Invoke(null, [| item; tail |])) items empty
    elif t.IsArray then
      let elementType = t.GetElementType()
      let items = this.Items(depth, elementType)
      let array = Array.CreateInstance(elementType, items.Length)
      items |> Array.iteri (fun index item -> array.SetValue(item, index))
      box array
    elif FSharpType.IsRecord t then
      let values = FSharpType.GetRecordFields t |> Array.map (fun field -> this.Sample(depth + 1, field.PropertyType))
      FSharpValue.MakeRecord(t, values)
    elif FSharpType.IsUnion t then
      let cases = FSharpType.GetUnionCases t
      let key (case: UnionCaseInfo) = t.FullName + "." + case.Name
      let count case =
        match visited.TryGetValue(key case) with
        | true, n -> n
        | false, _ -> 0
      let chosen =
        match depth > unionDepth with
        | true -> cases |> Array.minBy (fun case -> case.GetFields().Length)
        | false -> cases |> Array.minBy count
      this.OfCase(depth, t, chosen)
    else
      invalidOp (sprintf "no sample for %s" t.FullName)

  member this.OfCase(depth: int, t: Type, case: UnionCaseInfo) : obj =
    let key = t.FullName + "." + case.Name
    visited.[key] <- (match visited.TryGetValue key with | true, n -> n + 1 | false, _ -> 1)
    let values = case.GetFields() |> Array.map (fun field -> this.Sample(depth + 1, field.PropertyType))
    FSharpValue.MakeUnion(case, values)

  /// Some elements, in a cycle of lengths that includes empty, so empty and non-empty both occur; none below the fill depth.
  member this.Items(depth: int, elementType: Type) : obj[] =
    match depth > fillDepth with
    | true -> [||]
    | false ->
      listCounter <- listCounter + 1
      let count = listLengths.[listCounter % listLengths.Length]
      Array.init count (fun _ -> this.Sample(depth + 1, elementType))

type private Sample =
  | RequestSample of name: string * Request
  | ResponseSample of name: string * Response

/// Every union type reachable from `types` (through lists, arrays, records and union cases), themselves included.
let private unionsReachableFrom (types: Type list) : Type list =
  let seen = HashSet<Type>()
  let unions = ResizeArray<Type>()
  let rec walk (t: Type) =
    if seen.Add t then
      if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then walk (t.GetGenericArguments().[0])
      elif t.IsArray then walk (t.GetElementType())
      elif FSharpType.IsRecord t then FSharpType.GetRecordFields t |> Array.iter (fun field -> walk field.PropertyType)
      elif FSharpType.IsUnion t then
        unions.Add t
        FSharpType.GetUnionCases t |> Array.iter (fun case -> case.GetFields() |> Array.iter (fun field -> walk field.PropertyType))
  types |> List.iter walk
  List.ofSeq unions

/// The fewest and most samples of one top-level case. A case is sampled until every case of every union its fields
/// reach has been produced somewhere (and at least the minimum, so it also appears with different leaf values).
let private fewestSamples = 2
let private mostSamples = 60

let private build () : Sampler * Sample list =
  let sampler = Sampler()
  let isCovered (union: Type) =
    FSharpType.GetUnionCases union |> Array.forall (fun case -> sampler.Visited.ContainsKey(union.FullName + "." + case.Name))
  let sampleCase (topLevel: Type) (case: UnionCaseInfo) (make: string -> obj -> Sample) : Sample list =
    let reach = unionsReachableFrom (case.GetFields() |> Array.map (fun field -> field.PropertyType) |> List.ofArray)
    let rec loop (variant: int) (acc: Sample list) =
      let value = sampler.OfCase(0, topLevel, case)
      let acc = make (sprintf "%s.%s#%d" topLevel.Name case.Name variant) value :: acc
      match variant + 1 >= mostSamples || (variant + 1 >= fewestSamples && List.forall isCovered reach) with
      | true -> List.rev acc
      | false -> loop (variant + 1) acc
    loop 0 []
  let samples =
    [ for case in FSharpType.GetUnionCases typeof<Request> do
        yield! sampleCase typeof<Request> case (fun name value -> RequestSample(name, value :?> Request))
      for case in FSharpType.GetUnionCases typeof<Response> do
        yield! sampleCase typeof<Response> case (fun name value -> ResponseSample(name, value :?> Response)) ]
  sampler, samples

let private nameOf sample =
  match sample with
  | RequestSample(name, _)
  | ResponseSample(name, _) -> name

let private encodeSample sample =
  match sample with
  | RequestSample(_, request) -> encodeRequest request
  | ResponseSample(_, response) -> encodeResponse response

/// True when `json` decodes back to exactly the sample's value.
let private readsBackAs sample (json: string) =
  match sample with
  | RequestSample(_, request) -> decodeRequest json = Result.Ok request
  | ResponseSample(_, response) -> decodeResponse json = Result.Ok response

/// Every union type the protocol carries, found from the types, so the coverage check has nothing to keep in step.
let private reachableUnions () : Type list = unionsReachableFrom [ typeof<Request>; typeof<Response> ]

let private readGolden (file: string) : string list =
  let path = Path.Combine(goldenDir, file)
  File.Exists path
  |> Expect.isTrue (sprintf "golden file '%s' should exist; run with %s=1 to create it" file UpdateVariable)
  File.ReadAllLines path |> List.ofArray

let private writeGolden (file: string) (lines: string list) =
  Directory.CreateDirectory goldenDir |> ignore
  File.WriteAllLines(Path.Combine(goldenDir, file), lines)

let private wireFile = "wire-golden.tsv"
let private errorsFile = "errors-golden.tsv"

// ---- malformed and odd inputs ---------------------------------------------------------------

type private Decoder =
  | AsRequest
  | AsResponse

/// One input, shown as the codec answers it. NotJson shows only its tag: the parser's wording is the runtime's.
let private describeAnswer (result: Result<'a, ProtocolError>) : string =
  match result with
  | Result.Ok value -> sprintf "Ok %A" value |> fun text -> text.Replace("\r", "").Replace("\n", " ")
  | Result.Error(NotJson _) -> "Error NotJson"
  | Result.Error error -> sprintf "Error %A | %s" error (describeError error)

let private answer (decoder: Decoder) (input: string) : string =
  match decoder with
  | AsRequest -> describeAnswer (decodeRequest input)
  | AsResponse -> describeAnswer (decodeResponse input)

let private requestInputs =
  [ "null"; "123"; "\"str\""; "[1,2,3]"; "{}"; "true"; ""; "   "; "{nope"; "{\"case\":"
    """{"case":42}"""; """{"case":null}"""; """{"case":["Eval"]}"""; """{"case":"eval"}"""; """{"case":"dance"}"""; """{"case":""}"""
    """{"Case":"Interrupt"}"""
    // the case alone is enough for a case with no fields; extras are ignored
    """{"case":"Interrupt"}"""; """{"case":"Shutdown"}"""; """{"case":"Shutdown","fields":5}"""; """{"case":"Shutdown","other":[1,{"a":null}]}"""
    // an escaped property name is the same name
    "{\"c\\u0061se\":\"Interrupt\"}"; "{\"case\":\"Interr\\u0075pt\"}"
    // duplicate properties
    """{"case":"Eval","case":"Shutdown"}"""; """{"case":"Shutdown","case":"Eval"}"""
    """{"case":"Eval","fields":{"id":1,"id":2,"code":"a","code":"b"}}"""
    // fields: missing, wrong kind, wrong content
    """{"case":"Eval"}"""; """{"case":"Eval","fields":null}"""; """{"case":"Eval","fields":5}"""; """{"case":"Eval","fields":[1,"a"]}"""
    """{"case":"Eval","fields":{}}"""; """{"case":"Eval","fields":{"id":1}}"""; """{"case":"Eval","fields":{"code":"a"}}"""
    """{"case":"Eval","fields":{"id":"x","code":1}}"""; """{"case":"Eval","fields":{"id":1,"code":1}}"""; """{"case":"Eval","fields":{"id":1,"code":null}}"""
    """{"case":"Eval","fields":{"id":1,"code":"a"}}"""; """{"case":"Eval","fields":{"id":1,"code":"a","extra":true}}"""
    """{"case":"Eval","fields":{"id":-9223372036854775808,"code":"a"}}"""; """{"case":"Eval","fields":{"id":9223372036854775808,"code":"a"}}"""
    """{"case":"Eval","fields":{"id":1.5,"code":"a"}}"""; """{"case":"Eval","fields":{"id":1e3,"code":"a"}}"""; """{"case":"Eval","fields":{"id":1.0,"code":"a"}}"""
    """{"case":"Eval","fields":{"id":-0,"code":"a"}}"""; """{"case":"Eval","fields":{"id":"1","code":"a"}}"""; """{"case":"Eval","fields":{"id":true,"code":"a"}}"""
    // int and int64 limits
    """{"case":"Complete","fields":{"id":1,"text":"a","caret":2147483647}}"""; """{"case":"Complete","fields":{"id":1,"text":"a","caret":2147483648}}"""
    """{"case":"Complete","fields":{"id":1,"text":"a","caret":-2147483648}}"""; """{"case":"Complete","fields":{"id":1,"text":"a","caret":1.5}}"""
    """{"case":"Complete","fields":{"id":1,"text":"a","caret":"2"}}"""
    // a list of strings: elements, order of errors
    """{"case":"EvaluateMember","fields":{"id":1,"binding":"b","path":["x","y"]}}"""; """{"case":"EvaluateMember","fields":{"id":1,"binding":"b","path":[]}}"""
    """{"case":"EvaluateMember","fields":{"id":1,"binding":"b","path":"x"}}"""; """{"case":"EvaluateMember","fields":{"id":1,"binding":"b","path":["x",2,3]}}"""
    """{"case":"EvaluateMember","fields":{"id":1,"binding":"b","path":null}}"""; """{"case":"EvaluateMember","fields":{"id":1,"binding":"b","path":{}}}"""
    """{"case":"EvaluateMember","fields":{"id":1,"binding":5,"path":[7]}}"""
    // a list of int64
    """{"case":"AgentAwaitEntries","fields":{"id":1,"probes":[1,2,3],"bound":600000000}}"""; """{"case":"AgentAwaitEntries","fields":{"id":1,"probes":[1,"2"],"bound":600000000}}"""
    """{"case":"AgentAwaitEntries","fields":{"id":1,"probes":[],"bound":"x"}}"""; """{"case":"AgentAwaitEntries","fields":{"id":1,"probes":[],"bound":1.5}}"""
    """{"case":"AgentAwaitEntries","fields":{"id":1,"probes":[],"bound":-1}}"""; """{"case":"AgentAwaitEntries","fields":{"id":1,"probes":[],"bound":null}}"""
    // a nested union
    """{"case":"SetWalkMode","fields":{"id":1,"mode":{"case":"Safe"}}}"""; """{"case":"SetWalkMode","fields":{"id":1,"mode":{"case":"Nope"}}}"""
    """{"case":"SetWalkMode","fields":{"id":1,"mode":"Safe"}}"""; """{"case":"SetWalkMode","fields":{"id":1,"mode":{}}}"""; """{"case":"SetWalkMode","fields":{"id":1}}""" ]

let private responseInputs =
  [ "null"; "{}"; "[]"; "{nope"
    """{"case":"Ready","fields":{"runtime":"r","fsharpCore":"f"}}"""; """{"case":"Ready","fields":{"runtime":"r"}}"""; """{"case":"Ready","fields":{"runtime":1,"fsharpCore":"f"}}"""
    """{"case":"Output","fields":{"stream":{"case":"StdOut"},"text":"hi"}}"""; """{"case":"Output","fields":{"stream":{"case":"StdErr"},"text":"hi"}}"""
    """{"case":"Output","fields":{"stream":{"case":"StdAll"},"text":"hi"}}"""; """{"case":"Output","fields":{"stream":"StdOut","text":"hi"}}"""
    // a record list: elements, order of errors, empty
    """{"case":"CompletionsResult","fields":{"id":1,"items":[]}}"""
    """{"case":"CompletionsResult","fields":{"id":1,"items":[{"DisplayText":"a","ReplacementText":"b","Glyph":"c"}]}}"""
    """{"case":"CompletionsResult","fields":{"id":1,"items":[{"DisplayText":"a","ReplacementText":"b","Glyph":"c"},{"DisplayText":"a","ReplacementText":"b"}]}}"""
    """{"case":"CompletionsResult","fields":{"id":1,"items":[{"DisplayText":"a","ReplacementText":"b","Glyph":"c"},{"DisplayText":1,"ReplacementText":2}]}}"""
    """{"case":"CompletionsResult","fields":{"id":1,"items":[null]}}"""; """{"case":"CompletionsResult","fields":{"id":1,"items":[[]]}}"""
    """{"case":"CompletionsResult","fields":{"id":1,"items":{}}}"""; """{"case":"CompletionsResult","fields":{"id":1,"items":"x"}}"""
    """{"case":"CompletionsResult","fields":{"id":1,"items":[{"DisplayText":"a","ReplacementText":"b","Glyph":"c","More":1}]}}"""
    // diagnostics: a record with ints and a union
    """{"case":"CheckResult","fields":{"id":1,"diagnostics":[{"Severity":{"case":"DiagError"},"ErrorNumber":1,"Subcategory":"s","Message":"m","StartLine":1,"StartColumn":2,"EndLine":3,"EndColumn":4}]}}"""
    """{"case":"CheckResult","fields":{"id":1,"diagnostics":[{"Severity":{"case":"DiagBoom"},"ErrorNumber":1,"Subcategory":"s","Message":"m","StartLine":1,"StartColumn":2,"EndLine":3,"EndColumn":4}]}}"""
    """{"case":"CheckResult","fields":{"id":1,"diagnostics":[{"Severity":{"case":"DiagInfo"},"ErrorNumber":"1","Subcategory":"s","Message":"m","StartLine":1,"StartColumn":2,"EndLine":3,"EndColumn":4}]}}"""
    """{"case":"CheckResult","fields":{"id":1,"diagnostics":[{"Severity":{"case":"DiagInfo"},"ErrorNumber":1,"Subcategory":"s","Message":"m","StartLine":1,"StartColumn":2,"EndLine":3}]}}"""
    // a union case carrying fields, reached through another union
    """{"case":"EvalResult","fields":{"id":1,"outcome":{"case":"EvalFailed","fields":{"message":"bad"}},"diagnostics":[]}}"""
    """{"case":"EvalResult","fields":{"id":1,"outcome":{"case":"EvalFailed"},"diagnostics":[]}}"""
    """{"case":"EvalResult","fields":{"id":1,"outcome":{"case":"EvalFailed","fields":{}},"diagnostics":[]}}"""
    """{"case":"EvalResult","fields":{"id":1,"outcome":{"case":"EvalSucceeded"},"diagnostics":[]}}"""
    """{"case":"EvalResult","fields":{"id":1,"outcome":{"case":"EvalInterrupted","fields":3},"diagnostics":[]}}"""
    """{"case":"EvalResult","fields":{"id":1,"outcome":{"case":"EvalFailed","fields":{"message":"bad"}}}}"""
    // an odd nesting: a bool, a struct union, a tuple-like record, a timespan and a moment
    """{"case":"FlagResult","fields":{"id":1,"reading":{"case":"FlagWasBool","fields":{"value":true}}}}"""
    """{"case":"FlagResult","fields":{"id":1,"reading":{"case":"FlagWasBool","fields":{"value":"true"}}}}"""
    """{"case":"FlagResult","fields":{"id":1,"reading":{"case":"FlagWasBool","fields":{"value":1}}}}"""
    """{"case":"FlagResult","fields":{"id":1,"reading":{"case":"FlagWasBool","fields":{"value":null}}}}"""
    """{"case":"FlagResult","fields":{"id":1,"reading":{"case":"FlagWasUnbound"}}}"""
    """{"case":"FlagResult","fields":{"id":1,"reading":{"case":"FlagWasNotBool","fields":{"typeName":"T"}}}}"""
    """{"case":"AgentRefused","fields":{"id":1,"reason":"nope"}}"""; """{"case":"AgentRefused","fields":{"id":1,"reason":null}}"""
    """{"case":"AgentLoadedAssembliesResult","fields":{"id":1,"names":["a","b"]}}"""; """{"case":"AgentLoadedAssembliesResult","fields":{"id":1,"names":["a",null]}}"""
    """{"case":"AgentLoadedAssembliesResult","fields":{"id":1,"names":["a",{"x":1}]}}""" ]

let private errorLines () : string list =
  [ for input in requestInputs -> sprintf "request\t%s\t%s" (input.Replace("\t", "\\t")) (answer AsRequest input)
    for input in responseInputs -> sprintf "response\t%s\t%s" (input.Replace("\t", "\\t")) (answer AsResponse input) ]

[<Tests>]
let tests =
  testList "FsiProtocol wire golden" [
    testCase "the samples reach every case of every union the protocol carries" <| fun _ ->
      let sampler, _ = build ()
      let missing =
        [ for union in reachableUnions () do
            for case in FSharpType.GetUnionCases union do
              match sampler.Visited.ContainsKey(union.FullName + "." + case.Name) with
              | true -> ()
              | false -> yield union.FullName + "." + case.Name ]
      Expect.equal "every union case is sampled" [] missing

    testCase "every sample encodes to exactly the pinned wire text and reads back as itself" <| fun _ ->
      let _, samples = build ()
      let lines = samples |> List.map (fun sample -> sprintf "%s\t%s" (nameOf sample) (encodeSample sample))
      match updateRequested () with
      | true ->
        writeGolden wireFile lines
        printfn "%s=1: wrote %d wire samples to %s" UpdateVariable lines.Length wireFile
      | false ->
        let pinned = readGolden wireFile
        Expect.equal "same number of samples" pinned.Length lines.Length
        List.zip3 pinned lines samples
        |> List.iter (fun (pin, now, sample) ->
          Expect.equal (sprintf "the wire text of %s" (nameOf sample)) pin now
          let json = pin.Substring(pin.IndexOf '\t' + 1)
          readsBackAs sample json |> Expect.isTrue (sprintf "the pinned text of %s reads back as the sample" (nameOf sample)))

    testCase "every malformed or odd input gets exactly the pinned answer" <| fun _ ->
      let lines = errorLines ()
      match updateRequested () with
      | true ->
        writeGolden errorsFile lines
        printfn "%s=1: wrote %d answers to %s" UpdateVariable lines.Length errorsFile
      | false ->
        let pinned = readGolden errorsFile
        Expect.equal "same number of inputs" pinned.Length lines.Length
        List.zip pinned lines
        |> List.iter (fun (pin, now) -> Expect.equal (sprintf "the answer for %s" (pin.Substring(0, min 90 pin.Length))) pin now)
  ]
