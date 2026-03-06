// ============================================================
//  🦀 → 🦅  Coming from Rust? You're going to feel the same love.
//  Ownership model aside, F# and Rust share the same design philosophy:
//  make illegal states unrepresentable. No GC pauses to worry about here,
//  but also — no borrow checker anxiety. Trade-offs are a thing.
//  Alt+Enter any expression. Results inline.
// ============================================================

// ── Enums with data: you'll recognize these immediately ──
// Rust: enum Shape { Circle { radius: f64 }, Rectangle { width: f64, height: f64 } }
// F#:
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float
  | Triangle  of base': float * height: float

// Pattern matching: exhaustive, compiler-checked — just like Rust
let area shape =
  match shape with
  | Circle r           -> System.Math.PI * r * r
  | Rectangle (w, h)  -> w * h
  | Triangle (b, h)   -> 0.5 * b * h

// Forget a case? Warning. Add a case without updating match? Warning.
// The compiler has your back here too.

// ── Option<T>: None / Some, exactly as you'd expect ──
// Rust: Option<T>  → Some(x) / None
// F#:   'T option  → Some x  / None  (no parens required)
let safeDivide a b =
  if b = 0 then None
  else Some (a / b)

match safeDivide 10 3 with
| Some v -> printfn "Got: %d" v
| None   -> printfn "Divided by zero"
// Same semantic contract as Rust.  Compiler forces you to handle None.

// ── Result<T, E>: Ok / Error, familiar territory ──
// Rust: Result<T, E> → Ok(t) / Err(e)
// F#:   Result<'T, 'E> → Ok t / Error e
let parsePositive (s: string) =
  match System.Int32.TryParse(s) with
  | true, n when n > 0 -> Ok n
  | true, _            -> Error "must be positive"
  | _                  -> Error "not a number"

match parsePositive "42" with
| Ok n    -> printfn "Parsed: %d" n
| Error e -> printfn "Bad: %s" e
// → "Parsed: 42" ✓

// ── Structs / Records ──
// Rust: struct Point { x: f64, y: f64 }
// F#:   type Point = { X: float; Y: float }
//       (or [<Struct>] for actual stack allocation)
type Point = { X: float; Y: float }

let origin = { X = 0.0; Y = 0.0 }
let moved  = { origin with X = 3.0 }   // non-destructive update, like Rust's ..origin

// ── Traits → Interfaces (rarely needed) ──
// Most of the time, functions handle composition better.
// When you need a trait-like contract:
type IAnimal =
  abstract member Sound: unit -> string

// ── Closures: same idea, different syntax ──
// Rust: let double = |x: i32| x * 2;
// F#:   let double = fun x -> x * 2
let double = fun x -> x * 2
let triple x = x * 3   // named functions are values too

// Higher-order functions — same mental model
let applyTwice f x = f (f x)
applyTwice double 3   // → 12 ✓
applyTwice triple 2   // → 18 ✓

// ── Iterators / pipelines ──
// Rust: vec.iter().filter(|&&x| x % 2 == 0).map(|&x| x * x).collect::<Vec<_>>()
// F#:
let result =
  [1..10]
  |> List.filter (fun x -> x % 2 = 0)
  |> List.map    (fun x -> x * x)
// → [4; 16; 36; 64; 100] ✓
// No collect(), no type annotations on the pipeline, no &&x lifetimes to think about.

// ── Traits for common behavior — implemented implicitly ──
// Rust: derive(Debug, Clone, PartialEq, Hash)
// F#:   records and DUs get structural equality, hashing, and ToString for free.
//       No derive attributes needed.

type Color = Red | Green | Blue
Red = Red      // → true  (structural equality, free)
Red = Blue     // → false ✓

// ── Lifetimes / borrowing: F# doesn't have this ──
// Pro: zero borrow checker errors. Zero lifetime annotations. Zero 'a 'b 'c soup.
// Con: GC does the memory management. Pauses exist (though .NET GC is quite good).
//      For most server-side and tooling work, you won't notice.
//      If you're writing a game loop, set your GC to Server mode.

// ── Concurrency: actors instead of Arc<Mutex<T>> ──
// Rust: Arc::new(Mutex::new(state)) to share mutable state across threads
// F#:   MailboxProcessor (actor model) — send messages, no shared mutable state
let counter =
  MailboxProcessor.Start(fun inbox ->
    let rec loop n = async {
      let! msg = inbox.Receive()
      match msg with
      | "inc"  -> return! loop (n + 1)
      | "get"  -> return! loop n
      | _      -> return! loop n
    }
    loop 0)

counter.Post("inc")
counter.Post("inc")
// No Mutex, no deadlock potential, no poisoned lock unwrap().

// ── Cargo vs dotnet ──
// Cargo: cargo build, cargo test, cargo add
// dotnet: dotnet build, dotnet test, dotnet add package <name>
//         nuget.org = crates.io (equally searchable, larger ecosystem)
// SageFs: dotnet build is mostly for CI — day-to-day, just hit save.

// ── Why F# if you love Rust? ──
// • Faster to write. Same ideas, less syntax.
// • .NET ecosystem is enormous (NuGet >> crates in some domains)
// • Hot reload: impossible in Rust, trivial in SageFs
// • Interactive scripting: .fsx files are Rust's "missing" REPL
// • Web apps (Falco), GUIs (Raylib), ML (ML.NET), data (DiffSharp)
// • Type providers: read a JSON/CSV/DB schema as F# types at compile time

// ── EXERCISES ──
// 1. Model a Rust-style state machine (states as DU, transitions as functions)
// 2. Write a Result-returning parser chain using Result.bind
// 3. Build a MailboxProcessor that manages a simple todo list
// ============================================================
