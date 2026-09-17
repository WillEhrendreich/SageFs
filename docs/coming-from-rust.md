# 🦀 Coming from Rust?

F# shares Rust's core ideas: `Option`, `Result`, pattern matching, discriminated unions, immutability by default, and no `null`. It runs on .NET, has no borrow checker, and adds a REPL, `.fsx` scripting, and hot reload.

You leave behind borrow-checker fights over straightforward code, slow compiles on medium projects, no REPL, and reaching for Python whenever you want to explore data.

**What you'll notice right away:**
- `Option<'T>`, `Result<'T, 'E>`, and exhaustive pattern matching, like Rust.
- Records and DUs get structural equality by default, with no `#[derive(PartialEq)]` needed.
- Hot reload: SageFs watches your files and re-evaluates on save, refreshing running web apps in the browser.
- `.fsx` scripts give you the interactive exploration Rust doesn't have.

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
