/// The outcome gate for a function the compiler inlined into its caller.
///
/// A save re-points the old function at the new body. When the old function was
/// inlined into its caller, the caller carries its own copy of the old body and
/// never enters the function whose entry point was re-pointed. Every signal a
/// save had (the detour landed, the app "holds" the old copy) says success, and
/// the app keeps serving the old result.
///
/// This test drives the real path: a real compiled callee and caller (this
/// assembly), a real dynamically emitted save, the real `Agent` applying a real
/// Harmony detour, and the real planner decision. Only the F# compile of the
/// save is replaced by IL emission, the same way `HostAgentTests` does it.
module SageFs.Tests.InlinedCalleeOutcomeTests

open System
open System.Reflection
open System.Reflection.Emit
open System.Runtime.CompilerServices
open Expecto
open Expecto.Flip
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning
open SageFs.HostAgent

/// The compiled "app". `inlinedCalleeProbe` is `inline`, so the F# compiler
/// copies its body into `renderWithInlinedCallee` at compile time: the caller
/// never calls the method the detour re-points. That is deterministic, unlike
/// `AggressiveInlining`, which a tier-0 JIT ignores.
module InlinedCalleeFixture =
  let inline inlinedCalleeProbe (n: int) : int = n + 1

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  let renderWithInlinedCallee (n: int) : int = inlinedCalleeProbe n * 10

let private fixtureAssemblyPath = Assembly.GetExecutingAssembly().Location

/// A dynamically emitted re-eval of `InlinedCalleeFixture.inlinedCalleeProbe`
/// whose body is `n + 1000`, shaped like FSI's own flattened re-emit: the type
/// name survives without its namespace, so its name is a suffix of the compiled
/// copy's.
let private saveOfInlinedCallee () : Assembly =
  let asm = AssemblyBuilder.DefineDynamicAssembly(AssemblyName("sagefs-inlined-callee-save"), AssemblyBuilderAccess.Run)
  let md = asm.DefineDynamicModule "MainModule"
  let t = md.DefineType("InlinedCalleeFixture", TypeAttributes.Public ||| TypeAttributes.Class ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  let m = t.DefineMethod("inlinedCalleeProbe", MethodAttributes.Public ||| MethodAttributes.Static, typeof<int>, [| typeof<int> |])
  let il = m.GetILGenerator()
  il.Emit OpCodes.Ldarg_0
  il.Emit(OpCodes.Ldc_I4, 1000)
  il.Emit OpCodes.Add
  il.Emit OpCodes.Ret
  t.CreateType() |> ignore
  asm :> Assembly

let private declOf (name: string) : SourceDecl =
  { Name = name
    Kind = DeclKind.FunctionDecl
    Access = DeclAccess.Public
    Header = sprintf "let %s x" name
    Text = sprintf "let %s x = x + 1000" name
    Container = []
    StartLine = 1
    EndLine = 1 }

[<Tests>]
let tests =
  testList "a function inlined into its caller" [
    testCase "WHY — a patch whose new body is never entered is not reported as Patched, because the page keeps serving the old result" <| fun _ ->
      let mutable dynamic: Assembly[] = [||]
      let sources: AssemblySources = { Dynamic = (fun () -> dynamic); Loaded = (fun () -> [||]) }
      let init: AgentInit =
        { Projects = [ fixtureAssemblyPath ]
          ResolveFrom = []
          ValueReads = SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads }
      let agent = Agent(init, sources)
      agent.Started.AssemblyLoadErrors |> Expect.isEmpty "the fixture assembly loads"

      InlinedCalleeFixture.renderWithInlinedCallee 1
      |> Expect.equal "before the save the caller returns the old result" 20

      dynamic <- [| saveOfInlinedCallee () |]
      let report =
        agent.AfterEval
          { EvaluatedCode = "module InlinedCalleeFixture =\n  let inlinedCalleeProbe (n: int) = n + 1000"
            Detours = DetourPolicy.ApplyDetours
            Discovery = DiscoveryPolicy.WhenChanged
            IsFileSave = true }

      report.DetourReport.Redirected |> Expect.isNonEmpty "the detour landed, which is what makes this case look like a success"
      report.DetourReport.ReachedRunningProcess |> Expect.isNonEmpty "the app holds the compiled copy that was re-pointed"

      InlinedCalleeFixture.renderWithInlinedCallee 1
      |> Expect.equal "the caller inlined the old body, so it still returns the old result after the detour" 20

      let before = { ModulePath = [ "SageFs"; "Tests"; "InlinedCalleeFixture" ]; Opens = []; Decls = [ declOf "inlinedCalleeProbe" ]; RawSource = None }
      let outcome =
        confirmPatchAsOutcome before [ declOf "inlinedCalleeProbe" ] report.DetourReport.Redirected report.DetourReport.ReachedRunningProcess

      match outcome with
      | ReloadOutcome.Patched(patched, considered) ->
        failtestf "the save was reported Patched (%d of %d) but the running caller still returns the old result, so nothing the app does changed" patched considered
      | _ -> ()
  ]
