# ☕ Coming from Java?

F# gives you what Java keeps reaching for: a record in one line, real pattern matching, no null, and it runs on .NET. No `AbstractSingletonProxyFactoryBean`, no XML config, no ten files per feature.

You leave behind Spring Boot startup time, `Optional<Optional<List<? extends Comparable<? super T>>>>`, and `NullPointerException` at line 1 of the stack trace.

**What you'll notice right away:**
- A `Person` record is one line. Equals, hashCode, and toString come free.
- Pattern matching on sealed types, with exhaustiveness checking (Java 21 added something similar).
- No `Optional.ofNullable(x).map(f).orElse(null)`. `Option<'T>` is part of the language.
- `dotnet build` is fast, and day-to-day work in SageFs needs no build at all.

**→ [Start here: `samples/from-java/hello.fsx`](../samples/from-java/hello.fsx)**

```fsharp
// Java: public record Person(String name, int age) {}  +  equals + hashCode + toString
// F#:
type Person = { Name: string; Age: int }
// structural equality: { Name = "Alice"; Age = 30 } = { Name = "Alice"; Age = 30 } → true
// toString: printfn "%A" { Name = "Alice"; Age = 30 } → { Name = "Alice"; Age = 30 }
// No Lombok. No Jackson annotations. Just data.
```
