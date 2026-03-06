// ============================================================
//  🧘  About the Order of Evaluation — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutTheOrderOfEvaluation.fs
//
//  Sometimes you need to be explicit about evaluation order.
//  Parentheses and the <| operator are your tools.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Parentheses control evaluation order ─────────────────────

let add x y = x + y

// Alt+Enter these to explore:
add (add 5 8) (add 1 1)   // → 15  (inner adds first, then outer)
// add  add 5 8   add 1 1   ← this won't compile — try it!

// The <| backward pipe operator: f <| x  means  f x
// It lets you avoid parentheses around complex arguments.

let double x = x * 2

double <| add 5 8    // → 26  (add 5 8 = 13, then double 13)

// Compare:
double (add 5 8)     // → 26  (same, using parens)

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about the order of evaluation" [

  test "SometimesYouNeedParenthesisToGroupThings" {
    let result = add (add 5 8) (add 1 1)
    Expect.equal result __ "nested adds: (5+8) + (1+1)"
  }

  test "BackwardPipeOperatorHelpsWithGrouping" {
    let result = double <| add 5 8
    Expect.equal result __ "double the result of add 5 8"
  }

  test "ParensAndBackwardPipeAreEquivalent" {
    let withParens  = double (add 3 4)
    let withBwdPipe = double <| add 3 4
    Expect.equal withParens withBwdPipe "both should give same result"
    Expect.equal withParens __ "double of (3+4)"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `add (add 5 8) (add 1 1)` — see 15
// 2. Try `double <| add 5 8` — see 26
// 3. Try removing the parens in the first test — watch the error
// 4. The forward pipe |> sends LEFT to RIGHT: x |> f = f x
//    The backward pipe <| sends RIGHT to LEFT: f <| x = f x
// ============================================================
