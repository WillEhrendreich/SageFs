// ============================================================
//  🧘  F# Koans — SageFs Edition
//
//  Original koans: github.com/ChrisMarinos/FSharpKoans
//  Reimagined for SageFs: live Expecto tests in your editor.
//
//  HOW TO USE THESE FILES
//  ──────────────────────
//  1. Open any numbered file in VS Code (or Neovim / TUI).
//  2. Read the comments and Alt+Enter expressions to explore.
//  3. Fill in the __ blanks to make the 🔴 tests go 🟢.
//  4. Save — SageFs runs your tests and shows gutter markers.
//     No terminal. No dotnet run. Just fill in the blank.
//
//  THE KOAN FORMAT
//  ───────────────
//  Each file contains:
//    • Alt+Enter-able expressions with expected result comments
//    • Expecto tests that FAIL until you fill in the __
//    • Explanations of the concept being taught
//
//  __ is a typed placeholder that throws if evaluated:
//      let inline __<'T> : 'T = failwith "Fill me in"
//  Replace __ with the correct value to make the test pass.
//
//  THE JOURNEY
//  ───────────
//  01-about-asserts         → Meet Expecto (SageFs's test runner)
//  02-about-let             → let bindings and type inference
//  03-about-functions       → defining and calling functions
//  04-about-unit            → the unit type (F#'s void)
//  05-about-order-of-eval   → parentheses and <| operator
//  06-about-tuples          → grouping values with tuples
//  07-about-strings         → string operations
//  08-about-branching       → if expressions and pattern matching
//  09-about-lists           → F# lists
//  10-about-pipelining      → the |> operator
//  11-about-arrays          → .NET arrays
//  12-about-looping         → for and while loops
//  13-more-about-functions  → lambdas, currying, partial application
//  14-about-dot-net-colls   → Dictionary, List<T>, Seq
//  15-about-stock-example   → apply everything to real data
//  16-about-record-types    → records (named product types)
//  17-about-option-types    → Option<'T> (no more null)
//  18-about-disc-unions     → discriminated unions (sum types)
//  19-about-modules         → organizing code with modules
//  20-about-classes         → OOP classes in F#
//  21-about-filtering       → filter / find / choose / pick
//
//  WHAT'S DIFFERENT FROM THE ORIGINAL KOANS?
//  ──────────────────────────────────────────
//  Original koans:           SageFs edition:
//  ────────────────          ───────────────
//  NUnit AssertEquality      Expecto Expect.equal
//  dotnet run ~3s/cycle      Save → gutter marker <200ms
//  One failing test halts    All tests run, each shows ✓/✗
//  Terminal scroll to find   Gutter arrow jumps to failure
//  Custom [<Koan>] runner    Industry-standard Expecto
//
//  Start with 01-about-asserts.fsx. Enlightenment awaits.
// ============================================================
