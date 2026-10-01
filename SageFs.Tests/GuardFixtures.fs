/// Code for the guard tests to look at, patch and run. Everything that is called is `NoInlining`, so what the compiler
/// calls is what the JIT calls, and a patch on a method is a patch on every call to it.
module SageFs.Tests.GuardFixtures

open System
open System.Runtime.CompilerServices

/// A chain of calls three deep.
[<AbstractClass; Sealed>]
type Chain =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Leaf(n: int) : int = n + 1
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Middle(n: int) : int = Chain.Leaf n + 1
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Top(n: int) : int = Chain.Middle n + 1

/// Two methods that call each other.
[<AbstractClass; Sealed>]
type Mutual =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Ping(n: int) : int = match n with | 0 -> 0 | _ -> Mutual.Pong(n - 1)
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Pong(n: int) : int = match n with | 0 -> 0 | _ -> Mutual.Ping(n - 1)

/// A call into the framework.
[<AbstractClass; Sealed>]
type CallsFramework =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Go(s: string) : int = String.Concat(s, "x").Length

/// A call through an abstract method.
[<AbstractClass>]
type Shape() =
  abstract Area: unit -> int

type Square(n: int) =
  inherit Shape()
  override _.Area() = n * n
  member _.Side : int = n

[<AbstractClass; Sealed>]
type CallsAbstract =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Go(shape: Shape) : int = shape.Area()

/// A call through an interface.
type IThing =
  abstract Weigh: unit -> int

type Thing() =
  interface IThing with
    member _.Weigh() = 7

[<AbstractClass; Sealed>]
type CallsInterface =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Go(thing: IThing) : int = thing.Weigh()

/// A closure handed to a library function: the closure's body only runs because something made it.
[<AbstractClass; Sealed>]
type MakesClosure =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Go(n: int) : int = [ 1; 2; 3 ] |> List.map (fun x -> x + n) |> List.sum

/// A generic method and a caller.
[<AbstractClass; Sealed>]
type Generics =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Id<'a>(x: 'a) : 'a = x
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Use(n: int) : int = Generics.Id n

/// What the C# and F# compilers make for an `async` method, by hand, marked as the compiler marks its own.
[<CompilerGenerated>]
type FakeMachine() =
  interface IAsyncStateMachine with
    member _.MoveNext() = ()
    member _.SetStateMachine(_) = ()

/// A getter that makes a state machine and runs it.
[<AbstractClass; Sealed>]
type BuildsMachine =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Go() : int =
    let machine = FakeMachine()
    (machine :> IAsyncStateMachine).MoveNext()
    1

/// A getter that builds a task, so a state machine is reachable from it.
[<AbstractClass; Sealed>]
type MakesTask =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Go(n: int) : int = (task { return n + 1 }).Result

/// Loops until released, announcing when it is inside the loop. What the guards must be able to stop.
[<AbstractClass; Sealed>]
type Spinners =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Spin(release: System.Threading.ManualResetEventSlim, inside: System.Threading.ManualResetEventSlim) : int =
    inside.Set()
    let mutable i = 0
    while not release.IsSet do
      i <- i + 1
    i
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Getter(release: System.Threading.ManualResetEventSlim, inside: System.Threading.ManualResetEventSlim) : int =
    Spinners.Spin(release, inside) + 1

/// Shared helper, so two clicks can want the same method.
[<AbstractClass; Sealed>]
type Shared =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Helper(n: int) : int = n + 1
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member GetterA(n: int) : int = Shared.Helper n + 10
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member GetterB(n: int) : int = Shared.Helper n + 20

/// Each case that hot-reload-detours a method gets its own, because a detour cannot be undone in a running process.
/// `Orig` is what the getter calls, `Repl` is what hot reload points it at.
[<AbstractClass; Sealed>]
type Reloadable1 =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Orig() : int = 42
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Repl() : int = 99
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Getter() : int = Reloadable1.Orig()

[<AbstractClass; Sealed>]
type Reloadable2 =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Orig() : int = 42
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Repl() : int = 99
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Getter() : int = Reloadable2.Orig()

[<AbstractClass; Sealed>]
type Reloadable3 =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Orig() : int = 42
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Repl() : int = 99
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Getter() : int = Reloadable3.Orig()

[<AbstractClass; Sealed>]
type Reloadable4 =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Orig() : int = 42
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Repl() : int = 99

/// A method reloaded twice: Orig to Repl, then Repl to Newest.
[<AbstractClass; Sealed>]
type Reloadable5 =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Orig() : int = 42
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Repl() : int = 99
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Newest() : int = 7
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Getter() : int = Reloadable5.Orig()

/// The same loop, never patched by any case, for the control that shows it is the guard that stops the other.
[<AbstractClass; Sealed>]
type ControlSpinners =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Spin(release: System.Threading.ManualResetEventSlim, inside: System.Threading.ManualResetEventSlim) : int =
    inside.Set()
    let mutable i = 0
    while not release.IsSet do
      i <- i + 1
    i
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Getter(release: System.Threading.ManualResetEventSlim, inside: System.Threading.ManualResetEventSlim) : int =
    ControlSpinners.Spin(release, inside) + 1

/// A getter and one helper that only the first patcher case touches.
[<AbstractClass; Sealed>]
type Solo =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Helper(n: int) : int = n + 1
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Getter(n: int) : int = Solo.Helper n + 10

/// Two methods the ledger case records against by hand. Nothing ever detours them.
[<AbstractClass; Sealed>]
type LedgerOnly =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Orig() : int = 1
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Repl() : int = 2
