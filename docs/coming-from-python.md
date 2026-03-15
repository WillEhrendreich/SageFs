# 🐍 Coming from Python?

You already love interactive development — you live in the REPL. F# gives you the same energy, plus a compiler that catches your bugs before you run anything, blazing-fast pipelines instead of list comprehensions, and a type system that makes refactoring feel like a superpower instead of a minefield.

Pain you're leaving behind: `AttributeError: 'NoneType' object has no attribute 'foo'`, mystery runtime crashes, and the "just run it and see" debugging loop.

**What you'll love immediately:**
- `|>` pipelines read like Python chains, but faster and type-checked
- Pattern matching makes `if/elif/elif/elif/else` forests extinct
- `Option<'T>` means `None` is handled at compile time — no more surprise crashes
- SageFs = your Jupyter notebook, but in your editor, with live tests and hot reload

**→ [Start here: `samples/from-python/hello.fsx`](../samples/from-python/hello.fsx)**

```fsharp
// From this (Python):
// result = sum(x**2 for x in range(1, 11) if x % 2 == 0)

// To this (F#):
let result =
  [1..10]
  |> List.filter (fun x -> x % 2 = 0)
  |> List.map    (fun x -> x * x)
  |> List.sum
// Alt+Enter → 220. No running the file. No print(). Just results.
```
