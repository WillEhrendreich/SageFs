/// The outcome gate for a function the compiler inlined into its caller.
///
/// A save re-points the old function at the new body. When the old function was
/// inlined into its caller, the caller carries its own copy of the old body and
/// never enters the function whose entry point was re-pointed. Every signal a
/// save had (the detour landed, the app "holds" the old copy) says success, and
/// the app keeps serving the old result.
///
/// These tests drive the real path: a real compiled callee and caller (this
/// assembly), a real dynamically emitted save, the real `Agent` applying a real
/// Harmony detour through the real entry-probe stub, the real planner decision,
/// and the real host-side wait. Only the F# compile of the save is replaced by
/// IL emission, the same way `HostAgentTests` does it.
module SageFs.Tests.InlinedCalleeOutcomeTests

open System
open System.Reflection
open System.Reflection.Emit
open System.Runtime.CompilerServices
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning
open SageFs.Features.PatchConfirmation
open SageFs.HostAgent

type private Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

/// The compiled "app". `inlinedCalleeProbe` is `inline`, so the F# compiler
/// copies its body into `renderWithInlinedCallee` at compile time: the caller
/// never calls the method the detour re-points. That is deterministic, unlike
/// `AggressiveInlining`, which a tier-0 JIT ignores.
module InlinedCalleeFixture =
  let inline inlinedCalleeProbe (n: int) : int = n + 1

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  let renderWithInlinedCallee (n: int) : int = inlinedCalleeProbe n * 10

/// The control: the callee is a real call, so the patched body is entered.
module CalledCalleeFixture =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  let calledCalleeProbe (n: int) : int = n + 1

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  let renderWithCalledCallee (n: int) : int = calledCalleeProbe n * 10

let private fixtureAssemblyPath = Assembly.GetExecutingAssembly().Location

/// A dynamically emitted re-eval of one function whose body is `n + 1000`,
/// shaped like FSI's own flattened re-emit: the type name survives without its
/// namespace, so its name is a suffix of the compiled copy's.
let private saveOf (typeName: string) (methodName: string) : Assembly =
  let asm = AssemblyBuilder.DefineDynamicAssembly(AssemblyName("sagefs-save-" + typeName), AssemblyBuilderAccess.Run)
  let md = asm.DefineDynamicModule "MainModule"
  let t = md.DefineType(typeName, TypeAttributes.Public ||| TypeAttributes.Class ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  let m = t.DefineMethod(methodName, MethodAttributes.Public ||| MethodAttributes.Static, typeof<int>, [| typeof<int> |])
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

/// What the worker holds after a save: the planner's verdict, and the functions
/// it landed, with the probes the host put on their new bodies.
type private Applied =
  { Agent: Agent
    Report: AfterEvalReport
    Landed: SourceDecl list
    Planned: Outcome }

let private applySave (modulePath: string list) (typeName: string) (methodName: string) : Applied =
  let mutable dynamic: Assembly[] = [||]
  let sources: AssemblySources = { Dynamic = (fun () -> dynamic); Loaded = (fun () -> [||]) }
  let init: AgentInit =
    { Projects = [ fixtureAssemblyPath ]
      ResolveFrom = []
      ValueReads = SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads }
  let agent = Agent(init, sources)
  agent.Started.AssemblyLoadErrors |> Expect.isEmpty "the fixture assembly loads"
  dynamic <- [| saveOf typeName methodName |]
  let report =
    agent.AfterEval
      { EvaluatedCode = sprintf "module %s =\n  let %s (n: int) = n + 1000" typeName methodName
        Detours = DetourPolicy.ApplyDetours
        Discovery = DiscoveryPolicy.WhenChanged
        IsFileSave = true; Closures = [] }
  report.DetourReport.Redirected |> Expect.isNonEmpty "the detour landed, which is what makes this case look like a success"
  report.DetourReport.ReachedRunningProcess |> Expect.isNonEmpty "the app holds the compiled copy that was re-pointed"
  let decl = declOf methodName
  let before = { ModulePath = modulePath; Opens = []; Decls = [ decl ]; RawSource = None }
  let landed, planned =
    confirmPatchLanding before [ decl ] report.DetourReport.Redirected report.DetourReport.ReachedRunningProcess
  { Agent = agent; Report = report; Landed = landed; Planned = planned }

/// Ask the host what it saw, with a bound short enough for a test, and let the
/// decision settle it.
let private settled (applied: Applied) : Async<WatchStep> =
  async {
    match PatchConfirmation.start (watchedOfLanded applied.Landed applied.Report.DetourReport.Probes) applied.Planned with
    | Begun.NothingToWatch o -> return failtestf "a planner-confirmed patch must be watched, got %A" o
    | Begun.Watching(_, watch) ->
      let! reading = applied.Agent.AwaitEntries(probesOf watch, SageFs.Tests.TestTimeouts.entryAwaitBound)
      return settle reading watch
  }

[<Tests>]
let tests =
  testList "a function inlined into its caller" [
    testTask "WHY — a patch whose new body is never entered is never reported as Patched, because the page keeps serving the old result" {
      InlinedCalleeFixture.renderWithInlinedCallee 1
      |> Expect.equal "before the save the caller returns the old result" 20

      let applied = applySave [ "SageFs"; "Tests"; "InlinedCalleeOutcomeTests"; "InlinedCalleeFixture" ] "InlinedCalleeFixture" "inlinedCalleeProbe"

      InlinedCalleeFixture.renderWithInlinedCallee 1
      |> Expect.equal "the caller inlined the old body, so it still returns the old result after the detour" 20

      match applied.Planned with
      | Outcome.PatchPending(1, 1, []) -> ()
      | other -> failtestf "a landed detour is applied and unconfirmed, never Patched (the caller still runs the old body), got %A" other

      let! step = settled applied |> Async.StartAsTask
      match step with
      | WatchStep.Settled(Outcome.NeverEntered("inlinedCalleeProbe", [], 0, 1, [])) -> ()
      | other -> failtestf "the new body was never entered, so the save ends as NeverEntered naming it, got %A" other
    }

    testTask "WHY — a patch whose new body runs is confirmed, by the same machinery, because the evidence is the new body running" {
      CalledCalleeFixture.renderWithCalledCallee 1 |> Expect.equal "before the save" 20

      let applied = applySave [ "SageFs"; "Tests"; "InlinedCalleeOutcomeTests"; "CalledCalleeFixture" ] "CalledCalleeFixture" "calledCalleeProbe"

      match applied.Planned with
      | Outcome.PatchPending(1, 1, []) -> ()
      | other -> failtestf "a landed detour is applied and unconfirmed until its body runs, got %A" other

      CalledCalleeFixture.renderWithCalledCallee 1
      |> Expect.equal "the caller really calls the callee, so the patched body is what runs" 10010

      let! step = settled applied |> Async.StartAsTask
      match step with
      | WatchStep.Settled(Outcome.Patched(1, 1)) -> ()
      | other -> failtestf "the new body ran, so the patch is confirmed, got %A" other
    }
  ]
