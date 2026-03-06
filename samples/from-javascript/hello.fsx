// ============================================================
//  🟨 → 🦅  Coming from JavaScript / TypeScript? You'll feel right at home.
//  (And you'll never go back to `undefined is not a function`.)
//  Alt+Enter any expression. Results appear inline — no console.log needed.
// ============================================================

// ── Variables: let is immutable by default ──
// JS/TS: let x = 42;  (mutable by default, who thought that was fine?)
// F#:
let x = 42               // immutable — no one can reassign this
let mutable y = 0        // need mutation? say so.
y <- y + 1               // <- is assignment in F#

// ── Arrow functions? We have lambdas. ──
// JS: const double = (x) => x * 2;
// F#:
let double = fun x -> x * 2        // explicit lambda
let double' x = x * 2              // or just... a function definition
// Both work the same way.  F# functions are values too.

double 21     // Alt+Enter → 42 ✓
double' 21    // → 42 ✓

// ── Types: TypeScript → F# ──
// TS:  interface Person { name: string; age: number; }
// F#:
type Person = { Name: string; Age: int }
// It's immutable. No undefined. No partial object assignment. Built-in equality.

let alice = { Name = "Alice"; Age = 30 }
let older  = { alice with Age = 31 }   // spread-style copy, but type-safe

// ── Discriminated Unions: TypeScript union types, but actually good ──
// TS: type Shape = { kind: "circle"; r: number } | { kind: "rect"; w: number; h: number }
// F#:
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float

// TS: you get no warning if you don't handle all cases in a switch.
// F#: the compiler WILL warn you.  Exhaustive pattern matching.
let area shape =
  match shape with
  | Circle r          -> System.Math.PI * r * r
  | Rectangle (w, h) -> w * h

area (Circle 3.0)           // → 28.27... ✓
area (Rectangle (4.0, 5.0)) // → 20.0 ✓

// ── Option<'T>: no more `undefined`, ever ──
// TS: x?: string  (might be undefined, might be null, might be "")
// F#: string option  — either None or Some "value"
let greet (name: string option) =
  match name with
  | Some n -> $"Hey, {n}!"
  | None   -> "Hey, you!"

greet (Some "Alice")  // → "Hey, Alice!" ✓
greet None            // → "Hey, you!" ✓

// ── Pipelines: |> is cleaner than method chaining ──
// JS: [1,2,3,4,5].filter(x => x % 2 === 0).map(x => x * x)
// F#:
let result =
  [1..10]
  |> List.filter (fun x -> x % 2 = 0)
  |> List.map    (fun x -> x * x)
// → [4; 16; 36; 64; 100]
// No prototype chain, no .bind(this), no lost `this` context bugs.

// ── Async: cleaner than Promises + async/await ──
// JS: const data = await fetch(url).then(r => r.text());
// F#:
let fetchText url = async {
  use client = new System.Net.Http.HttpClient()
  let! text = client.GetStringAsync(url) |> Async.AwaitTask
  return text
}
// Run with: Async.RunSynchronously (fetchText "https://example.com")
// No unhandled promise rejection warnings.  No .catch() chains.

// ── Modules: no imports from ../../../../utils/helpers/index.js ──
// F# modules are just namespaces.  No barrel files.  No circular import hell.
module MathUtils =
  let square x = x * x
  let cube x = x * x * x

MathUtils.square 5  // → 25 ✓
MathUtils.cube 3    // → 27 ✓

// ── Pattern matching: the switch that actually works ──
// JS: switch (x) { case 1: ...; default: ... }
// TS: exhaustive switch still needs workarounds (_: never etc.)
// F#:
let classify x =
  match x with
  | 0                  -> "zero"
  | n when n < 0      -> "negative"
  | n when n % 2 = 0  -> "positive even"
  | _                  -> "positive odd"

classify 0    // → "zero" ✓
classify -3   // → "negative" ✓
classify 4    // → "positive even" ✓

// ── No package.json drama ──
// F# scripts reference packages inline — no separate dependency file:
//   #r "nuget: Newtonsoft.Json"
//   open Newtonsoft.Json
// For projects: NuGet via `dotnet add package` — works the same as npm install,
// but dependency resolution is deterministic and reproducible.

// ── Type inference: better than TypeScript's ──
// TS: you annotate everything and still get `any` creep
// F#: almost nothing needs annotations — the compiler figures it out
let add a b = a + b           // int -> int -> int, inferred
let concat (a: string) b = a + b   // one annotation is enough

// ── Fable: your F# compiles to JavaScript too ──
// If you still need JS output (for VS Code extensions, browser code):
// F# → Fable → clean, idiomatic JS/TS output
// The SageFs VS Code extension is written entirely in F# via Fable.
// No TypeScript needed.

// ── The SageFs hot reload vs. webpack/vite HMR comparison ──
// vite HMR:    ~200-400ms, sometimes flashes, sometimes loses state
// webpack HMR: 1-5s, often just does a full reload anyway
// SageFs:      ~100ms, Harmony patches method pointers in the running process
//              Your server doesn't restart.  Your client reconnects via SSE.
//              The browser updates before you look up from your keyboard.

// ── Migration cheatsheet ──
// const x = 42                     → let x = 42
// let x = 42 (mutable)             → let mutable x = 42
// x = newValue                     → x <- newValue
// (x) => x * 2                     → fun x -> x * 2
// interface Foo { bar: string }    → type Foo = { Bar: string }
// x?.foo                           → x |> Option.map (fun x -> x.Foo)
// x ?? defaultValue                → x |> Option.defaultValue defaultValue
// x !== null && x !== undefined    → x |> Option.isSome
// arr.filter(pred).map(proj)       → arr |> List.filter pred |> List.map proj
// arr.reduce(f, init)              → List.fold f init arr
// Promise<T>                       → Async<T>
// await x                          → let! x = ...   (inside async { })
// throw new Error("msg")           → failwith "msg"  or  Error "msg"
// console.log(x)                   → printfn "%A" x

// ── EXERCISES ──
// 1. Write a function that groups a list of records by a string field (like _.groupBy)
// 2. Replace an async/await chain you know with an F# async { } block
// 3. Model a JSON API response (success/error) as a Discriminated Union
// ============================================================
