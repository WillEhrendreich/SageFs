# 📓 Coming from Jupyter Notebooks?

You're already living the interactive-first dream. SageFs takes everything you love about notebooks (eval any expression, see results inline, build understanding iteratively) and fixes everything you hate (kernel crashes, "restart and run all", the JSON-blob hell of version control, no type checking, no hot reload into production).

Pain you're leaving behind: "the kernel died", cell execution order mysteries, `git diff` on `.ipynb` showing base64 blobs, and the chasm between notebook exploration and shipped code.

**What you'll love immediately:**
- Alt+Enter on *any* expression — not just at the end of a cell
- Your code is a real `.fsx` file that `git diff` shows beautifully
- Write Expecto tests alongside your analysis — they run on every save
- When you're ready to ship, your exploration code *is* the production code

**→ [Start here: `samples/from-jupyter/notebook.fsx`](../samples/from-jupyter/notebook.fsx)**

```fsharp
// Your "cell" is any expression. Run it anywhere.
let data = [1.0; 2.0; 3.0; 4.0; 5.0]
let mean = data |> List.average     // Alt+Enter → 3.0, right in the gutter
let std  =
  data
  |> List.map (fun x -> (x - mean) ** 2.0)
  |> List.average
  |> sqrt                           // Alt+Enter → 1.414...
```
