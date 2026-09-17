# 🔷 Coming from C#?

Same .NET runtime, same NuGet packages, same `dotnet` CLI. What changes is the code you write: a 3-line concept is 3 lines, not a `public class AbstractRepositoryFactoryImpl`.

No more 50-line classes for small concepts, no more null reference exceptions, no more `dotnet watch` taking ten seconds to rebuild after a typo fix.

**What you'll notice right away:**
- Records are immutable value objects with structural equality, in one line
- Discriminated unions give you `sealed class + pattern matching` without the ceremony
- `Result<'T, 'TError>` replaces `try/catch` for expected failure paths
- SageFs hot reload uses Harmony to patch running code, so many edits apply without a full rebuild or restart

**→ [Start here: `samples/from-csharp/hello.fsx`](../samples/from-csharp/hello.fsx)**

```fsharp
// C#: public record Person(string Name, int Age);  // 1 line in modern C#
// F#: one line too, but with structural equality, hashCode, and copy-with:
type Person = { Name: string; Age: int }

let alice = { Name = "Alice"; Age = 30 }
let older  = { alice with Age = 31 }   // alice is unchanged — immutability is the default
```
