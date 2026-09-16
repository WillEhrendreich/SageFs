# 🦀 Coming from Rust?

F# and Rust share the same design philosophy: `Option`, `Result`, pattern matching, discriminated unions, immutability by default, and no `null`. F# runs on .NET instead, has no borrow checker, and adds hot reload and interactive scripting.

You'll leave behind borrow checker fights over straightforward code, 45-second compile times for medium projects, no REPL, and reaching for Python whenever you want to explore data.

**What you'll notice right away:**
- `Option<'T>`, `Result<'T, 'E>`, and exhaustive pattern matching, just like Rust
- Records and DUs have structural equality by default, with no `#[derive(PartialEq)]` needed
- Hot reload: your running program patches itself on save, which Rust can't do
- `.fsx` scripts give you the interactive exploration that Rust has never had

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
