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
open System.IO
open System.Reflection
open System.Reflection.Emit
open Mono.Cecil.Cil
open System.Runtime.CompilerServices
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning
open SageFs.Features.PatchConfirmation
open SageFs.HostAgent

type private Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

/// The compiled "app" for the inlined case, EMITTED AS IL rather than written in F#.
///
/// WHY IL and not F#: the case needs a callee that EXISTS as a patchable method while the
/// caller has INLINED its body. F# cannot express that — an F# `inline` function, and an
/// F# member marked `AggressiveInlining`, BOTH emit no method at all (measured by reflecting
/// over this very assembly: `let inline` left the module declaring only its renderer). So an
/// F# fixture cannot present a method to re-point, and the detour correctly finds nothing.
///
/// Emitting it makes the shape exact and language-neutral:
///   * `inlinedCalleeProbe` — a real public static method, so a detour CAN re-point it,
///     marked `AggressiveInlining`.
///   * `renderWithInlinedCallee` — contains that body COPIED IN, not a call to it.
/// That is precisely the situation the product must report honestly: the patch lands, the
/// app holds the old copy, and the caller never enters what was patched.
///
/// `1 -> 2 -> 20` before a save; `10010` is what the CONTROL (a real call) returns once the
/// callee is patched, which is the contrast this case exists to pin.

/// The control: the callee is a real call, so the patched body is entered.
module CalledCalleeFixture =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  let calledCalleeProbe (n: int) : int = n + 1

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  let renderWithCalledCallee (n: int) : int = calledCalleeProbe n * 10

let private fixtureAssemblyPath = Assembly.GetExecutingAssembly().Location

/// Write the INLINED fixture — the one shape F# cannot express — as a REAL `.dll`.
///
/// WHY A FILE. The fixture is EMITTED, and an emitted assembly is not a file:
/// `AssemblyBuilder` has no `Save` on this runtime (nor a single-file generator), and an
/// in-memory assembly reports an EMPTY `Location`. The hot-reload host derives its search
/// path from the assembly it is asked to patch, so it was handed a null directory and
/// refused to load the fixture at all — surfacing as "the fixture assembly loads. Should
/// be empty." on a case whose entire point is a DETOUR that must be applied.
///
/// WHY CECIL. It is already this test project's IL dependency (CoverageInstrumenterTests
/// uses it) and it WRITES real assemblies. The IL below is the SAME shape the
/// `AssemblyBuilder` version emitted — an `AggressiveInlining` callee a detour can re-point,
/// and a `NoInlining` caller with that body COPIED IN rather than called. Only where the
/// bytes land changed.
let private writeInlinedFixture (path: string) =
  let module_ = Mono.Cecil.ModuleDefinition.CreateModule(path, Mono.Cecil.ModuleKind.Dll)
  // Cecil's `TypeDefinition` takes its NAMESPACE first (empty here), then the name, then
  // the attributes — a different order from `AssemblyBuilder.DefineType`.
  let t =
    Mono.Cecil.TypeDefinition(
      "",
      "InlinedCalleeFixture",
      Mono.Cecil.TypeAttributes.Public
      ||| Mono.Cecil.TypeAttributes.Abstract
      ||| Mono.Cecil.TypeAttributes.Sealed,
      module_.TypeSystem.Object)
  module_.Types.Add t |> ignore

  let callee =
    Mono.Cecil.MethodDefinition(
      "inlinedCalleeProbe",
      Mono.Cecil.MethodAttributes.Public
      ||| Mono.Cecil.MethodAttributes.Static
      ||| Mono.Cecil.MethodAttributes.HideBySig,
      module_.TypeSystem.Int32)
  t.Methods.Add callee |> ignore
  let calleeParam = Mono.Cecil.ParameterDefinition(module_.TypeSystem.Int32)
  calleeParam.Name <- "n"
  callee.Parameters.Add calleeParam |> ignore
  callee.ImplAttributes <- Mono.Cecil.MethodImplAttributes.AggressiveInlining
  let ci = callee.Body.GetILProcessor()
  ci.Emit(OpCodes.Ldarg_0) |> ignore
  ci.Emit(OpCodes.Ldc_I4_1) |> ignore
  ci.Emit(OpCodes.Add) |> ignore
  ci.Emit(OpCodes.Ret) |> ignore

  let caller =
    Mono.Cecil.MethodDefinition(
      "renderWithInlinedCallee",
      Mono.Cecil.MethodAttributes.Public
      ||| Mono.Cecil.MethodAttributes.Static
      ||| Mono.Cecil.MethodAttributes.HideBySig,
      module_.TypeSystem.Int32)
  t.Methods.Add caller |> ignore
  let callerParam = Mono.Cecil.ParameterDefinition(module_.TypeSystem.Int32)
  callerParam.Name <- "n"
  caller.Parameters.Add callerParam |> ignore
  caller.ImplAttributes <- Mono.Cecil.MethodImplAttributes.NoInlining
  let gi = caller.Body.GetILProcessor()
  gi.Emit(OpCodes.Ldarg_0) |> ignore
  gi.Emit(OpCodes.Ldc_I4_1) |> ignore
  gi.Emit(OpCodes.Add) |> ignore       // the inlined copy of the callee's body
  gi.Emit(OpCodes.Ldc_I4, 10) |> ignore
  gi.Emit(OpCodes.Mul) |> ignore
  gi.Emit(OpCodes.Ret) |> ignore

  // Cecil's `Write()` overload that takes a PATH does not exist — `ModuleDefinition.Write`
  // is `(Stream)` or `(Stream, WriterParameters)`, and the parameterless one tries to write
  // over the module's own never-opened file ("Operation is not valid due to the current
  // state of the object"). So it is handed a real stream.
  use stream = File.Create path
  module_.Write stream
  module_.Dispose()

/// The path the INLINED fixture is written to, written on first use and then reused so the
/// host and the test read the same bytes.
let private inlinedFixtureDll =
  lazy
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-inlined-fixtures")
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "inlined-callee-fixture.dll")
    if not (File.Exists path) then writeInlinedFixture path
    path

/// A dynamically emitted re-eval of one function whose body is `n + 1000`,
/// shaped like FSI's own flattened re-emit: the type name survives without its
/// namespace, so its name is a suffix of the compiled copy's.
let private saveOf (typeName: string) (methodName: string) : Assembly =
  let asm = AssemblyBuilder.DefineDynamicAssembly(AssemblyName("sagefs-save-" + typeName), AssemblyBuilderAccess.RunAndCollect)
  let md = asm.DefineDynamicModule "MainModule"
  let t = md.DefineType(typeName, TypeAttributes.Public ||| TypeAttributes.Class ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  let m = t.DefineMethod(methodName, MethodAttributes.Public ||| MethodAttributes.Static, typeof<int>, [| typeof<int> |])
  let il = m.GetILGenerator()
  il.Emit System.Reflection.Emit.OpCodes.Ldarg_0
  il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 1000)
  il.Emit System.Reflection.Emit.OpCodes.Add
  il.Emit System.Reflection.Emit.OpCodes.Ret
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

let private applySave
  (fixtureAssembly: string)
  (modulePath: string list)
  (typeName: string)
  (methodName: string)
  : Applied =
  let mutable dynamic: Assembly[] = [||]
  let sources: AssemblySources = { Dynamic = (fun () -> dynamic); Loaded = (fun () -> [||]) }
  let init: AgentInit =
    { Projects = [ fixtureAssembly ]
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
      let fixtureType = Assembly.LoadFrom(inlinedFixtureDll.Value).GetType("InlinedCalleeFixture")
      let render = fixtureType.GetMethod("renderWithInlinedCallee", BindingFlags.Public ||| BindingFlags.Static)
      render.Invoke(null, [| box 1 |])
      |> Expect.equal "before the save the caller returns the old result" 20

      // The fixture assembly is EMITTED, so it must be SAVED to a real file and the host
      // pointed at that file. Handing it the test assembly's directory instead meant the host
      // resolved nothing, `AssemblyLoadErrors` was non-empty, and the case died on "the
      // fixture assembly loads" before the detour it exists to prove was ever attempted.
      let fixtureDll = inlinedFixtureDll.Value
      let applied =
        applySave
          fixtureDll
          [ "InlinedCalleeFixture" ]
          "InlinedCalleeFixture"
          "inlinedCalleeProbe"

      render.Invoke(null, [| box 1 |])
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

      let applied =
        applySave
          fixtureAssemblyPath
          [ "SageFs"; "Tests"; "InlinedCalleeOutcomeTests"; "CalledCalleeFixture" ]
          "CalledCalleeFixture"
          "calledCalleeProbe"

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
