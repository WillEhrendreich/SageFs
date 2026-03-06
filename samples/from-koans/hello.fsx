// ============================================================
//  🧘 → 🦅  Coming from FSharpKoans? Welcome, graduate.
//  You already know F#. You proved it 20 exercises at a time.
//  Now let's ditch `dotnet watch run` and actually build things.
//  Alt+Enter any expression. Results appear inline. No terminal.
// ============================================================

// You've been here before:
//   let actual_value = __
//   AssertEquality expected_value actual_value
//   dotnet run → FAILED → fix → dotnet run → PASSED → next
//
// That loop taught you F#. It also taught you patience, because
// `dotnet watch run` takes 2-4 seconds per cycle and the feedback
// is a wall of terminal text. You squint. You scroll. You guess
// which koan broke.
//
// SageFs kills that loop. You're about to see why.

// ── 1. The instant feedback upgrade ──────────────────────────
//
// In Koans you wrote:
//     let x = 1 + 1
//     AssertEquality x __    ← figure it out, run, check terminal
//
// In SageFs, just evaluate the expression:

let x = 1 + 1              // Alt+Enter → 2 (inline, right here)
let name = "F#"             // Alt+Enter → "F#"
let greeting = $"Hello, {name}!"
// Alt+Enter → "Hello, F#!" — no print, no assertion, just the answer.

// You already KNOW what 1 + 1 is. Koans made you prove it.
// SageFs lets you explore and see results as you think.


// ── 2. Everything you learned, but now it's useful ──────────
//
// Koans taught you pattern matching on DUs.
// Here's that same knowledge applied to a real domain:

type OrderStatus =
    | Pending
    | Shipped  of trackingNumber: string
    | Delivered of deliveredAt: System.DateTime
    | Cancelled of reason: string

let describeOrder status =
    match status with
    | Pending        -> "⏳ Awaiting shipment"
    | Shipped t      -> $"📦 Shipped — tracking: {t}"
    | Delivered d    -> $"✅ Delivered on {d:yyyy-MM-dd}"
    | Cancelled r    -> $"❌ Cancelled: {r}"

// Alt+Enter on each of these to see the output:
describeOrder Pending
describeOrder (Shipped "1Z999AA10123456784")
describeOrder (Cancelled "Customer changed mind")

// You learned this in AboutDiscriminatedUnions.fs.
// The difference: you're modeling real things now, not condiments.


// ── 3. Pipelines — from koan exercises to real data ──────────
//
// AboutPipelining.fs had you fill in blanks on toy lists.
// Now chain real transformations and see every step:

type Sale = { Product: string; Amount: float; Region: string }

let sales = [
    { Product = "Widget";  Amount = 29.99; Region = "North" }
    { Product = "Gadget";  Amount = 49.99; Region = "South" }
    { Product = "Widget";  Amount = 29.99; Region = "South" }
    { Product = "Gizmo";   Amount = 99.99; Region = "North" }
    { Product = "Widget";  Amount = 29.99; Region = "North" }
    { Product = "Gadget";  Amount = 49.99; Region = "North" }
]

// Revenue by region — Alt+Enter the whole pipeline:
let revenueByRegion =
    sales
    |> List.groupBy (fun s -> s.Region)
    |> List.map (fun (region, items) ->
        region, items |> List.sumBy (fun s -> s.Amount))
// → [("North", 209.96); ("South", 79.98)]

// Most popular product:
let topProduct =
    sales
    |> List.countBy (fun s -> s.Product)
    |> List.sortByDescending snd
    |> List.head
// → ("Widget", 3)

// In Koans this was: [1;2;3] |> List.filter (fun x -> x > __)
// Same skill. Real data. Instant feedback.


// ── 4. Records and copy-with — beyond the koan ──────────────
//
// AboutRecordTypes.fs taught you the syntax.
// Here's why it matters:

type Config = {
    MaxRetries: int
    TimeoutMs:  int
    Verbose:    bool
    Endpoint:   string
}

let defaultConfig = {
    MaxRetries = 3
    TimeoutMs  = 5000
    Verbose    = false
    Endpoint   = "https://api.example.com"
}

// Non-destructive update — the original is untouched:
let debugConfig  = { defaultConfig with Verbose = true; TimeoutMs = 30000 }
let prodConfig   = { defaultConfig with MaxRetries = 5 }

defaultConfig    // Alt+Enter → the original, unchanged
debugConfig      // Alt+Enter → Verbose = true, TimeoutMs = 30000


// ── 5. Option — you learned it, now use it everywhere ────────
//
// AboutOptionTypes.fs showed you Some / None.
// Here's the pattern you'll use daily:

let tryParseInt (s: string) =
    match System.Int32.TryParse(s) with
    | true, n  -> Some n
    | false, _ -> None

// Chain Options with map/bind instead of nested ifs:
let doubled =
    "42"
    |> tryParseInt
    |> Option.map (fun n -> n * 2)
// → Some 84

let invalid =
    "nope"
    |> tryParseInt
    |> Option.map (fun n -> n * 2)
// → None — no crash, no exception, just None.

// Option.defaultValue for fallbacks:
let safeValue = invalid |> Option.defaultValue 0
// → 0


// ── 6. Your koan assertions → real tests ─────────────────────
//
// Koans used a custom test framework:
//     [<Koan>]
//     let AssertExpectation() =
//         AssertEquality (1 + 1) __
//
// Expecto is the F# community standard.
// SageFs runs these on every save — green gutter markers:

#r "nuget: Expecto"
open Expecto

let orderTests = testList "order status" [
    test "pending orders show waiting message" {
        let msg = describeOrder Pending
        Expect.stringContains msg "Awaiting" "should mention awaiting"
    }
    test "shipped orders include tracking number" {
        let msg = describeOrder (Shipped "ABC123")
        Expect.stringContains msg "ABC123" "should contain tracking"
    }
    test "cancelled orders explain why" {
        let msg = describeOrder (Cancelled "Out of stock")
        Expect.stringContains msg "Out of stock" "should contain reason"
    }
]

// In Koans:  dotnet watch run → see terminal pass/fail → scroll to find failure
// In SageFs: save → gutter markers turn green → you see WHICH tests pass/fail
//            right next to the code. No scrolling. No terminal.

let pipelineTests = testList "pipeline exercises" [
    test "revenue by region sums correctly" {
        let northRevenue =
            revenueByRegion
            |> List.find (fun (r, _) -> r = "North")
            |> snd
        Expect.floatClose Accuracy.medium northRevenue 209.96 "north revenue"
    }
    test "top product is Widget" {
        Expect.equal (fst topProduct) "Widget" "most frequent product"
    }
]


// ── 7. Beyond Koans: things they didn't teach you ───────────
//
// Koans covered the basics. Here's what comes next:

// Computation expressions (async, result, custom):
let fetchData url = async {
    use client = new System.Net.Http.HttpClient()
    let! response = client.GetStringAsync(url) |> Async.AwaitTask
    return response.Length
}
// Alt+Enter won't run the async, but you can:
// Async.RunSynchronously (fetchData "https://example.com")

// Active patterns — pattern matching on steroids:
let (|Even|Odd|) n = if n % 2 = 0 then Even else Odd

let classify = function
    | Even -> "even"
    | Odd  -> "odd"

classify 42   // → "even"
classify 7    // → "odd"

// Units of measure — the compiler prevents Mars Climate Orbiter bugs:
[<Measure>] type kg
[<Measure>] type lb

let toKg (pounds: float<lb>) : float<kg> = pounds * 0.453592<kg/lb>

toKg 150.0<lb>   // → 68.0388<kg>
// Try: toKg 150.0<kg>  — compiler ERROR. Can't pass kg where lb expected.


// ── 8. The SageFs workflow vs the Koans workflow ─────────────
//
// FSharpKoans:                    SageFs:
// ─────────────                   ───────
// Edit → dotnet run → terminal    Edit → Alt+Enter → inline result
// ~3 sec feedback loop            ~200ms feedback loop
// One exercise at a time          Explore freely, evaluate anything
// Custom [<Koan>] framework       Expecto (industry standard)
// Terminal pass/fail output        Gutter markers (✓/✗) per test
// No IDE integration               VS Code / Neovim / TUI / Web
// .NET 6.0                         .NET 10 + hot reload
//
// You graduated from the dojo. This is the workshop.

// ── EXERCISES (yes, old habits die hard) ─────────────────────
// 1. Add a `Refunded` case to OrderStatus and update describeOrder
//    → save → watch the exhaustive match warning, then fix it
// 2. Write a pipeline that finds the average sale amount per product
// 3. Write an Expecto test that verifies your pipeline from #2
// 4. Model a Card type (Suit * Rank) and write a hand evaluator
//    → use active patterns for poker hands
// ============================================================
