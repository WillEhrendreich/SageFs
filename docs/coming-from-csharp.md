# 🔷 Coming from C#?

You're on the same .NET runtime, the same NuGet packages, and the same `dotnet` CLI. The difference is you stop writing `public class AbstractRepositoryFactoryImpl` and start writing code that says what it means.

You'll leave behind 50-line classes for 3-line concepts, null reference exceptions at 3am, `dotnet watch` taking 10 seconds to rebuild after a typo fix, and writing the same LINQ query a dozen different ways because the extension method didn't exist.

**What you'll notice right away:**
- Records are immutable value objects with equality built in, in one line
- Discriminated unions make `sealed class + pattern matching` simple instead of painful
- `Result<'T, 'TError>` replaces `try/catch` for expected failure paths
- SageFs hot reload patches method pointers at runtime, so there's no rebuild and no restart

**→ [Start here: `samples/from-csharp/hello.fsx`](../samples/from-csharp/hello.fsx)**

```fsharp
// C#: public record Person(string Name, int Age);  // 1 line in modern C#
// F#: one line too, but with structural equality, hashCode, and copy-with:
type Person = { Name: string; Age: int }

let alice = { Name = "Alice"; Age = 30 }
let older  = { alice with Age = 31 }   // alice is unchanged — immutability is the default
```
