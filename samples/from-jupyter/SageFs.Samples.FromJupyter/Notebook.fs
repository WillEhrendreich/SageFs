module SageFs.Samples.FromJupyter.Notebook

open Expecto
open Expecto.Flip

// ============================================================
//  📓 → 🦅  Coming from Jupyter Notebooks? You're home.
//  SageFs is what notebooks always wanted to be.
// ============================================================

// ── Statistics ──
let xs = [1.0; 2.0; 3.0; 4.0; 5.0]
let mean = xs |> List.average
let variance =
  let m = mean
  xs |> List.map (fun x -> (x - m) ** 2.0) |> List.average

// ── Fibonacci ──
let fib n =
  let rec go a b n = if n = 0 then a else go b (a + b) (n - 1)
  go 0 1 n

// ── Data exploration ──
type Record = { Name: string; Value: float; Category: string }

let data = [
  { Name = "Alpha";   Value = 3.14;  Category = "A" }
  { Name = "Beta";    Value = 2.71;  Category = "B" }
  { Name = "Gamma";   Value = 1.41;  Category = "A" }
  { Name = "Delta";   Value = 0.57;  Category = "B" }
  { Name = "Epsilon"; Value = 1.73;  Category = "A" }
]

let groupA = data |> List.filter (fun r -> r.Category = "A")
let avgA   = groupA |> List.map (fun r -> r.Value) |> List.average

let grouped =
  data
  |> List.groupBy (fun r -> r.Category)
  |> List.map (fun (cat, rs) ->
       cat, rs |> List.map (fun r -> r.Value) |> List.average)

let tests = testList "from Jupyter" [
  testList "statistics" [
    test "mean of xs" {
      mean |> Expect.floatClose "mean of 1..5" Accuracy.medium 3.0
    }
    test "variance of xs" {
      variance |> Expect.floatClose "variance of 1..5" Accuracy.medium 2.0
    }
  ]

  testList "fibonacci" [
    test "fib 0 = 0" {
      fib 0 |> Expect.equal "fib(0)" 0
    }
    test "fib 1 = 1" {
      fib 1 |> Expect.equal "fib(1)" 1
    }
    test "fib 10 = 55" {
      fib 10 |> Expect.equal "fib(10)" 55
    }
    test "fib 20 = 6765" {
      fib 20 |> Expect.equal "fib(20)" 6765
    }
    test "fib 30 = 832040" {
      fib 30 |> Expect.equal "fib(30)" 832040
    }
  ]

  testList "data exploration" [
    test "group A has 3 records" {
      groupA |> List.length |> Expect.equal "3 items in A" 3
    }
    test "average of group A" {
      avgA
      |> Expect.floatClose "avg of 3.14, 1.41, 1.73" Accuracy.low ((3.14 + 1.41 + 1.73) / 3.0)
    }
    test "grouped categories" {
      grouped |> List.map fst |> List.sort
      |> Expect.equal "categories" ["A"; "B"]
    }
  ]

  testList "data invariants" [
    test "all values positive" {
      data |> List.iter (fun r ->
        Expect.isTrue "value should be positive" (r.Value > 0.0))
    }
    test "categories are only A or B" {
      data |> List.iter (fun r ->
        Expect.isTrue "valid category" (r.Category = "A" || r.Category = "B"))
    }
  ]
]
