# 🦀 Coming from Rust?

You're going to feel right at home. `Option`, `Result`, pattern matching, discriminated unions, immutability by default, zero `null` — F# and Rust share the same design philosophy. The difference is that F# runs on .NET, skips the borrow checker, and gives you hot reload and interactive scripting.

Pain you're leaving behind: borrow checker fights for straightforward code, 45-second compile times for medium projects, no REPL, and having to reach for Python every time you want to explore data.

**What you'll love immediately:**
- `Option<'T>`, `Result<'T, 'E>`, and exhaustive pattern matching — just like Rust
- Records and DUs have structural equality by default — no `#[derive(PartialEq)]` needed
- Hot reload: your running program patches itself on save — impossible in Rust, trivial here
- `.fsx` scripts give you the interactive exploration story Rust has always lacked

**→ [Start here: `samples/from-rust/hello.fsx`](../samples/from-rust/hello.fsx)**

```fsharp
// Rust: enum Shape { Circle { radius: f64 }, Rectangle { width: f64, height: f64 } }
// F#:
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float

// match is exhaustive just like Rust — add a case, get a warning everywhere it's not handled
let area = function
  | Circle r          -> System.Math.PI * r * r
  | Rectangle (w, h) -> w * h
```
