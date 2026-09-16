# ☕ Coming from Java?

You've probably written `AbstractSingletonProxyFactoryBean` — the ecosystem left you no choice. F# is what Java's design aims for: expressive, type-safe, concise, and it runs on .NET instead of the JVM, with a better GC.

You'll leave behind 10 files for one feature, XML everywhere, Spring Boot startup time, `Optional<Optional<List<? extends Comparable<? super T>>>>`, and `NullPointerException` at line 1 of your stack trace.

**What you'll notice right away:**
- A `Person` record is one line. Getters, equals, hashCode, toString come free.
- Pattern matching on sealed types, with exhaustiveness checking (Java 21 added something similar)
- No `Optional.ofNullable(x).map(f).orElse(null)`; `Option<'T>` is built into the language
- `dotnet build` is fast, and day-to-day work in SageFs needs no build at all

**→ [Start here: `samples/from-java/hello.fsx`](../samples/from-java/hello.fsx)**

```fsharp
// Java: public record Person(String name, int age) {}  +  equals + hashCode + toString
// F#:
type Person = { Name: string; Age: int }
// structural equality: { Name = "Alice"; Age = 30 } = { Name = "Alice"; Age = 30 } → true
// toString: printfn "%A" { Name = "Alice"; Age = 30 } → { Name = "Alice"; Age = 30 }
// No Lombok. No Jackson annotations. Just data.
```
