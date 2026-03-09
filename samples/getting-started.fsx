// ============================================================
//  🦅  Welcome to SageFs — Your F# Live Development Environment
//
//  This file teaches you SageFs in 5 minutes, one block at a time.
//  Place your cursor on any expression and press Alt+Enter.
//  You'll see the result appear inline. No build step. No waiting.
// ============================================================

// ── 1. Instant feedback ──
// Put your cursor here and press Alt+Enter:
1 + 1
// → 2   That's it. You just used SageFs.

// ── 2. Let bindings — names for values ──
let greeting = "Hello from SageFs!"
// Alt+Enter on `greeting` → "Hello from SageFs!"

let year = 2026
let pi  = 3.14159

// F# infers types. No annotations needed (but you can add them).
let radius : float = 4.2

// ── 3. Functions — just let bindings with parameters ──
let double x = x * 2
double 21     // → 42

let greet name = $"Hey {name}, welcome to F#!"
greet "World" // → "Hey World, welcome to F#!"

// ── 4. The pipeline operator |> — F#'s superpower ──
// Read left to right, like a sentence:
[1..10]
|> List.filter (fun n -> n % 2 = 0)
|> List.map double
|> List.sum
// → 60  (evens 2,4,6,8,10 → doubled 4,8,12,16,20 → sum 60)

// Compare without pipelines — same thing, harder to read:
// List.sum (List.map double (List.filter (fun n -> n % 2 = 0) [1..10]))

// ── 5. Records — lightweight data structures ──
type Pet = { Name: string; Species: string; Age: int }

let rex = { Name = "Rex"; Species = "Dog"; Age = 5 }
let olderRex = { rex with Age = 6 }  // immutable update — rex unchanged

rex.Name      // → "Rex"
olderRex.Age  // → 6
rex.Age       // → 5  (still 5!)

// ── 6. Discriminated unions — the heart of F# modeling ──
type Feedback =
  | ThumbsUp
  | ThumbsDown
  | Rating of stars: int
  | Comment of text: string

let respond feedback =
  match feedback with
  | ThumbsUp       -> "Thanks! 👍"
  | ThumbsDown     -> "We'll do better 👎"
  | Rating stars   -> $"Rated {stars}/5 ⭐"
  | Comment text   -> $"You said: {text}"

respond (Rating 5)       // → "Rated 5/5 ⭐"
respond ThumbsUp         // → "Thanks! 👍"

// The compiler warns you if you forget a case. No runtime surprises.

// ── 7. Pattern matching — more powerful than switch ──
let describe value =
  match value with
  | x when x < 0  -> "negative"
  | 0              -> "zero"
  | 1              -> "one"
  | x when x < 10 -> "small"
  | _              -> "big"

describe 0   // → "zero"
describe 42  // → "big"
describe -3  // → "negative"

// ── 8. Writing tests with Expecto ──
// SageFs uses Expecto for testing. Here's the basics:
//
// open Expecto
//
// let myTests = testList "my tests" [
//   test "addition works" {
//     let result = 2 + 2
//     Expect.equal result 4 "2 + 2 should be 4"
//   }
//   test "strings concatenate" {
//     let result = "Hello" + " " + "World"
//     Expect.equal result "Hello World" "string concat"
//   }
// ]
//
// Save the file → SageFs runs your tests automatically (live testing).
// Green gutter marks = passing. Red = failing. It's that simple.

// ── 9. Hot reload — edit and see changes instantly ──
// SageFs watches your source files. When you save:
//   • Changed files are reloaded into the FSI session
//   • Affected tests re-run automatically
//   • Results update in your editor's gutter
//
// Keybindings:
//   Ctrl+Alt+W → Watch all project files
//   Ctrl+Alt+U → Unwatch all files
//   Ctrl+Alt+T → Toggle live testing on/off
//
// The status bar shows how many files are watched
// and whether live testing is active.

// ── 10. Where to go next ──
//
// 📚 F# Koans (learn F# from scratch):
//    Open samples/from-koans/00-about-sagefs-koans.fsx
//    22 progressive exercises, from basics to data pipelines.
//
// 🔷 Coming from C#?    → samples/from-csharp/hello.fsx
// 🐍 Coming from Python? → samples/from-python/hello.fsx
// 🟨 Coming from JS/TS?  → samples/from-javascript/hello.fsx
// ☕ Coming from Java?    → samples/from-java/hello.fsx
// 🦀 Coming from Rust?   → samples/from-rust/hello.fsx
// 📓 From Jupyter?       → samples/from-jupyter/notebook.fsx
//
// 🎮 Fun demos:
//    samples/demos/raylib-hello.fsx  — draw graphics with Raylib
//    samples/demos/raylib-game.fsx   — a simple game
//    samples/demos/webapp-datastar.fsx — reactive web app
//
// Happy hacking! 🦅
