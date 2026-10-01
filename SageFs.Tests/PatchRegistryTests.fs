/// Who holds which method, and when a patch goes on and comes off. The registry is the rule; the backend that really
/// patches is a recording fake here, so every order of leases is settled without patching real code. (The real backend,
/// Harmony, is checked in the patcher cases.)
module SageFs.Tests.PatchRegistryTests

open System
open System.Collections.Generic
open System.Reflection
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features
open SageFs.Tests.GuardFixtures

/// A backend that remembers what is patched, and how many times each call happened.
type private Recording() =
  let patched = HashSet<string>()
  member val Patches = 0 with get, set
  member val Unpatches = 0 with get, set
  member val Refuse : string = "" with get, set
  member _.Patched = patched
  member this.Backend : PatchBackend =
    { Patch =
        fun m ->
          match this.Refuse with
          | "" ->
            this.Patches <- this.Patches + 1
            patched.Add m.Name |> ignore
            PatchAttempt.Patched
          | detail -> PatchAttempt.PatchFailed detail
      Unpatch =
        fun m ->
          this.Unpatches <- this.Unpatches + 1
          patched.Remove m.Name |> ignore }

let private methodOf (name: string) : MethodBase =
  typeof<Shared>.GetMethod(name, BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static) :> MethodBase

let private helper = methodOf "Helper"
let private getterA = methodOf "GetterA"
let private getterB = methodOf "GetterB"

let private registry (recording: Recording) = PatchRegistry(recording.Backend, Ledger())

/// An operation on the registry, over two leases and three methods.
type private Op =
  | Acquire of lease: int * method: int
  | Release of lease: int * method: int

let private pool = [| helper; getterA; getterB |]

let private ops : Arbitrary<Op list> =
  let lease = Gen.choose (1, 3)
  let method = Gen.choose (0, 2)
  Gen.oneof [ Gen.map2 (fun l m -> Acquire (l, m)) lease method; Gen.map2 (fun l m -> Release (l, m)) lease method ]
  |> Gen.listOf
  |> Arb.fromGen

[<Tests>]
let patchRegistryTests =
  testList "patch registry (who holds which method)" [

    testCase "WHY - the first lease patches a method, a second lease shares the patch, and it comes off with the last" <| fun _ ->
      let recording = Recording()
      let registry = registry recording
      registry.Acquire(1, helper) |> Expect.equal "first holds it" Acquired.Held
      registry.Acquire(2, helper) |> Expect.equal "second shares it" Acquired.Held
      recording.Patches |> Expect.equal "patched once" 1
      registry.Release(1, helper)
      recording.Patched |> Expect.contains "still patched for the second lease" "Helper"
      registry.Release(2, helper)
      recording.Unpatches |> Expect.equal "unpatched once, by the last" 1
      registry.HeldCount |> Expect.equal "nothing held" 0

    testCase "WHY - releasing a method a lease does not hold, or releasing twice, changes nothing" <| fun _ ->
      let recording = Recording()
      let registry = registry recording
      registry.Release(9, helper)
      registry.Acquire(1, helper) |> ignore
      registry.Release(1, helper)
      registry.Release(1, helper)
      recording.Unpatches |> Expect.equal "unpatched exactly once" 1

    testCase "WHY - a method hot reload re-pointed is refused and never patched" <| fun _ ->
      let recording = Recording()
      let ledger = Ledger()
      let registry = PatchRegistry(recording.Backend, ledger)
      ledger.MarkDetoured helper
      registry.Acquire(1, helper) |> Expect.equal "refused, said so" (Acquired.Refused SkipReason.DetouredByHotReload)
      recording.Patches |> Expect.equal "nothing was patched" 0

    testCase "WHY - a reload takes the patch off whoever holds it, nothing goes back on, and the holders' later releases are no-ops" <| fun _ ->
      let recording = Recording()
      let registry = registry recording
      registry.Acquire(1, helper) |> ignore
      registry.Acquire(2, helper) |> ignore
      registry.ReleaseBeforeDetour helper
      recording.Patched |> Expect.isEmpty "the patch is off"
      registry.Acquire(3, helper) |> Expect.equal "and cannot go back on" (Acquired.Refused SkipReason.DetouredByHotReload)
      registry.Release(1, helper)
      registry.Release(2, helper)
      recording.Unpatches |> Expect.equal "unpatched once, by the reload" 1

    testCase "WHY - a patch the backend refuses is not held, and says what the backend said" <| fun _ ->
      let recording = Recording()
      recording.Refuse <- "no body to patch"
      let registry = registry recording
      registry.Acquire(1, helper) |> Expect.equal "refused" (Acquired.Refused (SkipReason.PatchRefused "no body to patch"))
      registry.HeldCount |> Expect.equal "nothing held" 0

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 300 } "WHY - a method is patched exactly while some lease holds it, whatever order the leases come and go in"
      (Prop.forAll ops (fun list ->
        let recording = Recording()
        let registry = registry recording
        let model = Dictionary<int, HashSet<int>>()
        let holders (m: int) =
          match model.TryGetValue m with
          | true, found -> found
          | false, _ ->
            let made = HashSet<int>()
            model.[m] <- made
            made
        let mutable agreed = true
        for op in list do
          match op with
          | Acquire (lease, m) ->
            registry.Acquire(lease, pool.[m]) |> ignore
            (holders m).Add lease |> ignore
          | Release (lease, m) ->
            registry.Release(lease, pool.[m])
            (holders m).Remove lease |> ignore
          for m in 0 .. 2 do
            agreed <- agreed && (recording.Patched.Contains pool.[m].Name = ((holders m).Count > 0))
        agreed && recording.Patches - recording.Unpatches = recording.Patched.Count))
  ]
