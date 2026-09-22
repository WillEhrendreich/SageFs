# 🧘 Coming from FSharpKoans?

You already know F#. You filled in the blanks, matched the patterns, piped the lists. What SageFs gives you is that same F#, minus the `dotnet watch run` cycle, the terminal-only pass/fail output, and the squinting to figure out which koan you broke this time.

You leave behind the `dotnet watch run` loop, terminal-only output, the custom `[<Koan>]` framework that only exists inside the koans repo, and the gap between "I finished the exercises" and "I built something real."

**What you'll notice right away:**
- Alt+Enter on any expression shows the result inline, not in the terminal.
- Expecto tests with live gutter markers (✓/✗) build directly on what koan assertions taught you.
- No more `dotnet run` cycles; feedback is instant.
- The skills the koans taught you (DUs, pipelines, options, pattern matching) apply directly to real code, unchanged.

**→ [Start here: `samples/from-koans/00-about-sagefs-koans.fsx`](../samples/from-koans/00-about-sagefs-koans.fsx)** (the roadmap; then work through `01-about-asserts.fsx` → `21-about-filtering.fsx` at your own pace, with `22-graduation-guide.fsx` at the end)

```fsharp
// Koans taught you this:
//     let actual_value = __
//     AssertEquality expected_value actual_value
//     dotnet run → FAIL → fix → run → PASS → next (~3 sec cycle)

// SageFs: just evaluate it.
let x = 1 + 1   // Alt+Enter → 2, right here

// Your DU skills, applied to a real domain:
type OrderStatus =
    | Pending | Shipped of tracking: string | Cancelled of reason: string

let describe = function
    | Pending    -> "⏳ Awaiting shipment"
    | Shipped t  -> $"📦 {t}"
    | Cancelled r -> $"❌ {r}"
// Alt+Enter → instant result. No test framework needed for exploration.
```
