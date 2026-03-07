module SageFs.Tests.DomainModelVizTests

open SageFs.Features.DomainModelViz
open Expecto
open Expecto.Flip

// Test DU types for extraction
type OrderState =
  | Pending
  | Confirmed of orderId: string
  | Shipped of trackingNumber: string * carrier: string
  | Delivered
  | Cancelled of reason: string

type TrafficLight = Red | Yellow | Green

let duExtractorTests =
  testList "DUExtractor" [
    testCase "extractCases returns empty for non-union type" (fun () ->
      let cases = DUExtractor.extractCases typeof<string>
      cases |> Expect.isEmpty "non-union type should yield no cases"
    )

    testCase "extractCases returns all cases for simple DU" (fun () ->
      let cases = DUExtractor.extractCases typeof<TrafficLight>
      cases |> Expect.hasLength "should have 3 cases" 3
      cases |> List.map (fun c -> c.Name) |> Expect.containsAll "should have Red, Yellow, Green" ["Red"; "Yellow"; "Green"]
    )

    testCase "extractCases returns field info for cases with data" (fun () ->
      let cases = DUExtractor.extractCases typeof<OrderState>
      cases |> Expect.hasLength "should have 5 cases" 5
      let shipped = cases |> List.find (fun c -> c.Name = "Shipped")
      shipped.Fields |> Expect.hasLength "Shipped has 2 fields" 2
      shipped.Fields |> List.map fst |> Expect.containsAll "field names" ["trackingNumber"; "carrier"]
    )

    testCase "extractCases returns empty fields for fieldless cases" (fun () ->
      let cases = DUExtractor.extractCases typeof<OrderState>
      let pending = cases |> List.find (fun c -> c.Name = "Pending")
      pending.Fields |> Expect.isEmpty "Pending has no fields"
    )

    testCase "formatTypeName handles primitives" (fun () ->
      DUExtractor.formatTypeName typeof<string> |> Expect.equal "string" "string"
      DUExtractor.formatTypeName typeof<int> |> Expect.equal "int" "int"
      DUExtractor.formatTypeName typeof<bool> |> Expect.equal "bool" "bool"
    )

    testCase "formatTypeName handles generic types" (fun () ->
      let name = DUExtractor.formatTypeName typeof<Result<string, int>>
      name |> Expect.stringContains "should contain FSharpResult or Result" "Result"
    )
  ]

let transitionTests =
  testList "TransitionExtraction" [
    testCase "extractTransitions finds direct A->B" (fun () ->
      let caseNames = Set.ofList ["Pending"; "Confirmed"; "Shipped"]
      let transitions = DUExtractor.extractTransitionsFromSignature caseNames "confirm" "Pending" "Confirmed"
      transitions |> Expect.hasLength "should find 1 transition" 1
      let t = transitions.[0]
      t.FromState |> Expect.equal "from Pending" "Pending"
      t.ToState |> Expect.equal "to Confirmed" "Confirmed"
      t.FunctionName |> Expect.equal "function name" "confirm"
      t.IsErrorBranch |> Expect.isFalse "not error branch"
    )

    testCase "extractTransitions finds Result<Ok, Err> branches" (fun () ->
      let caseNames = Set.ofList ["Pending"; "Confirmed"; "Cancelled"]
      let transitions = DUExtractor.extractTransitionsFromSignature caseNames "tryConfirm" "Pending" "Result<Confirmed, Cancelled>"
      transitions |> Expect.hasLength "should find 2 transitions" 2
      let ok = transitions |> List.find (fun t -> t.IsErrorBranch |> not)
      let err = transitions |> List.find (fun t -> t.IsErrorBranch)
      ok.ToState |> Expect.equal "ok goes to Confirmed" "Confirmed"
      err.ToState |> Expect.equal "err goes to Cancelled" "Cancelled"
    )

    testCase "extractTransitions returns empty for non-DU types" (fun () ->
      let caseNames = Set.ofList ["Pending"; "Confirmed"]
      let transitions = DUExtractor.extractTransitionsFromSignature caseNames "doSomething" "string" "int"
      transitions |> Expect.isEmpty "no transitions for non-DU types"
    )

    testCase "extractTransitions handles qualified type names" (fun () ->
      let caseNames = Set.ofList ["Pending"; "Confirmed"]
      let transitions = DUExtractor.extractTransitionsFromSignature caseNames "confirm" "MyModule.Pending" "MyModule.Confirmed"
      transitions |> Expect.hasLength "should normalize and find" 1
    )
  ]

let rendererTests =
  testList "StateMachineRenderer" [
    testCase "render produces non-empty output for model with cases" (fun () ->
      let model = {
        TypeName = "OrderState"
        Cases = [
          { Name = "Pending"; Fields = [] }
          { Name = "Confirmed"; Fields = [("orderId", "string")] }
          { Name = "Delivered"; Fields = [] }
        ]
        Transitions = [
          { FromState = "Pending"; ToState = "Confirmed"; FunctionName = "confirm"; IsErrorBranch = false }
          { FromState = "Confirmed"; ToState = "Delivered"; FunctionName = "deliver"; IsErrorBranch = false }
        ]
      }
      let diagram = StateMachineRenderer.render model
      diagram.Length |> fun len -> (len, 0) |> Expect.isGreaterThan "diagram should be non-empty"
      diagram |> Expect.stringContains "should contain Pending" "Pending"
      diagram |> Expect.stringContains "should contain Confirmed" "Confirmed"
      diagram |> Expect.stringContains "should contain Delivered" "Delivered"
    )

    testCase "render handles empty model" (fun () ->
      let model = StateMachineModel.empty "Empty"
      let diagram = StateMachineRenderer.render model
      diagram |> Expect.equal "empty model produces empty diagram" ""
    )

    testCase "renderAsData produces correct structure" (fun () ->
      let model = {
        TypeName = "Light"
        Cases = [
          { Name = "Red"; Fields = [] }
          { Name = "Green"; Fields = [] }
        ]
        Transitions = [
          { FromState = "Red"; ToState = "Green"; FunctionName = "go"; IsErrorBranch = false }
        ]
      }
      let data = StateMachineRenderer.renderAsData model
      data.TypeName |> Expect.equal "type name" "Light"
      data.States |> Array.length |> Expect.equal "2 states" 2
      data.Transitions |> Array.length |> Expect.equal "1 transition" 1
      let red = data.States |> Array.find (fun s -> s.Name = "Red")
      red.IsEntry |> Expect.isTrue "Red is entry (no inbound)"
      let green = data.States |> Array.find (fun s -> s.Name = "Green")
      green.IsTerminal |> Expect.isTrue "Green is terminal (no outbound)"
    )

    testCase "render includes box-drawing characters" (fun () ->
      let model = {
        TypeName = "Test"
        Cases = [{ Name = "A"; Fields = [] }; { Name = "B"; Fields = [] }]
        Transitions = [{ FromState = "A"; ToState = "B"; FunctionName = "go"; IsErrorBranch = false }]
      }
      let diagram = StateMachineRenderer.render model
      diagram |> Expect.stringContains "has top-left corner" "┌"
      diagram |> Expect.stringContains "has bottom-right corner" "┘"
      diagram |> Expect.stringContains "has arrow" "▼"
    )

    testCase "buildModel composes extraction and transitions" (fun () ->
      let model = DUExtractor.buildModel typeof<TrafficLight> [("go", "Red", "Green"); ("stop", "Green", "Red")]
      model.Cases |> Expect.hasLength "3 cases" 3
      model.Transitions |> Expect.hasLength "2 transitions" 2
    )
  ]

[<Tests>]
let domainModelVizTests =
  testList "DomainModelViz" [
    duExtractorTests
    transitionTests
    rendererTests
  ]
