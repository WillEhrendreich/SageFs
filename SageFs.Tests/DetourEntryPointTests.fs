/// The reflection scan that finds the detour library's entry point is a cost per DETOUR if it is not remembered, and
/// an eval makes one detour per earlier copy of every function it changes.
module SageFs.Tests.DetourEntryPointTests

open System
open System.Reflection
open System.Reflection.Emit
open Expecto
open Expecto.Flip
open SageFs.Middleware.HotReloadCore

let private quiet : SageFs.Utils.ILogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

/// `count` static methods in a dynamic assembly, each returning its own number.
let private methods (count: int) : MethodInfo list =
  let name = sprintf "DetourEntryPointFixture_%s" (Guid.NewGuid().ToString "N")
  let assembly = AssemblyBuilder.DefineDynamicAssembly(AssemblyName name, AssemblyBuilderAccess.Run)
  let modul = assembly.DefineDynamicModule name
  let builder = modul.DefineType("Holder", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  for i in 0 .. count - 1 do
    let m = builder.DefineMethod(sprintf "f%d" i, MethodAttributes.Public ||| MethodAttributes.Static, typeof<int>, [||])
    let il = m.GetILGenerator()
    il.Emit(OpCodes.Ldc_I4, i)
    il.Emit OpCodes.Ret
  let created = builder.CreateType()
  [ for i in 0 .. count - 1 -> created.GetMethod(sprintf "f%d" i) ]

let private detourCount = 50

[<Tests>]
let entryPointTests =
  testList "HotReloadCore detour entry point" [
    testCase "WHY — detourMethod — the entry point is looked up once however many detours follow, because the scan is over every type of the merged Harmony assembly" <| fun _ ->
      let all = methods (detourCount + 1)
      let target = List.last all
      let before = HarmonyDetour.lookups ()
      for older in List.take detourCount all do
        detourMethod quiet older target |> ignore
      let lookups = HarmonyDetour.lookups () - before
      match lookups <= 1L with
      | true -> ()
      | false -> failtestf "%d reflection lookups for %d detours" lookups detourCount

    testCase "WHY — detourMethod — the remembered entry point still re-points the old method onto the new one" <| fun _ ->
      let all = methods 3
      let older, newer = all.[0], all.[2]
      detourMethod quiet older newer |> Expect.equal "the detour landed" DetourApplied.Redirected
      older.Invoke(null, [||]) |> Expect.equal "the old method answers as the new one" (box 2)
  ]
