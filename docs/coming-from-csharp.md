# 🔷 Coming from C#?

You're already home. Same .NET runtime. Same NuGet packages. Same `dotnet` CLI. You just get to stop writing `public class AbstractRepositoryFactoryImpl` and start writing code that says what it means.

Pain you're leaving behind: 50-line classes for 3-line concepts, null reference exceptions at 3am, `dotnet watch` rebuilding for 10 seconds when you fix a typo, and writing the same LINQ query 12 different ways because the extension method didn't exist.

**What you'll love immediately:**
- Records are immutable value objects with equality built in — one line
- Discriminated unions make `sealed class + pattern matching` elegant instead of painful
- `Result<'T, 'TError>` replaces `try/catch` spaghetti for expected failure paths
- SageFs hot reload patches method pointers at runtime — no rebuild, no restart

**→ [Start here: `samples/from-csharp/hello.fsx`](../samples/from-csharp/hello.fsx)**

```fsharp
// C#: public record Person(string Name, int Age);  // 1 line in modern C#
// F#: one line too, but with structural equality, hashCode, and copy-with:
type Person = { Name: string; Age: int }

let alice = { Name = "Alice"; Age = 30 }
let older  = { alice with Age = 31 }   // alice is unchanged — immutability is the default
```
