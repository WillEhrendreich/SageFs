module SageFs.Tests.ExplorationTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools

module Integration = SageFs.Tests.TestInfrastructure.Integration

[<Tests>]
let tests =
  testSequenced <| Integration.hostList "Package/Namespace Explorer" [

    testList "exploreNamespace" [
      testCase "lists types in System.IO namespace"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreNamespace ctx "test" "System.IO" None |> fun t -> t.Result
        result |> Expect.stringContains "Should contain File type" "File"
        result |> Expect.stringContains "Should contain Directory type" "Directory"
        result |> Expect.stringContains "Should contain Stream type" "Stream"

      testCase "lists types in System.Collections.Generic namespace"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreNamespace ctx "test" "System.Collections.Generic" None |> fun t -> t.Result
        result |> Expect.stringContains "Should contain List type" "List"
        result |> Expect.stringContains "Should contain Dictionary type" "Dictionary"

      testCase "returns helpful message for unknown namespace"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreNamespace ctx "test" "NonExistent.Namespace.Here" None |> fun t -> t.Result
        result |> Expect.stringContains "Should indicate nothing found" "No members found"
    ]

    testList "exploreType" [
      testCase "lists members of System.IO.File"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreType ctx "test" "System.IO.File" None |> fun t -> t.Result
        result |> Expect.stringContains "Should contain ReadAllText method" "ReadAllText"
        result |> Expect.stringContains "Should contain Exists method" "Exists"
        result |> Expect.stringContains "Should contain Delete method" "Delete"

      testCase "lists members of System.String"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreType ctx "test" "System.String" None |> fun t -> t.Result
        result |> Expect.stringContains "Should contain static Concat method" "Concat"
        result |> Expect.stringContains "Should contain static IsNullOrEmpty method" "IsNullOrEmpty"

      testCase "returns helpful message for unknown type"
      <| fun _ ->
        let ctx = TestInfrastructure.sharedCtx ()
        let result = exploreType ctx "test" "NonExistent.Type.Here" None |> fun t -> t.Result
        result |> Expect.stringContains "Should indicate nothing found" "No members found"
    ]
  ]
