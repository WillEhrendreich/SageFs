// ============================================================
//  🧘  F# Koans — SageFs Edition
//
//  Original koans: github.com/ChrisMarinos/FSharpKoans
//  Reimagined for SageFs: live Expecto tests in your editor.
//
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  WELCOME, BEGINNER! 🎉
//  ──────────────────────
//  You don't need to know F# to start. These 21 exercises teach
//  you one concept at a time, from "what is 1 + 1?" all the way
//  to discriminated unions and data pipelines.
//
//  HOW TO USE THESE FILES
//  ──────────────────────
//  1. Open any numbered file in VS Code (or Neovim / TUI).
//  2. Read the comments and Alt+Enter expressions to explore.
//     ↳ SageFs evaluates instantly — you'll see results inline!
//  3. Fill in the __ blanks to make the 🔴 tests go 🟢.
//  4. Save — SageFs runs your tests and shows gutter markers.
//     No terminal. No dotnet run. Just fill in the blank.
//
//  THE KOAN FORMAT
//  ───────────────
//  Each file contains:
//    • Alt+Enter-able expressions with expected result comments
//      (try it! highlight an expression and press Alt+Enter)
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
//  07-about-strings         → string operations and interpolation
//  08-about-branching       → if expressions and pattern matching
//  09-about-lists           → F# lists (the bread and butter)
//  10-about-pipelining      → the |> operator (you'll love this)
//  11-about-arrays          → .NET arrays
//  12-about-looping         → for and while loops
//  13-more-about-functions  → lambdas, currying, partial application
//  14-about-dot-net-colls   → Dictionary, List<T>, Seq
//  15-about-stock-example   → apply everything to real data 📊
//  16-about-record-types    → records (named product types)
//  17-about-option-types    → Option<'T> (no more null!)
//  18-about-disc-unions     → discriminated unions (sum types) ⭐
//  19-about-modules         → organizing code with modules
//  20-about-classes         → OOP classes in F#
//  21-about-filtering       → filter / find / choose / pick
//  22-graduation-guide      → what comes next? Real projects!
//
//  WHY SAGEFS MAKES THIS MORE FUN
//  ────────────────────────────────
//  The original FSharpKoans were great, but the workflow was:
//    1. Edit a file  →  2. dotnet run (~3 seconds)  →  3. Scroll terminal output
//
//  With SageFs:
//    1. Edit a file  →  2. Save  →  3. See ✓/✗ right in your editor (<200ms)
//
//  ┌───────────────────────┬──────────────────────────────────┐
//  │ Original Koans        │ SageFs Edition                   │
//  ├───────────────────────┼──────────────────────────────────┤
//  │ NUnit AssertEquality  │ Expecto.Flip pipelines           │
//  │ dotnet run ~3s/cycle  │ Save → gutter marker <200ms     │
//  │ One failure halts all │ All tests run, each shows ✓/✗   │
//  │ Scroll terminal output│ Gutter arrow jumps to failure    │
//  │ Custom [<Koan>] runner│ Industry-standard Expecto        │
//  │ Edit → build → run    │ Alt+Enter for instant results    │
//  └───────────────────────┴──────────────────────────────────┘
//
//  Start with 01-about-asserts.fsx. Enlightenment awaits. 🧘
// ============================================================
