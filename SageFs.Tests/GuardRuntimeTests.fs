/// The runtime half of the guards: the cell a watchdog moves and the checks the patched code calls. A check throws on
/// the thread that was asked to stop and on no other, a stop is counted once however often it is asked for, and
/// whoever retires a cell lets its stop go.
module SageFs.Tests.GuardRuntimeTests

open System
open System.Threading
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features
open SageFs.Features.MemberEvaluation

/// What a thread that answers to `cell` sees when it checks, asked from another thread so the test's own thread is
/// never bound.
let private checkOn (cell: GuardCell) : exn option =
  let seen : exn option ref = ref None
  let body () =
    Guard.Bind cell
    try
      try Guard.Check()
      with e -> seen.Value <- Some e
    finally
      Guard.Unbind()
  let thread = Thread(ThreadStart body, GetterStackBytes)
  thread.Start()
  thread.Join TestTimeouts.patienceBrief |> Expect.isTrue "the checking thread ended"
  seen.Value

/// An operation on one of a few cells.
type private CellOp =
  | Request of int
  | Retire of int

let private cellOps : Arbitrary<CellOp list> =
  let cell = Gen.choose (0, 3)
  Gen.oneof [ Gen.map Request cell; Gen.map Retire cell ] |> Gen.listOf |> Arb.fromGen

[<Tests>]
let guardRuntimeTests =
  testList "guard runtime (cells and checks)" [

    testCase "WHY — a check does nothing on a thread that was not asked to stop" <| fun _ ->
      checkOn (GuardCell()) |> Expect.isNone "no exception"

    testCase "WHY — a check throws the guard's own exception on the thread that was asked to stop" <| fun _ ->
      let cell = GuardCell()
      Guard.RequestStop cell
      try
        match checkOn cell with
        | Some (:? GuardAbortedException) -> ()
        | other -> failtestf "expected a GuardAbortedException, got %A" other
      finally
        Guard.Retire cell

    testCase "WHY — a check on another thread is not stopped by someone else's stop" <| fun _ ->
      let stopped = GuardCell()
      Guard.RequestStop stopped
      try
        checkOn (GuardCell()) |> Expect.isNone "an unrelated cell is not stopped"
        // A thread bound to no cell at all: the test's own.
        Guard.Check()
      finally
        Guard.Retire stopped

    testCase "WHY — the stop stays set, so a catch-all that swallowed it is stopped again at the next check" <| fun _ ->
      let cell = GuardCell()
      Guard.RequestStop cell
      let thrown = ref 0
      let body () =
        Guard.Bind cell
        try
          for _ in 1 .. 3 do
            try Guard.Check()
            with :? GuardAbortedException -> thrown.Value <- thrown.Value + 1
        finally
          Guard.Unbind()
      let thread = Thread(ThreadStart body, GetterStackBytes)
      try
        thread.Start()
        thread.Join TestTimeouts.patienceBrief |> Expect.isTrue "ended"
        thrown.Value |> Expect.equal "every check threw" 3
      finally
        Guard.Retire cell

    testCase "WHY — a stop that was never asked for is not released, and one that was is released once" <| fun _ ->
      let neverAsked = GuardCell()
      neverAsked.Retire() |> Expect.equal "nothing to release" GuardCellMove.Unchanged
      let asked = GuardCell()
      asked.RequestStop() |> Expect.equal "the first ask counts" GuardCellMove.Changed
      asked.RequestStop() |> Expect.equal "the second does not" GuardCellMove.Unchanged
      asked.Retire() |> Expect.equal "released once" GuardCellMove.Changed
      asked.Retire() |> Expect.equal "and not twice" GuardCellMove.Unchanged
      asked.RequestStop() |> Expect.equal "a retired cell cannot be asked again" GuardCellMove.Unchanged
      asked.StopIsRequested |> Expect.equal "so it no longer stops anything" GuardCellStop.NotRequested

    testSequenced
    <| testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 } "WHY — the global count of stops is exactly the cells asked and not yet retired, whatever order they are asked in"
      (Prop.forAll cellOps (fun ops ->
        let cells = Array.init 4 (fun _ -> GuardCell())
        let before = Guard.Pending
        let asked = Collections.Generic.HashSet<int>()
        let mutable agreed = true
        for op in ops do
          match op with
          | Request i ->
            Guard.RequestStop cells.[i]
            match cells.[i].StopIsRequested with
            | GuardCellStop.Requested -> asked.Add i |> ignore
            | GuardCellStop.NotRequested -> ()
          | Retire i ->
            Guard.Retire cells.[i]
            asked.Remove i |> ignore
          agreed <- agreed && Guard.Pending = before + asked.Count
        for cell in cells do Guard.Retire cell
        agreed && Guard.Pending = before))
  ]
