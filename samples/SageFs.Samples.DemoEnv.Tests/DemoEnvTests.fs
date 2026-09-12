module DemoEnvTests

open Expecto
open Expecto.Flip
open SageFs.Samples.DemoEnv

[<Tests>]
let parseWindowTests =
  testList "DemoEnv.parseWindow" [
    testCase "well-formed x,y,w,h parses" <| fun _ ->
      parseWindow (Some "10,20,800,600")
      |> Expect.equal
        "well-formed input parses to the matching WindowSpec"
        (Some { X = 10; Y = 20; Width = 800; Height = 600 })

    testCase "unset (None) yields None" <| fun _ ->
      parseWindow None
      |> Expect.isNone "an unset env var must not change behavior"

    testCase "a null env var (Option.ofObj null) yields None" <| fun _ ->
      let raw : string = null
      parseWindow (Option.ofObj raw)
      |> Expect.isNone "a null env var value must not change behavior"

    testCase "empty string yields None" <| fun _ ->
      parseWindow (Some "")
      |> Expect.isNone "an empty value is malformed"

    testCase "whitespace-only string yields None" <| fun _ ->
      parseWindow (Some "   ")
      |> Expect.isNone "a whitespace-only value is malformed"

    testCase "non-numeric garbage yields None" <| fun _ ->
      parseWindow (Some "garbage")
      |> Expect.isNone "unparseable text is malformed"

    testCase "wrong arity (3 parts) yields None" <| fun _ ->
      parseWindow (Some "1,2,3")
      |> Expect.isNone "SAGEFS_DEMO_WINDOW needs exactly x,y,w,h"

    testCase "wrong arity (5 parts) yields None" <| fun _ ->
      parseWindow (Some "1,2,3,4,5")
      |> Expect.isNone "SAGEFS_DEMO_WINDOW needs exactly x,y,w,h"

    testCase "non-positive width yields None" <| fun _ ->
      parseWindow (Some "0,0,0,600")
      |> Expect.isNone "a zero/negative width can never produce a visible window"

    testCase "non-positive height yields None" <| fun _ ->
      parseWindow (Some "0,0,800,-1")
      |> Expect.isNone "a zero/negative height can never produce a visible window"
  ]

[<Tests>]
let parseSeedTests =
  testList "DemoEnv.parseSeed" [
    testCase "well-formed integer parses" <| fun _ ->
      parseSeed (Some "42")
      |> Expect.equal "well-formed input parses to the matching seed" (Some 42)

    testCase "unset (None) yields None" <| fun _ ->
      parseSeed None
      |> Expect.isNone "an unset env var must not change behavior"

    testCase "a null env var (Option.ofObj null) yields None" <| fun _ ->
      let raw : string = null
      parseSeed (Option.ofObj raw)
      |> Expect.isNone "a null env var value must not change behavior"

    testCase "non-numeric text yields None" <| fun _ ->
      parseSeed (Some "x")
      |> Expect.isNone "unparseable text is malformed"

    testCase "empty string yields None" <| fun _ ->
      parseSeed (Some "")
      |> Expect.isNone "an empty value is malformed"
  ]
