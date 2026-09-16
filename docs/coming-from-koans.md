# 🧘 Coming from FSharpKoans?

You've already proven you know F# — you filled in the blanks, matched the patterns, piped the lists. But the Koans workflow was slow: `dotnet watch run`, squint at terminal output, scroll to find which koan broke, fix it, wait 3 seconds, repeat. SageFs gives you the same F# you learned, with instant inline feedback, live test gutter markers, and no more squinting at the terminal.

You'll leave behind the 2-4 second `dotnet watch run` cycle, terminal-only pass/fail output, the custom `[<Koan>]` framework that doesn't work anywhere else, and the gap between finishing the exercises and building real things.

**What you'll notice right away:**
- Alt+Enter on any expression shows the result inline, not in the terminal
- Expecto tests with live gutter markers (✓/✗) build on what koan assertions taught you
- No more `dotnet run` cycles; feedback arrives in about 200ms
- The skills you learned in koans (DUs, pipelines, options, pattern matching) apply directly to real code

**→ [Start here: `samples/from-koans/00-about-sagefs-koans.fsx`](../samples/from-koans/00-about-sagefs-koans.fsx)** (the roadmap; then work through `01-about-asserts.fsx` → `21-about-filtering.fsx` at your own pace)

```fsharp
// Koans taught you this:
//     let actual_value = __
//     AssertEquality expected_value actual_value
//     dotnet run → FAIL → fix → run → PASS → next (~3 sec cycle)

// SageFs: just evaluate it.
let x = 1 + 1   // Alt+Enter → 2, right here, in ~200ms

// Your DU skills, applied to a real domain:
type OrderStatus =
    | Pending | Shipped of tracking: string | Cancelled of reason: string

let describe = function
    | Pending    -> "⏳ Awaiting shipment"
    | Shipped t  -> $"📦 {t}"
    | Cancelled r -> $"❌ {r}"
// Alt+Enter → instant result. No test framework needed for exploration.
```
