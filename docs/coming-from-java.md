# ☕ Coming from Java?

Welcome. You've been writing `AbstractSingletonProxyFactoryBean` and we won't judge you — the ecosystem made you do it. But it's time. F# is what Java always wished it could be: expressive, type-safe, concise, and running on a genuinely great runtime (.NET, not JVM — yes, the GC is better).

Pain you're leaving behind: 10 files for one feature, XML everywhere, Spring Boot startup time, `Optional<Optional<List<? extends Comparable<? super T>>>>`, and `NullPointerException` at line 1 of your stack trace.

**What you'll love immediately:**
- A `Person` record is one line. Getters, equals, hashCode, toString — free.
- Pattern matching on sealed types, with exhaustiveness checking — the Java 21 feature, but good
- No `Optional.ofNullable(x).map(f).orElse(null)` — `Option<'T>` is a language citizen
- Build time: `dotnet build` is fast. SageFs day-to-day: no build at all.

**→ [Start here: `samples/from-java/hello.fsx`](../samples/from-java/hello.fsx)**

```fsharp
// Java: public record Person(String name, int age) {}  +  equals + hashCode + toString
// F#:
type Person = { Name: string; Age: int }
// structural equality: { Name = "Alice"; Age = 30 } = { Name = "Alice"; Age = 30 } → true
// toString: printfn "%A" { Name = "Alice"; Age = 30 } → { Name = "Alice"; Age = 30 }
// No Lombok. No Jackson annotations. Just data.
```
