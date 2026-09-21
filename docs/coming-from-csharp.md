# 🔷 Coming from C#?

Same .NET runtime, same NuGet packages, same `dotnet` CLI. I didn't build SageFs to get you off .NET — I built it because a 3-line idea kept costing me a `public class AbstractRepositoryFactoryImpl` to express.

What you get to leave behind: 50-line classes for something that's one record, null reference exceptions, and `dotnet watch` taking ten seconds to notice you fixed the typo.

**What you'll notice right away:**
- Records are immutable value objects with structural equality, in one line
- Discriminated unions give you `sealed class + pattern matching` without the ceremony
- `Result<'T, 'TError>` replaces `try/catch` for the failures you actually expect
- SageFs hot reload uses Harmony to patch running code, so a lot of edits apply without a full rebuild or restart — the edits it can't patch, it tells you so, instead of pretending it worked

**→ [Start here: `samples/from-csharp/hello.fsx`](../samples/from-csharp/hello.fsx)**

```fsharp
// C#: public record Person(string Name, int Age);  // 1 line in modern C#
// F#: one line too, but with structural equality, hashCode, and copy-with:
type Person = { Name: string; Age: int }

let alice = { Name = "Alice"; Age = 30 }
let older  = { alice with Age = 31 }   // alice is unchanged — immutability is the default
```
