module DemoEnvTests

open Expecto
open Expecto.Flip
open SageFs.Samples.DemoEnv

[<Tests>]
let parseSeedTests =
  testList "DemoEnv.parseSeed" [
    testCase "unset (None) yields None" <| fun _ ->
      parseSeed None
      |> Expect.isNone "an unset env var must not change behavior"

    testCase "garbage yields None" <| fun _ ->
      parseSeed (Some "garbage")
      |> Expect.isNone "a non-integer value is malformed"

    testCase "a positive integer yields Some" <| fun _ ->
      parseSeed (Some "7")
      |> Expect.equal "positive seeds are kept" (Some 7)

    testCase "zero yields Some 0" <| fun _ ->
      parseSeed (Some "0")
      |> Expect.equal "zero is a valid seed" (Some 0)

    testCase "a negative integer yields None" <| fun _ ->
      parseSeed (Some "-1")
      |> Expect.isNone "negative seeds are refused"
  ]

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

    testCase "wrong arity (3 parts) yields None" <| fun _ ->
      parseWindow (Some "1,2,3")
      |> Expect.isNone "SAGEFS_DEMO_WINDOW needs exactly x,y,w,h"

    testCase "non-positive width yields None" <| fun _ ->
      parseWindow (Some "0,0,0,600")
      |> Expect.isNone "a zero width is malformed"
  ]
