/// What one eval costs in a session that has already run many: every earlier copy of a redefined function is
/// re-pointed (`FSI_0001.Mod.fn`, `FSI_0002.Mod.fn`, ...), so the number of detours grows with the session. What
/// a user reads (the log, the report that crosses the process boundary, the "N of M" counts) must grow with
/// the number of DISTINCT functions the eval changed, never with the number of copies.
module SageFs.Tests.HotReloadCopiesTests

open System
open System.Reflection
open System.Reflection.Emit
open Expecto
open Expecto.Flip
open SageFs.Middleware
open SageFs.Middleware.HotReloadCore
open SageFs.Features

/// A logger that keeps every call, whatever its level.
type private RecordingLogger() =
  let calls = Collections.Generic.List<string>()
  member _.Calls = lock calls (fun () -> List.ofSeq calls)
  interface SageFs.Utils.ILogger with
    member _.LogInfo m = lock calls (fun () -> calls.Add m)
    member _.LogDebug m = lock calls (fun () -> calls.Add m)
    member _.LogWarning m = lock calls (fun () -> calls.Add m)
    member _.LogError m = lock calls (fun () -> calls.Add m)

/// The functions an eval redefines, named as the session names them.
let private functionNames = [ "Fixture.alpha"; "Fixture.beta"; "Fixture.gamma"; "Fixture.delta"; "Fixture.epsilon" ]

/// One FSI generation: a type of its own in one dynamic assembly, holding every function. Real methods in a real
/// dynamic assembly, as FSI emits them, so the detours are the production ones.
let private generations (count: int) : MethodInfo list list =
  let assemblyName = sprintf "HotReloadCopiesFixture_%s" (Guid.NewGuid().ToString "N")
  let assembly = AssemblyBuilder.DefineDynamicAssembly(AssemblyName assemblyName, AssemblyBuilderAccess.Run)
  let modul = assembly.DefineDynamicModule assemblyName
  [ for generation in 1..count do
      let builder = modul.DefineType(sprintf "FSI_%04d" generation, TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
      for name in functionNames do
        let bare = name.Substring(name.LastIndexOf '.' + 1)
        let m = builder.DefineMethod(bare, MethodAttributes.Public ||| MethodAttributes.Static, typeof<int>, [||])
        let il = m.GetILGenerator()
        il.Emit(OpCodes.Ldc_I4, generation)
        il.Emit OpCodes.Ret
      let created = builder.CreateType()
      yield [ for name in functionNames -> created.GetMethod(name.Substring(name.LastIndexOf '.' + 1)) ] ]

/// A plan that re-points `copies` earlier generations of every function onto the newest one.
let private planOf (copies: int) : DetourPlan =
  let all = generations (copies + 1)
  let newest = List.last all
  let methodOf (name: string) (info: MethodInfo) : Method = { MethodInfo = info; FullName = name }
  let functions =
    [ for older in List.take copies all do
        for name, (olderInfo, newerInfo) in List.zip functionNames (List.zip older newest) ->
          methodOf name olderInfo, methodOf name newerInfo ]
  { Functions = functions; MutableBindings = []; Declined = [] }

let private apply (copies: int) : DetourReport * string list =
  let logger = RecordingLogger()
  let report = applyDetourPlan (logger :> SageFs.Utils.ILogger) Map.empty (planOf copies) []
  report, logger.Calls

let private sessionLengths = [ 1; 5; 40 ]

[<Tests>]
let copiesTests =
  testList "HotReloadCore applyDetourPlan across copies" [
    for copies in sessionLengths do
      testCase (sprintf "WHY — applyDetourPlan — %d earlier copies of each function are re-pointed and each function is named once in the report" copies) <| fun _ ->
        let report, _ = apply copies
        report.Redirected |> Expect.equal "one entry per function, first-seen order" functionNames
        report.Ineffective |> List.distinct |> Expect.equal "the ineffective list repeats nothing" report.Ineffective
        report.RedirectedFromCompiled |> List.distinct |> Expect.equal "the compiled list repeats nothing" report.RedirectedFromCompiled
        report.Failures |> Expect.isEmpty "every copy was re-pointed"

      testCase (sprintf "WHY — applyDetourPlan — %d earlier copies write a log bounded by the functions changed, not by the copies" copies) <| fun _ ->
        let report, calls = apply copies
        let distinctFunctions = report.Redirected |> List.distinct |> List.length
        let bound = distinctFunctions + List.length report.Failures + 1
        match List.length calls <= bound with
        | true -> ()
        | false -> failtestf "%d log calls for %d copies of %d functions (bound %d); first lines: %A" (List.length calls) copies distinctFunctions bound (List.truncate 3 calls)

      testCase (sprintf "WHY — applyDetourPlan — %d earlier copies leave the counts a user reads where one copy puts them" copies) <| fun _ ->
        let report, _ = apply copies
        let watched =
          PatchConfirmation.watchedOfRedirected report.Redirected report.Probes
          |> List.filter (fun w -> not (List.isEmpty w.Probes))
        ReloadOutcome.ReloadOutcome.ofPatchCounts (List.length watched) (List.length report.Redirected) []
        |> Expect.equal "five functions patched of five considered, as with a single copy" (ReloadOutcome.ReloadOutcome.ofPatchCounts 5 5 [])

    testCase "WHY — applyDetourPlan — the copies of one function all reach the newest body because a running app may hold any of them" <| fun _ ->
      let all = generations 6
      let newest = List.last all
      let olders = List.take 5 all
      let plan =
        { Functions =
            [ for older in olders ->
                { MethodInfo = List.head older; FullName = List.head functionNames },
                { MethodInfo = List.head newest; FullName = List.head functionNames } ]
          MutableBindings = []
          Declined = [] }
      applyDetourPlan (RecordingLogger() :> SageFs.Utils.ILogger) Map.empty plan [] |> ignore
      olders
      |> List.map (fun older -> (List.head older).Invoke(null, [||]))
      |> Expect.equal "every old copy now answers as the newest" (List.replicate 5 (box 6))
  ]
