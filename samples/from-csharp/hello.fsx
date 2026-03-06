// ============================================================
//  🔷 → 🦅  Coming from C#? You're going to love this.
//  F# is C#'s cooler sibling. Same .NET. Half the code. Twice the fun.
//  Alt+Enter any expression. Results appear inline.
// ============================================================

// ── You already know .NET. You just don't need the ceremony. ──

// C#:
//   public class Person
//   {
//       public string Name { get; init; }
//       public int Age { get; init; }
//       public Person(string name, int age) { Name = name; Age = age; }
//       public override string ToString() => $"{Name}, age {Age}";
//   }
//
// F#:
type Person = { Name: string; Age: int }
// That's it. Structural equality, ToString, GetHashCode — all generated.
// And it's immutable by default. Because we're adults.

let alice = { Name = "Alice"; Age = 30 }
let olderAlice = { alice with Age = 31 }   // non-destructive update — alice unchanged
// Alt+Enter on olderAlice → { Name = "Alice"; Age = 31 } ✓

// ── Discriminated Unions: the enum you always wanted ──
// C#: abstract class Shape + Circle : Shape + Rectangle : Shape + switch expression
// F#:
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float
  | Triangle  of base': float * height: float

let area = function
  | Circle r           -> System.Math.PI * r * r
  | Rectangle (w, h)  -> w * h
  | Triangle (b, h)   -> 0.5 * b * h

// The compiler tells you if you forget a case.  No runtime surprises.
area (Circle 3.0)           // → 28.274... ✓
area (Rectangle (4.0, 5.0)) // → 20.0 ✓

// ── No null. Use Option<'T>. ──
// C#: string? name = null; if (name != null) ...
// F#: Option<string> — None or Some "value"

let greet (name: string option) =
  match name with
  | Some n -> $"Hello, {n}!"
  | None   -> "Hello, stranger!"

greet (Some "Bob")  // → "Hello, Bob!" ✓
greet None          // → "Hello, stranger!" ✓

// ── Result<'T, 'TError>: exceptions are for exceptional things ──
// C#: try/catch everywhere, or the Functional<T,E> pattern library
// F#: built into the language idiom
let divide a b =
  if b = 0 then Error "division by zero"
  else Ok (a / b)

match divide 10 2 with
| Ok v    -> printfn "Result: %d" v
| Error e -> printfn "Error: %s" e

// ── Pipelines: no more method chaining limitations ──
// C#: list.Where(x => x % 2 == 0).Select(x => x * x).Sum()
// F#: same concept, but you can pipe *any* function, not just LINQ extension methods
let sumOfEvenSquares =
  [1..10]
  |> List.filter (fun x -> x % 2 = 0)
  |> List.map    (fun x -> x * x)
  |> List.sum
// → 220 ✓

// ── Async: computation expressions, no stray awaits ──
// C#: async Task<int>, await, ConfigureAwait(false), CancellationToken everywhere
// F#: async { } blocks, let! for awaiting, Async.RunSynchronously to run
let fetchLength url = async {
  use client = new System.Net.Http.HttpClient()
  let! html = client.GetStringAsync(url) |> Async.AwaitTask
  return html.Length
}
// Async.RunSynchronously (fetchLength "https://example.com")

// ── Type inference: write less, get more ──
// C# 10+ has `var`, but you still need type annotations everywhere.
// F# infers almost everything:
let add a b = a + b           // (int -> int -> int) — inferred
let addFloat a b : float = a + b  // if you want float

// ── Interfaces still work — your existing .NET libraries just work ──
open System.Collections.Generic

let dict = Dictionary<string, int>()
dict["one"] <- 1
dict["two"] <- 2
dict |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.toList
// → [("one", 1); ("two", 2)] ✓

// ── LINQ operators you know, F# style ──
// C# LINQ → F# equivalent
//  .Where(pred)       → List.filter pred
//  .Select(proj)      → List.map proj
//  .Aggregate(f)      → List.fold f init
//  .Any(pred)         → List.exists pred
//  .All(pred)         → List.forall pred
//  .First(pred)       → List.find pred          (throws on miss)
//                     → List.tryFind pred       (returns Option)
//  .GroupBy(key)      → List.groupBy key
//  .OrderBy(key)      → List.sortBy key

// ── Hot reload with SageFs: the dotnet watch upgrade ──
// dotnet watch: rebuilds the whole project (~5-30s), restarts the process
// SageFs:       patches method pointers at runtime (~100ms), browser auto-refreshes
//               your Falco/ASP.NET app is live before your fingers leave the keyboard

// ── Migration cheatsheet ──
// public class Foo { ... }          → type Foo = { ... }  or  type Foo(...) = ...
// public interface IFoo { ... }     → type IFoo = interface ... end  (rarely needed)
// List<T>.ForEach(x => ...)         → List.iter (fun x -> ...) list
// var result = new Foo(...)         → let result = { ... }  or  Foo(...)
// int? x = null                     → let x: int option = None
// throw new Exception("msg")        → failwith "msg"  or  Error "msg"
// Console.WriteLine(x)              → printfn "%A" x
// string.Format("{0}", x)           → $"{x}"  (same interpolation syntax)
// async Task Foo() { ... }          → let foo () = async { ... }
// await foo()                       → let! result = foo()

// ── EXERCISES ──
// 1. Rewrite a C# class you know well as an F# record + DU
// 2. Replace a try/catch block with Result<'T, string>
// 3. Convert a LINQ chain to a |> pipeline
// ============================================================
