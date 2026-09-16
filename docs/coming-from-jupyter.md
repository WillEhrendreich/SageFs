# 📓 Coming from Jupyter Notebooks?

SageFs keeps what you like about notebooks — evaluate any expression, see results inline, build understanding step by step — and fixes what you don't: kernel crashes, "restart and run all," `.ipynb` files that turn into JSON blobs in version control, no type checking, and no path from exploration to production code.

You'll leave behind "the kernel died," cell execution order mysteries, `git diff` on `.ipynb` showing base64 blobs, and the gap between notebook exploration and shipped code.

**What you'll notice right away:**
- Alt+Enter works on any expression, not just at the end of a cell
- Your code is a real `.fsx` file, so `git diff` shows it clearly
- Write Expecto tests alongside your analysis; they run on every save
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
