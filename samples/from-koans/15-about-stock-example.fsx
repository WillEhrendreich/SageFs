// ============================================================
//  🧘  About the Stock Example — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutTheStockExample.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  Apply everything you've learned to a real data processing task.
//  No more condiments and toy examples — this is real CSV parsing.
//
//  TASK: Find the date with the greatest difference between the
//        opening and closing prices in the Microsoft stock data.
//
//  Fill in the __ to make the test go 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip
open System.Globalization

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── The data ─────────────────────────────────────────────────
// Microsoft stock prices for March 2012.
// Format: Date,Open,High,Low,Close,Volume,Adj Close

let stockData = [
  "Date,Open,High,Low,Close,Volume,Adj Close"
  "2012-03-30,32.40,32.41,32.04,32.26,31749400,32.26"
  "2012-03-29,32.06,32.19,31.81,32.12,37038500,32.12"
  "2012-03-28,32.52,32.70,32.04,32.19,41344800,32.19"
  "2012-03-27,32.65,32.70,32.40,32.52,36274900,32.52"
  "2012-03-26,32.19,32.61,32.15,32.59,36758300,32.59"
  "2012-03-23,32.10,32.11,31.72,32.01,35912200,32.01"
  "2012-03-22,31.81,32.09,31.79,32.00,31749500,32.00"
  "2012-03-21,31.96,32.15,31.82,31.91,37928600,31.91"
  "2012-03-20,32.10,32.15,31.74,31.99,41566800,31.99"
  "2012-03-19,32.54,32.61,32.15,32.20,44789200,32.20"
  "2012-03-16,32.91,32.95,32.50,32.60,65626400,32.60"
  "2012-03-15,32.79,32.94,32.58,32.85,49068300,32.85"
  "2012-03-14,32.53,32.88,32.49,32.77,41986900,32.77"
  "2012-03-13,32.24,32.69,32.15,32.67,48951700,32.67"
  "2012-03-12,31.97,32.20,31.82,32.04,34073600,32.04"
  "2012-03-09,32.10,32.16,31.92,31.99,34628400,31.99"
  "2012-03-08,32.04,32.21,31.90,32.01,36747400,32.01"
  "2012-03-07,31.67,31.92,31.53,31.84,34340400,31.84"
  "2012-03-06,31.54,31.98,31.49,31.56,51932900,31.56"
  "2012-03-05,32.01,32.05,31.62,31.80,45240000,31.80"
  "2012-03-02,32.31,32.44,32.00,32.08,47314200,32.08"
  "2012-03-01,31.93,32.39,31.85,32.29,77344100,32.29"
  "2012-02-29,31.89,32.00,31.61,31.74,59323600,31.74"
]

// ── Helper functions — Alt+Enter these to understand the data ─

let splitCommas (line: string) = line.Split(',')

// Parse a double using the invariant culture (dot as decimal separator):
let parseDouble (s: string) =
  System.Double.Parse(s, CultureInfo.InvariantCulture)

// Let's explore the data:
let header = stockData.[0]
header             // → "Date,Open,High,Low,Close,Volume,Adj Close"
splitCommas header // → [|"Date"; "Open"; "High"; "Low"; "Close"; "Volume"; "Adj Close"|]

// One data row:
let firstRow = stockData.[1]
let cols     = splitCommas firstRow
cols.[0]   // → "2012-03-30"  (date)
cols.[1]   // → "32.40"       (open)
cols.[4]   // → "32.26"       (close)

// The open-close difference for one row:
abs (parseDouble cols.[1] - parseDouble cols.[4])   // → 0.14

// ── Your solution ─────────────────────────────────────────────
// Find the date (string) with the GREATEST |open - close| difference.
// Hints:
//   1. Skip the header row: List.tail stockData
//   2. For each row: splitCommas → get date, open, close
//   3. Compute abs (open - close)
//   4. Find the row with the maximum difference
//   5. Return the date string
//
// Build your pipeline here and Alt+Enter to verify:
//   let myAnswer =
//     stockData
//     |> List.tail
//     |> List.map (fun row -> ...)
//     |> List.maxBy (fun (_, diff) -> diff)
//     |> fst

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about the stock example" [

  test "YouGotTheAnswerCorrect" {
    // Replace __ with your pipeline (starting from stockData |> List.tail |> ...):
    let result : string = __
    result |> Expect.equal "date with biggest open/close swing" "2012-03-13"
  }

  // Bonus: verify your parsing helpers work correctly:
  test "SplitCommasWorks" {
    let cols = splitCommas "2012-03-30,32.40,32.41,32.04,32.26,31749400,32.26"
    cols.[0] |> Expect.equal "first column is date" "2012-03-30"
    cols.[1] |> Expect.equal "second column is open" "32.40"
    cols.[4] |> Expect.equal "fifth column is close" "32.26"
  }

  test "ParseDoubleWorks" {
    let value = parseDouble "32.40"
    value |> Expect.floatClose "should parse 32.40" Accuracy.medium 32.40
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter each step of your pipeline to debug
// 2. Can you find the day with the highest closing price?
// 3. Can you compute the average daily open-close range?
// 4. Chart the closing prices over time using a simple text sparkline
// ============================================================
