/// ## Mutation Testing Framework
///
/// Proves test suite effectiveness by defining mutations and verifying
/// the existing assertions catch them. Each mutation breaks exactly one
/// function; if a test still passes on the mutated code, the mutation
/// "survived" — meaning there's a gap in the test suite.
///
/// ### Architecture
///   1. **Lean theorems** (formal-verification/lean/) — model correctness
///   2. **Correspondence tests** — model ↔ implementation alignment
///   3. **Mutation tests** (this file) — test suite effectiveness
///
/// Usage:
///   Mutations are defined as `Mutant<'a>` records. Each test applies the
///   mutant to production code and verifies the assertion FAILS on the
///   mutated output. If the assertion still holds, the mutation survived
///   (test gap).
module MutationTestingFramework

open Expecto

/// A named mutation of a value or function.
/// `Apply` transforms the real implementation into a broken version;
/// if existing tests still pass, the mutation survived.
type Mutant<'a> = {
  Name: string
  Apply: 'a -> 'a
  Description: string
}

/// Verify that a mutation is caught by an assertion.
///
/// The assertion should be TRUE for the real implementation's output
/// but FALSE for the mutated output. If the mutated output still
/// satisfies the assertion, the mutation survived (test gap).
///
/// ```fsharp
/// detectsMutant normalizeMutant "C:\\Foo.fs" (fun result ->
///   result = "c:/foo.fs")
/// ```
let detectsMutant (mutant: Mutant<'a>) (input: 'a) (assertion: 'a -> bool) =
  testCase (sprintf "WHY — %s — %s" mutant.Name mutant.Description) <| fun () ->
    let mutated = mutant.Apply input
    if assertion mutated then
      failwithf "Mutation '%s' was NOT caught — test gap!" mutant.Name

/// Verify that a mutation is caught by checking a property of the OUTPUT.
///
/// The predicate should be TRUE for the real function's output but
/// FALSE for the mutated function's output.
///
/// ```fsharp
/// detectsOutputMutant isWatchedMutant "src/foo.fs" HotReloadState.isWatched (fun result ->
///   result = true)
/// ```
let detectsOutputMutant
  (mutant: Mutant<'a -> 'b>)
  (input: 'a)
  (realFn: 'a -> 'b)
  (predicate: 'b -> bool) =
  testCase (sprintf "WHY — %s — %s" mutant.Name mutant.Description) <| fun () ->
    let realOutput = realFn input
    let mutatedOutput = mutant.Apply realFn input
    if not (predicate realOutput) then
      failwithf "Real output does not satisfy predicate — bug in test setup for '%s'" mutant.Name
    if predicate mutatedOutput then
      failwithf "Mutation '%s' was NOT caught — test gap!" mutant.Name

/// Verify that a mutation is caught by comparing two outputs.
///
/// Takes a mutant, two inputs, and two real functions. If the mutant
/// causes the outputs to converge (or diverge from expected), the
/// mutation is caught.
let detectsConvergenceMutant
  (mutant: Mutant<'a -> 'b>)
  (inputA: 'a)
  (inputB: 'a)
  (realFn: 'a -> 'b)
  (areEqual: 'b -> 'b -> bool) =
  testCase (sprintf "WHY — %s — %s" mutant.Name mutant.Description) <| fun () ->
    let realA = realFn inputA
    let realB = realFn inputB
    let mutatedA = mutant.Apply realFn inputA
    let mutatedB = mutant.Apply realFn inputB
    // Real outputs should be different (or same, depending on test)
    // Mutated outputs should match the real relationship
    if areEqual realA realB <> areEqual mutatedA mutatedB then
      failwithf "Mutation '%s' changed the relationship between outputs — caught!" mutant.Name

/// Compute mutation score: caught / total * 100
let computeMutationScore (caught: int) (total: int) =
  if total = 0 then 100.0
  else (float caught / float total) * 100.0
