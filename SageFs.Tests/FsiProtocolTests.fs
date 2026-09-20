module SageFs.Tests.FsiProtocolTests

open Expecto
open FsCheck
open FsCheck.FSharp
open SageFs.FsiHost.FsiProtocol

/// Adversarial text: empty, newlines (the framing delimiter!), quotes, backslashes, control chars, unicode, random.
let private genText =
  Gen.oneof [
    Gen.constant ""
    Gen.constant "line1\nline2\r\nline3"
    Gen.constant "quote \" backslash \\ slash /"
    Gen.constant "\u0000\u0001\u001f control"
    Gen.constant "日本語 🚀 émoji"
    Gen.constant "{\"t\":\"shutdown\"}"
    (Gen.elements [ 'a' .. 'z' ] |> Gen.listOfLength 12 |> Gen.map (fun cs -> System.String(List.toArray cs)))
  ]

let private genSeverity = Gen.elements [ DiagHidden; DiagInfo; DiagWarning; DiagError ]

let private genDiagnostic =
  gen {
    let! severity = genSeverity
    let! number = Gen.choose (0, 4000)
    let! message = genText
    let! startLine = Gen.choose (0, 500)
    let! startColumn = Gen.choose (0, 200)
    let! endLine = Gen.choose (0, 500)
    let! endColumn = Gen.choose (0, 200)
    return
      { Severity = severity
        ErrorNumber = number
        Message = message
        StartLine = startLine
        StartColumn = startColumn
        EndLine = endLine
        EndColumn = endColumn }
  }

let private genDiagnosticsAndText =
  gen {
    let! text = genText
    let! diagnostics = Gen.listOf genDiagnostic
    return text, diagnostics
  }

let private config = { FsCheckConfig.defaultConfig with maxTest = 300 }

/// Every union case name of a protocol type, straight from the type — a new case shows up here automatically.
let private caseNames (t: System.Type) =
  Microsoft.FSharp.Reflection.FSharpType.GetUnionCases t |> Array.map (fun case -> case.Name) |> Set.ofArray

let private caseNameOf (value: obj) =
  let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(value, value.GetType())
  case.Name

[<Tests>]
let tests =
  testList "FsiProtocol" [
    testCase "checkSupported: every type the protocol mentions is representable" <| fun _ ->
      Expect.isOk (checkSupported ()) "supported"

    // Generators derived from the TYPES by FsCheck: a new case or field is covered without touching this file.
    testPropertyWithConfig config "any request (generated from the type) round-trips"
    <| fun (request: Request) -> decodeRequest (encodeRequest request) = Result.Ok request

    testPropertyWithConfig config "any response (generated from the type) round-trips"
    <| fun (response: Response) -> decodeResponse (encodeResponse response) = Result.Ok response

    // Adversarial text through the same codec (newlines are the framing delimiter).
    testPropertyWithConfig config "adversarial text round-trips through every text-carrying message"
    <| Prop.forAll (Arb.fromGen genDiagnosticsAndText) (fun (text, diagnostics) ->
      let messages =
        [ EvalResult(7L, EvalFailed text, diagnostics)
          Output(StdOut, text)
          Ready(text, text) ]
      messages |> List.forall (fun message -> decodeResponse (encodeResponse message) = Result.Ok message)
      && decodeRequest (encodeRequest (Eval(1L, text))) = Result.Ok(Eval(1L, text)))

    testPropertyWithConfig config "an encoded message is one line: it never contains a raw newline"
    <| Prop.forAll (Arb.fromGen genDiagnosticsAndText) (fun (text, diagnostics) ->
      let line = encodeResponse (EvalResult(1L, EvalFailed text, diagnostics))
      not (line.Contains '\n') && not (line.Contains '\r'))

    // Drift guards: the derived generators must actually exercise EVERY case of every protocol union.
    testCase "the generators reach every case of Request and Response" <| fun _ ->
      let sample (generator: Gen<'a>) =
        Gen.sample 3000 generator |> Array.map (fun value -> caseNameOf (box value)) |> Set.ofArray
      Expect.equal (sample (ArbMap.defaults |> ArbMap.generate<Request>)) (caseNames typeof<Request>) "every request case"
      Expect.equal (sample (ArbMap.defaults |> ArbMap.generate<Response>)) (caseNames typeof<Response>) "every response case"

    testPropertyWithConfig config "decoding arbitrary text never throws, it returns Ok or Error"
    <| Prop.forAll (Arb.fromGen genText) (fun text ->
      match decodeResponse text, decodeRequest text with
      | Result.Ok _, _
      | Result.Error _, _ -> true)

    testCase "valid JSON of the wrong shape is an Error, never an exception" <| fun _ ->
      [ "null"; "123"; "\"str\""; "[1,2,3]"; "{}"; """{"case":"Eval","fields":{"id":"x","code":1}}"""
        """{"case":"Eval","fields":{"id":1}}"""; """{"case":42}""" ]
      |> List.iter (fun json ->
        match decodeRequest json with
        | Result.Error _ -> ()
        | Result.Ok request -> failtestf "%s decoded as %A" json request)

    testCase "an unknown case is an Error naming it" <| fun _ ->
      match decodeRequest """{"case":"dance"}""" with
      | Result.Error message -> Expect.stringContains message "dance" "names the case"
      | Result.Ok _ -> failtest "expected an error"

    testCase "a missing field is an Error naming it" <| fun _ ->
      match decodeRequest """{"case":"Eval","fields":{"id":1}}""" with
      | Result.Error message -> Expect.stringContains message "code" "names the field"
      | Result.Ok _ -> failtest "expected an error"
  ]
