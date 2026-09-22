# 📓 Coming from Jupyter Notebooks?

I like what notebooks are for: evaluate an expression, see the result right there, one step at a time. I don't like what they cost you to get it. Kernel crashes, "restart and run all," `.ipynb` files that show up as JSON blobs in a diff, no type checking, and no clear path from exploration to code you'd actually ship.

SageFs keeps the good part and drops the rest. Your code lives in a plain `.fsx` file, so `git diff` reads like code instead of base64. Execution order is whatever you evaluated, and you can see it happen. And when you're done exploring, the code you wrote is the code that ships. There's no notebook-to-production translation step to get wrong.

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
