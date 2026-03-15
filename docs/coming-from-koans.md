# 🧘 Coming from FSharpKoans?

Congratulations, graduate — you've already proven you know F#. You filled in the blanks, matched the patterns, piped the lists. But the Koans workflow was also kind of... slow? `dotnet watch run`, squint at terminal output, scroll to find which koan broke, fix it, wait 3 seconds, repeat. SageFs is the upgrade: same F# you learned, but with instant inline feedback, live test gutter markers, and no more terminal-squinting.

Pain you're leaving behind: The 2-4 second `dotnet watch run` cycle, terminal-only pass/fail output, the custom `[<Koan>]` framework that doesn't work anywhere else, and the gap between "I finished the exercises" and "I can build real things."

**What you'll love immediately:**
- Alt+Enter on any expression → result appears inline, not in the terminal
- Expecto tests with live gutter markers (✓/✗) — the natural evolution of koan assertions
- No more `dotnet run` cycles — feedback in ~200ms
- Your koan-learned skills (DUs, pipelines, options, pattern matching) applied to real domains

**→ [Start here: `samples/from-koans/00-about-sagefs-koans.fsx`](../samples/from-koans/00-about-sagefs-koans.fsx)** (the roadmap — then work through `01-about-asserts.fsx` → `21-about-filtering.fsx` at your own pace)

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
