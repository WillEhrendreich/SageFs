module SageFs.Tests.ElmLoopMsgLabelTests

open Expecto
open Expecto.Flip
open SageFs

// Test DU types for msgLabel
type SimpleMsg =
  | Alpha
  | Beta of int
  | Gamma of string * int

type InnerEvent =
  | Started
  | Completed of int

type NestedMsg =
  | Direct
  | Wrapped of InnerEvent

[<Tests>]
let msgLabelTests = testList "ElmLoop.msgLabel" [

  testCase "simple case with no fields" <| fun _ ->
    ElmLoop.msgLabel (Alpha :> obj)
    |> Expect.equal "should be case name" "Alpha"

  testCase "simple case with fields uses case name only" <| fun _ ->
    ElmLoop.msgLabel (Beta 42 :> obj)
    |> Expect.equal "should be case name" "Beta"

  testCase "multi-field case uses case name only" <| fun _ ->
    ElmLoop.msgLabel (Gamma("hi", 1) :> obj)
    |> Expect.equal "should be case name" "Gamma"

  testCase "nested DU unwraps one level" <| fun _ ->
    ElmLoop.msgLabel (Wrapped(Started) :> obj)
    |> Expect.equal "should be Outer.Inner" "Wrapped.Started"

  testCase "nested DU with fields unwraps one level" <| fun _ ->
    ElmLoop.msgLabel (Wrapped(Completed 99) :> obj)
    |> Expect.equal "should be Outer.Inner" "Wrapped.Completed"

  testCase "direct case (not nested) returns case name" <| fun _ ->
    ElmLoop.msgLabel (Direct :> obj)
    |> Expect.equal "should be case name" "Direct"

  testCase "non-DU returns type name" <| fun _ ->
    ElmLoop.msgLabel (42 :> obj)
    |> Expect.equal "should be type name" "Int32"

  testCase "same value returns same string reference (cached)" <| fun _ ->
    let msg = Beta 1 :> obj
    let label1 = ElmLoop.msgLabel msg
    let label2 = ElmLoop.msgLabel msg
    obj.ReferenceEquals(label1, label2)
    |> Expect.isTrue "should be same string reference from cache"

  testCase "different values of same case return same string reference" <| fun _ ->
    let label1 = ElmLoop.msgLabel (Beta 1 :> obj)
    let label2 = ElmLoop.msgLabel (Beta 999 :> obj)
    obj.ReferenceEquals(label1, label2)
    |> Expect.isTrue "same case = same cached string"

  testCase "different nested inner cases return different labels" <| fun _ ->
    let label1 = ElmLoop.msgLabel (Wrapped(Started) :> obj)
    let label2 = ElmLoop.msgLabel (Wrapped(Completed 1) :> obj)
    label1 |> Expect.notEqual "different inner cases" label2
]
