/// Guards and hot reload on the same method. This is the one real design risk of layer 2: patching a method that hot
/// reload has already detoured replaces the detour with the original at once, and taking the patch off does not put the
/// detour back, so the user's reloaded code is gone. The fence: a detoured method is never patched. The method its entry
/// jumps to is, when that is known, and the click says so when it is not. A detour made while a click holds guards takes
/// them off first. Every case here reads the result of the real `detourMethod`.
module SageFs.Tests.GuardCoexistenceTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open HarmonyLib
open SageFs.Features
open SageFs.Middleware.HotReloadCore
open SageFs.Tests.GuardFixtures

let private logger : SageFs.Utils.ILogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private methodOf (t: Type) (name: string) : MethodBase =
  t.GetMethod(name, BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance) :> MethodBase

let private world = GuardReachability.worldFor [ typeof<Chain>.Assembly ] GuardPatcher.calleesOf

/// What hot reload does for one function: detour it, then say where it went (as `applyDetourPlan` does).
let private reload (older: MethodBase) (newer: MethodBase) : unit =
  detourMethod logger older newer |> ignore
  DetourLedger.recordBody older newer

let private ownsGuards (m: MethodBase) : bool =
  match Harmony.GetPatchInfo m with
  | null -> false
  | info -> info.Transpilers |> Seq.exists (fun patch -> patch.owner = "sagefs.getter-guards")

let private covered (lease: GuardLease) : GuardedMethods =
  match lease.Coverage with
  | GuardCoverage.Guarded found -> found
  | GuardCoverage.NotGuarded reason -> failtestf "expected guards, got %s" (NotGuardedReason.describe reason)

[<Tests>]
let guardCoexistenceTests =
  testList "guards and hot reload on one method" [

    testCase "WHY - the hazard itself: a Harmony patch put on a detoured method and taken off puts the old code back over the detour" <| fun _ ->
      let orig = methodOf typeof<Reloadable4> "Orig"
      let repl = methodOf typeof<Reloadable4> "Repl"
      let harmony = Harmony "guard-coexistence-hazard"
      detourMethod logger orig repl |> ignore
      Reloadable4.Orig() |> Expect.equal "the detour is live" 99
      let weave = HarmonyMethod(typeof<GuardTranspiler>.GetMethod "Weave")
      harmony.Patch(orig, transpiler = weave) |> ignore
      harmony.Unpatch(orig, HarmonyPatchType.Transpiler, harmony.Id)
      Reloadable4.Orig() |> Expect.equal "the original is back: the reload is lost" 42

    testCase "WHY - the ledger says nothing for a method hot reload never touched" <| fun _ ->
      DetourLedger.resolve (methodOf typeof<Chain> "Top") |> Expect.equal "untouched" Detour.NotDetoured

    testCase "WHY - marking a method detoured without a body refuses it, and recording the body says where it went" <| fun _ ->
      let older = methodOf typeof<LedgerOnly> "Orig"
      let body = methodOf typeof<LedgerOnly> "Repl"
      DetourLedger.markDetoured older
      DetourLedger.resolve older |> Expect.equal "known to be detoured, destination unknown" Detour.DetouredElsewhere
      DetourLedger.recordBody older body
      DetourLedger.resolve older |> Expect.equal "now it says where" (Detour.DetouredTo body)
      DetourLedger.markDetoured older
      DetourLedger.resolve older |> Expect.equal "a new detour forgets the old destination until it is recorded" Detour.DetouredElsewhere

    testCase "WHY - a getter that calls a reloaded method is guarded through the new body, and the reload survives the click" <| fun _ ->
      let orig = methodOf typeof<Reloadable1> "Orig"
      let repl = methodOf typeof<Reloadable1> "Repl"
      reload orig repl
      Reloadable1.Getter() |> Expect.equal "the reload is live before the click" 99
      let lease = GuardPatcher.prepareWith world WalkBudget.product (methodOf typeof<Reloadable1> "Getter")
      try
        let found = covered lease
        found.Methods |> Expect.contains "the new body is guarded" (GuardReachability.nameOf repl)
        found.Methods |> List.contains (GuardReachability.nameOf orig) |> Expect.isFalse "the detoured method is not patched"
        ownsGuards orig |> Expect.isFalse "nothing of ours is on the detoured entry"
        Reloadable1.Getter() |> Expect.equal "guarded, it still runs the reloaded code" 99
      finally
        lease.Release()
      Reloadable1.Orig() |> Expect.equal "after the click the detour is intact" 99
      Reloadable1.Getter() |> Expect.equal "and so is the getter" 99
      ownsGuards repl |> Expect.isFalse "the new body is clean again"

    testCase "WHY - a method hot reload re-pointed with no body known is refused, said on the row, and left alone" <| fun _ ->
      let orig = methodOf typeof<Reloadable2> "Orig"
      let repl = methodOf typeof<Reloadable2> "Repl"
      detourMethod logger orig repl |> ignore
      let lease = GuardPatcher.prepareWith world WalkBudget.product (methodOf typeof<Reloadable2> "Getter")
      try
        let found = covered lease
        found.Skipped
        |> List.exists (fun skip -> skip.Reason = SkipReason.DetouredByHotReload && skip.Method = GuardReachability.nameOf orig)
        |> Expect.isTrue "the row says hot reload re-pointed it"
        ownsGuards orig |> Expect.isFalse "and nothing was put on it"
      finally
        lease.Release()
      Reloadable2.Orig() |> Expect.equal "the detour is intact" 99

    testCase "WHY - a reload that lands while a click holds guards takes them off first, so the reload is not undone" <| fun _ ->
      let orig = methodOf typeof<Reloadable3> "Orig"
      let repl = methodOf typeof<Reloadable3> "Repl"
      let lease = GuardPatcher.prepareWith world WalkBudget.product (methodOf typeof<Reloadable3> "Getter")
      try
        ownsGuards orig |> Expect.isTrue "the click guarded the method before any reload"
        reload orig repl
        ownsGuards orig |> Expect.isFalse "hot reload took our patch off before it detoured"
        Reloadable3.Orig() |> Expect.equal "the reload took" 99
      finally
        lease.Release()
      Reloadable3.Orig() |> Expect.equal "releasing the click's lease does not undo it" 99

    testCase "WHY - a method reloaded twice is guarded through the newest body" <| fun _ ->
      let orig = methodOf typeof<Reloadable5> "Orig"
      let repl = methodOf typeof<Reloadable5> "Repl"
      let newest = methodOf typeof<Reloadable5> "Newest"
      reload orig repl
      reload repl newest
      let lease = GuardPatcher.prepareWith world WalkBudget.product (methodOf typeof<Reloadable5> "Getter")
      try
        let found = covered lease
        found.Methods |> Expect.contains "the newest body is guarded" (GuardReachability.nameOf newest)
        found.Methods |> List.contains (GuardReachability.nameOf orig) |> Expect.isFalse "not the first"
        found.Methods |> List.contains (GuardReachability.nameOf repl) |> Expect.isFalse "not the second"
      finally
        lease.Release()
      Reloadable5.Getter() |> Expect.equal "the chain of reloads still ends at the newest" 7
  ]
