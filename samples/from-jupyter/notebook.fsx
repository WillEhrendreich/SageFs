// ============================================================
//  📓 → 🦅  Coming from Jupyter Notebooks? You're home.
//  SageFs is what notebooks always wanted to be.
//  Alt+Enter any expression.  Results appear inline.  No browser.
// ============================================================

// ── The notebook cell model, but better ──
// In Jupyter, cells are the unit of execution.
// In SageFs, *any expression* is a cell. No box to draw.
// Hit Alt+Enter on any line and it runs.

// Just like a notebook cell:
let xs = [1.0; 2.0; 3.0; 4.0; 5.0]
let mean = xs |> List.average          // Alt+Enter → 3.0 ✓
let variance =
  let m = mean
  xs |> List.map (fun x -> (x - m) ** 2.0) |> List.average  // → 2.0 ✓

// ── No kernel crashes. No "restart and run all". ──
// Jupyter has one global mutable kernel state.
// You run cell 7 after cell 3 and wonder why nothing works.
// SageFs sessions are isolated processes.  Crash one → swap for a pre-warmed one.
// `hard_reset_fsi_session` in 200ms.  Your data is still there.

// ── Inline results instead of print() everywhere ──
// Jupyter: you need a cell that ends in an expression, or use display()
// SageFs:  Alt+Enter on *any* expression, anywhere in the file

let fib n =
  let rec go a b n = if n = 0 then a else go b (a + b) (n - 1)
  go 0 1 n

fib 10   // Alt+Enter here → 55 appears in the gutter ✓
fib 20   // → 6765 ✓
fib 30   // → 832040 ✓

// ── Data exploration — familiar territory ──
type Record = { Name: string; Value: float; Category: string }

let data = [
  { Name = "Alpha";   Value = 3.14;  Category = "A" }
  { Name = "Beta";    Value = 2.71;  Category = "B" }
  { Name = "Gamma";   Value = 1.41;  Category = "A" }
  { Name = "Delta";   Value = 0.57;  Category = "B" }
  { Name = "Epsilon"; Value = 1.73;  Category = "A" }
]

// Filter, map, group — same mental model as pandas, but with types
let groupA = data |> List.filter (fun r -> r.Category = "A")
let avgA    = groupA |> List.map (fun r -> r.Value) |> List.average
// Alt+Enter → avgA = 2.093...

// Group by — like groupby in pandas
let grouped =
  data
  |> List.groupBy (fun r -> r.Category)
  |> List.map (fun (cat, rs) ->
       cat, rs |> List.map (fun r -> r.Value) |> List.average)
// Alt+Enter → [("A", 2.09...); ("B", 1.64)]

// ── Markdown cells? We have comments. And they're compiled. ──
// (Your documentation can't go stale if it's in the same file as working code.)

// ── Plotting — coming soon / BYO library ──
// Plotly.NET works great with SageFs:
//   #r "nuget: Plotly.NET"
//   open Plotly.NET
//   [ for r in data -> r.Name, r.Value ]
//   |> Chart.Bar
//   |> Chart.show   // opens in browser tab, or use the SageFs dashboard

// ── The killer feature: live tests alongside your analysis ──
// In Jupyter you check assumptions manually.
// In SageFs you write them as tests and they run every time you save:

#r "nuget: Expecto"
open Expecto

let tests = testList "data invariants" [
  test "all values positive" {
    data |> List.iter (fun r -> Expect.isTrue (r.Value > 0.0) "value should be positive")
  }
  test "categories are only A or B" {
    data |> List.iter (fun r ->
      Expect.isTrue (r.Category = "A" || r.Category = "B") "valid category")
  }
]
// SageFs runs these on every save.  Gutter turns green.  No runTests() call needed.

// ── Script vs Project mode ──
// .fsx = interactive script (like a Jupyter notebook file)
//        great for exploration, data work, quick experiments
// .fs  = compiled module in a project
//        great for shipping production code
// SageFs handles both.  When you're ready to promote your script to production,
// just move the logic into a .fs file.  Nothing else changes.

// ── SageFs vs Jupyter: the honest comparison ──
//  Jupyter:  great for ad-hoc analysis, terrible for long-lived code
//  SageFs:   same interactive feel, but your code is real code
//            • type-checked as you type
//            • lives in source control (it's just a file, not JSON)
//            • hot-reloads into your running web app
//            • AI agents can run it via MCP
//            • no browser, no kernel, no "trust this notebook" warnings

// ── EXERCISES ──
// 1. Compute the standard deviation of `data` values using the variance pattern above
// 2. Add a `Score: int` field to Record and write a test that all scores are 0-100
// 3. Write a function that returns the top-N records by Value using List.sortByDescending
// ============================================================
