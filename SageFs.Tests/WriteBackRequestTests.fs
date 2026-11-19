/// TDD for the pure front door of the edit tool. These tests are the contract:
/// a request is either actionable or refused with a typed reason, before any
/// session is touched. No MCP types, no IO, no string parsing.
module SageFs.Tests.WriteBackRequestTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.Tweak

let private request code file line =
  { WriteBackRequest.Request.Code = code
    WriteBackRequest.Request.FilePath = file
    WriteBackRequest.Request.BlockStartLine = line }

let private plan code file line = request code file line |> WriteBackRequest.plan

let private genLine = Gen.choose (-5, 20)
let private genText = Gen.elements [ ""; "  "; "let x = 1"; "let f x = x + 1" ]
let private genPath = Gen.elements [ ""; "  "; "/repo/App.fs"; "relative/App.fs" ]

[<Tests>]
let writeBackRequestTests =
  testList "WriteBackRequest — pure request decisions" [

    testCase "WHY — a good request is planned with its fields intact" <| fun _ ->
      match plan "let answer = 42" "/repo/App.fs" 12 with
      | Error r -> failtestf "expected a plan, got %A" r
      | Ok p ->
        p.Code |> Expect.equal "code" "let answer = 42"
        p.FilePath |> Expect.equal "file" "/repo/App.fs"
        p.BlockStartLine |> Expect.equal "line" 12

    testCase "WHY — each guard names itself, in the order a caller fixes them" <| fun _ ->
      plan "" "" 0 |> Expect.equal "empty code first" (Error WriteBackRequest.Refusal.EmptyCode)
      plan "let x = 1" "" 0 |> Expect.equal "then the file" (Error WriteBackRequest.Refusal.MissingFilePath)
      plan "let x = 1" "/repo/App.fs" 0
      |> Expect.equal "then the line" (Error(WriteBackRequest.Refusal.InvalidBlockStartLine 0))

    testProperty "PROPERTY: plan succeeds exactly when every guard passes" <|
      Prop.forAll (Arb.fromGen (gen { let! code = genText
                                      let! file = genPath
                                      let! line = genLine
                                      return code, file, line })) (fun (code, file, line) ->
        let actionable =
          not (String.IsNullOrWhiteSpace code)
          && not (String.IsNullOrWhiteSpace file)
          && line >= 1
        match plan code file line with
        | Ok _ -> actionable
        | Error _ -> not actionable)

    testProperty "PROPERTY: a refusal always says what was missing and what to do" <|
      Prop.forAll (Arb.fromGen (gen {
        let! code = genText
        let! file = genPath
        let! line = genLine
        return code, file, line })) (fun (code, file, line) ->
        match plan code file line with
        | Ok _ -> true
        | Error refusal ->
          let text = WriteBackRequest.Refusal.describe refusal
          let next = WriteBackRequest.Refusal.suggestedAction refusal
          text.Contains "Nothing was evaluated"
          && text.StartsWith("write_back:")
          && not (String.IsNullOrWhiteSpace next))

    testCase "WHY — a null code argument is refused, not thrown" <| fun _ ->
      plan null "/repo/App.fs" 1
      |> Expect.equal "null is an empty submission" (Error WriteBackRequest.Refusal.EmptyCode)
  ]
