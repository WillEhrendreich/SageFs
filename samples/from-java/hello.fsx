// ============================================================
//  ☕ → 🦅  Coming from Java? Sit down, take a breath.
//  You don't need a framework. You don't need a factory.
//  You don't need an AbstractSingletonProxyFactoryBean.
//  You just need F#.
//  Alt+Enter any expression. Results appear inline.
// ============================================================

// ── Let's begin with the simplest possible thing ──
// Java:
//   public class HelloWorld {
//       public static void main(String[] args) {
//           System.out.println("Hello, World!");
//       }
//   }
//
// F#:
printfn "Hello, World!"   // Alt+Enter → printed ✓
// That's the whole program.  No class.  No main.  No public static void.

// ── Data classes: half the boilerplate ──
// Java 14+ record:  record Person(String name, int age) {}
// F#:
type Person = { Name: string; Age: int }
// Structural equality, hashCode, toString — all included.
// Immutable by default.  No getters needed.  No Lombok.

let alice = { Name = "Alice"; Age = 30 }
let older  = { alice with Age = 31 }   // copy with one field changed
// Alt+Enter on older → { Name = "Alice"; Age = 31 } ✓

// ── Sealed classes are Discriminated Unions ──
// Java 17+ sealed interface Shape permits Circle, Rectangle, Triangle {}
// F#:
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float
  | Triangle  of base': float * height: float

// Pattern matching — the switch expression, but complete and checked:
let area shape =
  match shape with
  | Circle r           -> System.Math.PI * r * r
  | Rectangle (w, h)  -> w * h
  | Triangle (b, h)   -> 0.5 * b * h
// Forget a case? Compiler warning.  No runtime MatchException.

area (Circle 5.0)           // → 78.54... ✓
area (Rectangle (3.0, 4.0)) // → 12.0 ✓

// ── Optional: Java Optional<T> → F# Option<'T> ──
// Java: Optional.ofNullable(x).map(f).orElse(default)
// F#:
let safeDivide a b =
  if b = 0 then None
  else Some (a / b)

match safeDivide 10 3 with
| Some v -> printfn "Result: %d" v
| None   -> printfn "Cannot divide by zero"
// No NullPointerException in idiomatic F# code. Pure F# doesn't have null.

// ── Generic methods without the <T extends Comparable<? super T>> noise ──
// F# type inference handles it:
let maxOf a b = if a > b then a else b   // works for int, float, string — all of them
maxOf 3 7       // → 7 ✓
maxOf "apple" "banana"  // → "banana" ✓

// ── Streams → List pipelines ──
// Java: list.stream().filter(x -> x % 2 == 0).map(x -> x * x).collect(Collectors.toList())
// F#:
let result =
  [1..10]
  |> List.filter (fun x -> x % 2 = 0)
  |> List.map    (fun x -> x * x)
// → [4; 16; 36; 64; 100] ✓
// No .stream(), no .collect(), no Collectors.toList()

// ── No checked exceptions ──
// Java: throws IOException, throws SQLException, throws ...
// F#: use Result<'T, 'TError> when you want to signal failure:
let readFileLines path =
  try Ok (System.IO.File.ReadAllLines(path))
  with ex -> Error ex.Message
// Callers must handle both cases.  Compiler-enforced.

// ── Interfaces: still there, rarely needed ──
// Java: everything is an interface + class + impl.  F# just uses functions.
// But when you need them:
type IAnimal =
  abstract member Speak: unit -> string

type Dog() =
  interface IAnimal with
    member _.Speak() = "Woof!"

let dog = Dog() :> IAnimal
dog.Speak()   // → "Woof!" ✓

// ── No verbose generics ──
// Java: Map<String, List<Optional<Integer>>>
// F#:  Map<string, int list option>   (and you'd use a DU anyway)

// ── Async: Tasks without the thread management ceremony ──
// Java: CompletableFuture<String>.thenApply(f).thenCompose(g).exceptionally(e -> ...)
// F#:
let fetchAsync url = async {
  use client = new System.Net.Http.HttpClient()
  let! content = client.GetStringAsync(url) |> Async.AwaitTask
  return content.Length
}
// Async.RunSynchronously (fetchAsync "https://example.com")

// ── No Spring. No XML. No annotations. Just a function. ──
// Spring Boot HelloWorld requires:
//   @SpringBootApplication, @RestController, @RequestMapping,
//   @GetMapping, ResponseEntity, a pom.xml, and about 40 seconds to compile.
//
// SageFs + Falco HelloWorld:
//   #r "nuget: Falco"
//   open Falco
//   webHost [||] {
//     endpoints [ get "/" (Response.ofPlainText "Hello, World!") ]
//   }
// That's it.  And it hot-reloads on save.

// ── Build tool comparison ──
// Maven:  pom.xml (XML), 100-line configs, 10-minute builds
// Gradle: build.gradle (Groovy or Kotlin), still verbose, still slow
// F#:     .fsproj (MSBuild, but ~10 lines), `dotnet build` in seconds
//         SageFs:  no rebuild at all — hot-patches at runtime

// ── Migration cheatsheet ──
// public class Foo { private String x; }     → type Foo = { X: string }
// public interface IFoo { void bar(); }      → type IFoo = abstract member Bar: unit -> unit
// List<String> list = new ArrayList<>()      → let list = ResizeArray<string>()  or  []
// Optional<T>.empty()                        → None
// Optional.of(x)                             → Some x
// x != null ? x.foo() : default             → x |> Option.map (fun x -> x.Foo())
// throw new IllegalArgumentException("msg") → failwith "msg"  or  Error "msg"
// System.out.println(x)                      → printfn "%A" x
// x instanceof Foo f                         → match x with :? Foo as f -> ...

// ── EXERCISES ──
// 1. Model a bank account as a DU: Open, Closed, Frozen (with balance)
// 2. Write a pipeline equivalent of this Java stream:
//      list.stream().filter(s -> s.startsWith("A")).sorted().distinct().collect(toList())
// 3. Replace a try/catch block with a Result-returning function
// ============================================================
