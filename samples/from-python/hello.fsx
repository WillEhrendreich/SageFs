// ============================================================
//  🐍 → 🦅  Coming from Python? Welcome to F#!
//  Open this file in SageFs, hit Alt+Enter on any line,
//  and watch results appear inline — no print() required.
// ============================================================

// ── 1. Variables aren't mutable by default.  Deal with it.  ──
//    Python: x = 42
//    F#:
let x = 42            // immutable — the compiler will yell if you try to reassign it
                      // (you'll grow to love this, I promise)

let mutable counter = 0   // need mutation? fine, but say so explicitly
counter <- counter + 1    // <- is assignment, not =

// ── 2. No indentation bugs, ever. F# uses indentation *and* types. ──
//    Python: forgot to indent? Runtime crash. Good luck.
//    F#:     the compiler catches that before you even save.

// ── 3. Functions are first-class. Just like Python. But faster. ──
//    Python: def double(x): return x * 2
let double x = x * 2       // no parens, no def, no return, no colon.  breathe.

let result = double 21      // call it — no parens needed for single args
// Hit Alt+Enter here → result = 42 appears inline, instantly ✓

// ── 4. Pipelines: |> is your new best friend ──
//    Python: reduce(lambda acc, x: acc + x, filter(lambda x: x % 2 == 0, map(lambda x: x * 2, [1..10])))
//    (yes, that's a real Python thing people write. We don't talk about it.)
let answer =
  [1..10]
  |> List.map (fun x -> x * 2)
  |> List.filter (fun x -> x % 2 = 0)
  |> List.sum
// Alt+Enter → 60 ✓  (clean, readable, no lambda spaghetti)

// ── 5. Pattern matching: the switch/if-elif you always deserved ──
//    Python: a forest of if/elif/else
let describe n =
  match n with
  | 0           -> "zero"
  | n when n < 0 -> "negative"
  | 1 | 2 | 3  -> "small"
  | _           -> "big"   // _ is "anything else"

describe -5   // Alt+Enter → "negative" ✓
describe 2    // → "small" ✓

// ── 6. Discriminated Unions: enums on steroids ──
//    Python: you'd use a class hierarchy, or a string, or hope for the best
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float
  | Triangle  of base': float * height: float

let area shape =
  match shape with
  | Circle r           -> System.Math.PI * r * r
  | Rectangle (w, h)  -> w * h
  | Triangle (b, h)   -> 0.5 * b * h

area (Circle 5.0)          // → 78.539... ✓
area (Rectangle (4.0, 6.0)) // → 24.0 ✓

// ── 7. Records: named tuples that are actually good ──
//    Python: @dataclass or NamedTuple, but with more ceremony
type Person = { Name: string; Age: int }

let alice = { Name = "Alice"; Age = 30 }
let olderAlice = { alice with Age = 31 }  // non-destructive update — alice is unchanged!

// ── 8. Option<'T>: None doesn't crash your program ──
//    Python: None is a loaded gun.  F# makes you handle it or it won't compile.
let safeDivide a b =
  if b = 0 then None
  else Some (a / b)

match safeDivide 10 2 with
| Some result -> printfn "Got %d" result
| None        -> printfn "Division by zero!"
// No more AttributeError: 'NoneType' object has no attribute '...' in idiomatic code.

// ── 9. Collections — familiar, but typed ──
let numbers = [1; 2; 3; 4; 5]      // list  (Python list)
let arr = [|1; 2; 3|]               // array (Python list, but fixed-size + faster)
let set = Set.ofList [1; 2; 2; 3]   // set   (Python set)
let map = Map.ofList [("a", 1); ("b", 2)]  // Map  (Python dict)

// All have the same pipeline-friendly functions:
numbers |> List.filter (fun x -> x > 2) |> List.map (fun x -> x * x)
// → [9; 16; 25]

// ── 10. Async — cleaner than asyncio ──
//    No more async/await keyword soup.  F# async is a computation expression.
let fetchData url = async {
  use client = new System.Net.Http.HttpClient()
  let! response = client.GetStringAsync(url) |> Async.AwaitTask
  return response.Length
}
// Run it: Async.RunSynchronously (fetchData "https://example.com")

// ── 11. No semicolons. No curly braces. No colons after if/for. ──
//    You're going to be so much less tired at the end of the day.

// ── 12. The SageFs difference from Jupyter ──
//    Jupyter: you run cells in sequence, hoping for the best
//    SageFs:  • Live test results in your gutter as you type
//             • Hot reload — save a .fs file, your web app updates in <100ms
//             • Works in VS Code, Neovim, or a terminal TUI — no browser tab
//             • AI agents (MCP) can run your code directly — no copy-paste

// ── EXERCISES ──
// 1. Write a function that sums only even squares from 1..100 using |>
// 2. Add a `Square of side: float` case to Shape and handle it in area
// 3. Make a Record type for a Task and a list of tasks, filter the done ones
// ============================================================
