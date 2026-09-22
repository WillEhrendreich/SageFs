# 🐍 Coming from Python?

If you live in a Python REPL, F# will feel familiar. Same evaluate-as-you-go rhythm. What you get on top: a compiler that checks types before you run anything, pipelines instead of list comprehensions, and `Option<'T>` so `None` gets handled at compile time instead of blowing up three functions downstream.

No more `AttributeError: 'NoneType' object has no attribute 'foo'`, no more "just run it and see."

**What you'll notice right away:**
- `|>` pipelines read like Python method chains, and they're type-checked
- Pattern matching replaces `if/elif/elif/elif/else` chains
- `Option<'T>` forces you to handle `None` at compile time, so it can't crash you at runtime
- SageFs runs like a notebook inside your editor, with live testing and hot reload built in

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
