/// Putting guards on what a getter can reach and taking them off again. A patch goes on when the first click wants a
/// method and comes off when the last lets go; guarded code computes what it computed; and a guarded loop is stopped by
/// the watchdog instead of running forever.
module SageFs.Tests.GuardPatcherTests

open System
open System.Reflection
open System.Threading
open Expecto
open Expecto.Flip
open HarmonyLib
open SageFs.Features
open SageFs.Features.MemberEvaluation
open SageFs.Tests.GuardFixtures

let private ownedId = "sagefs.getter-guards"

let private methodOf (t: Type) (name: string) : MethodBase =
  t.GetMethod(name, BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance) :> MethodBase

let private world = GuardReachability.worldFor [ typeof<Chain>.Assembly ] GuardPatcher.calleesOf

/// Whether our transpiler is on this method right now, asked of Harmony itself.
let private carriesGuards (m: MethodBase) : bool =
  match Harmony.GetPatchInfo m with
  | null -> false
  | info -> info.Transpilers |> Seq.exists (fun patch -> patch.owner = ownedId)

let private guardedNames (lease: GuardLease) : string list =
  match lease.Coverage with
  | GuardCoverage.Guarded covered -> covered.Methods
  | GuardCoverage.NotGuarded reason -> failtestf "expected guards, got %s" (NotGuardedReason.describe reason)

[<Tests>]
let guardPatcherTests =
  testList "guard patcher (putting guards on and taking them off)" [

    testCase "WHY — the getter and what it calls carry guards for the click, and carry none after it" <| fun _ ->
      let top = methodOf typeof<Solo> "Getter"
      let helper = methodOf typeof<Solo> "Helper"
      let lease = GuardPatcher.prepareWith world WalkBudget.product top
      try
        guardedNames lease |> Expect.hasLength "the getter and the helper" 2
        carriesGuards top |> Expect.isTrue "the getter is patched"
        carriesGuards helper |> Expect.isTrue "so is the helper"
        Solo.Getter 1 |> Expect.equal "guarded code computes what it computed" 12
      finally
        lease.Release()
      carriesGuards top |> Expect.isFalse "the getter is clean again"
      carriesGuards helper |> Expect.isFalse "so is the helper"
      Solo.Getter 1 |> Expect.equal "and still computes the same" 12

    testCase "WHY — releasing a lease twice is harmless" <| fun _ ->
      let top = methodOf typeof<Chain> "Top"
      let lease = GuardPatcher.prepareWith world WalkBudget.product top
      lease.Release()
      lease.Release()
      carriesGuards top |> Expect.isFalse "clean"

    testCase "WHY — two clicks that share a helper do not take each other's guards off" <| fun _ ->
      let helper = methodOf typeof<Shared> "Helper"
      let first = GuardPatcher.prepareWith world WalkBudget.product (methodOf typeof<Shared> "GetterA")
      let second = GuardPatcher.prepareWith world WalkBudget.product (methodOf typeof<Shared> "GetterB")
      try
        first.Release()
        carriesGuards helper |> Expect.isTrue "the second click still needs the helper guarded"
        carriesGuards (methodOf typeof<Shared> "GetterA") |> Expect.isFalse "the first click's own getter is clean"
      finally
        second.Release()
      carriesGuards helper |> Expect.isFalse "the last release takes it off"

    testCase "WHY — a guarded loop is stopped by the watchdog and not left running" <| fun _ ->
      let release = new ManualResetEventSlim(false)
      let inside = new ManualResetEventSlim(false)
      let getter = methodOf typeof<Spinners> "Getter"
      let lease = GuardPatcher.prepareWith world WalkBudget.product getter
      let cell = GuardCell()
      let outcome : Result<int, exn> ref = ref (Result.Error (InvalidOperationException "did not run"))
      let body () =
        Guard.Bind cell
        try
          try outcome.Value <- Result.Ok (Spinners.Getter(release, inside))
          with e -> outcome.Value <- Result.Error e
        finally
          Guard.Unbind()
      let thread = Thread(ThreadStart body, GetterStackBytes)
      try
        thread.Start()
        inside.Wait TestTimeouts.patienceBrief |> Expect.isTrue "the loop started"
        Guard.RequestStop cell
        thread.Join TestTimeouts.patienceBrief |> Expect.isTrue "the loop ended without being released"
        match outcome.Value with
        | Result.Error (:? GuardAbortedException) -> ()
        | other -> failtestf "expected the guard's own exception, got %A" other
      finally
        release.Set()
        thread.Join TestTimeouts.patienceBrief |> ignore
        Guard.Retire cell
        lease.Release()

    testCase "WHY — the same loop without guards is not stopped by the watchdog: the guard is what did it" <| fun _ ->
      let release = new ManualResetEventSlim(false)
      let inside = new ManualResetEventSlim(false)
      let cell = GuardCell()
      let thread = Thread(ThreadStart(fun () ->
        Guard.Bind cell
        try ControlSpinners.Getter(release, inside) |> ignore
        finally Guard.Unbind()), GetterStackBytes)
      try
        thread.Start()
        inside.Wait TestTimeouts.patienceBrief |> Expect.isTrue "the loop started"
        Guard.RequestStop cell
        thread.Join TestTimeouts.settle |> Expect.isFalse "still looping: nothing was woven into it"
      finally
        release.Set()
        thread.Join TestTimeouts.patienceBrief |> Expect.isTrue "it ends once released"
        Guard.Retire cell

    testCase "WHY — a getter with nothing to guard is not guarded, and says why" <| fun _ ->
      let nothing = GuardReachability.worldFor [] GuardPatcher.calleesOf
      let lease = GuardPatcher.prepareWith nothing WalkBudget.product (methodOf typeof<Chain> "Top")
      match lease.Coverage with
      | GuardCoverage.NotGuarded (NotGuardedReason.GetterSkipped (SkipReason.NotOurCode _)) -> ()
      | other -> failtestf "expected the getter to be skipped as not ours, got %A" other
      lease.Release()

    testCase "WHY — a property's getter is guarded through the property, on the value that is read" <| fun _ ->
      let lease = GuardPatcher.prepare (typeof<Square>.GetProperty "Side") (box (Square 3))
      try
        match lease.Coverage with
        | GuardCoverage.Guarded covered -> covered.Methods |> Expect.isNonEmpty "the getter is guarded"
        | other -> failtestf "expected guards, got %A" other
      finally
        lease.Release()
  ]
