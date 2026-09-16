# 🐍 Coming from Python?

If you already work in a REPL, F# will feel familiar. You get the same interactive style, plus a compiler that catches bugs before you run anything, fast pipelines in place of list comprehensions, and a type system that makes refactoring safe instead of risky.

You'll leave behind `AttributeError: 'NoneType' object has no attribute 'foo'`, mystery runtime crashes, and the "just run it and see" debugging loop.

**What you'll notice right away:**
- `|>` pipelines read like Python chains, but they're faster and type-checked
- Pattern matching replaces `if/elif/elif/elif/else` chains
- `Option<'T>` means `None` is handled at compile time, so it can't cause a surprise crash
- SageFs works like a Jupyter notebook in your editor, with live tests and hot reload

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
