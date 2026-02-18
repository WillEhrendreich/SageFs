module SageFs.Tests.ExplorationTests

open Expecto
open SageFs
open SageFs.McpTools

[<Tests>]
let tests =
  testSequenced <| testList "Package/Namespace Explorer" [

    testList "exploreNamespace" [
      testCase "lists types in System.IO namespace"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreNamespace ctx "test" "System.IO" |> fun t -> t.Result
        Expect.stringContains result "File" "Should contain File type"
        Expect.stringContains result "Directory" "Should contain Directory type"
        Expect.stringContains result "Stream" "Should contain Stream type"

      testCase "lists types in System.Collections.Generic namespace"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreNamespace ctx "test" "System.Collections.Generic" |> fun t -> t.Result
        Expect.stringContains result "List" "Should contain List type"
        Expect.stringContains result "Dictionary" "Should contain Dictionary type"

      testCase "returns helpful message for unknown namespace"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreNamespace ctx "test" "NonExistent.Namespace.Here" |> fun t -> t.Result
        Expect.stringContains result "No items found" "Should indicate nothing found"
    ]

    testList "exploreType" [
      testCase "lists members of System.IO.File"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreType ctx "test" "System.IO.File" |> fun t -> t.Result
        Expect.stringContains result "ReadAllText" "Should contain ReadAllText method"
        Expect.stringContains result "Exists" "Should contain Exists method"
        Expect.stringContains result "Delete" "Should contain Delete method"

      testCase "lists members of System.String"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreType ctx "test" "System.String" |> fun t -> t.Result
        Expect.stringContains result "Concat" "Should contain static Concat method"
        Expect.stringContains result "IsNullOrEmpty" "Should contain static IsNullOrEmpty method"

      testCase "returns helpful message for unknown type"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreType ctx "test" "NonExistent.Type.Here" |> fun t -> t.Result
        Expect.stringContains result "No items found" "Should indicate nothing found"
    ]
  ]
