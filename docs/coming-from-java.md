# ☕ Coming from Java?

F# gives you what Java keeps reaching for: a record in one line, real pattern matching, no null, and it still runs on the CLR's cousin, .NET. No `AbstractSingletonProxyFactoryBean`, no XML config, no ten files per feature.

You leave behind Spring Boot startup time, `Optional<Optional<List<? extends Comparable<? super T>>>>`, and a `NullPointerException` whose stack trace starts three layers away from the actual bug.

**What you'll notice right away:**
- A `Person` record is one line. Equals, hashCode, and toString come free.
- Pattern matching on sealed types, with exhaustiveness checking (Java 21 added something similar; F# has had it the whole time).
- No `Optional.ofNullable(x).map(f).orElse(null)`. `Option<'T>` is part of the language, not a library bolted on afterward.
- `dotnet build` is fast, and day-to-day work in SageFs needs no build at all. You're evaluating expressions, not compiling a JAR.

**→ [Start here: `samples/from-java/hello.fsx`](../samples/from-java/hello.fsx)**

```fsharp
// Java: public record Person(String name, int age) {}  +  equals + hashCode + toString
// F#:
type Person = { Name: string; Age: int }
// structural equality: { Name = "Alice"; Age = 30 } = { Name = "Alice"; Age = 30 } → true
// toString: printfn "%A" { Name = "Alice"; Age = 30 } → { Name = "Alice"; Age = 30 }
// No Lombok. No Jackson annotations. Just data.
```
