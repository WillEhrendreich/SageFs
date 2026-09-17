# 📓 Coming from Jupyter Notebooks?

SageFs gives you the parts of notebooks you use: evaluate any expression and see the result inline, one step at a time. It drops the parts that hurt: kernel crashes, "restart and run all," `.ipynb` files that show up as JSON blobs in version control, no type checking, and no path from exploration to production code.

Your code lives in a plain `.fsx` file, so `git diff` reads like code instead of base64. Execution order is whatever you evaluated, and you can see it. And the exploration code you end up with is the code you ship.

**What you'll notice right away:**
- Alt+Enter works on any expression, not just at the end of a cell
- Your code is a real `.fsx` file, so `git diff` shows it clearly
- Write Expecto tests alongside your analysis, and live testing re-runs them as you save
- When you're ready to ship, your exploration code is the production code

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
