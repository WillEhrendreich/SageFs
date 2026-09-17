# 🧘 Coming from FSharpKoans?

You already know F# — you filled in the blanks, matched the patterns, piped the lists. SageFs gives you that same F# with instant inline feedback and live test markers, instead of the koans loop of `dotnet watch run`, terminal output, and squinting to find which koan broke.

You leave behind the `dotnet watch run` cycle, terminal-only pass/fail output, the custom `[<Koan>]` framework that works nowhere else, and the gap between finishing the exercises and building real things.

**What you'll notice right away:**
- Alt+Enter on any expression shows the result inline, not in the terminal.
- Expecto tests with live gutter markers (✓/✗) build on what koan assertions taught you.
- No more `dotnet run` cycles; inline feedback is instant.
- The skills from koans (DUs, pipelines, options, pattern matching) apply directly to real code.

**→ [Start here: `samples/from-koans/00-about-sagefs-koans.fsx`](../samples/from-koans/00-about-sagefs-koans.fsx)** (the roadmap; then work through `01-about-asserts.fsx` → `21-about-filtering.fsx` at your own pace)

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
