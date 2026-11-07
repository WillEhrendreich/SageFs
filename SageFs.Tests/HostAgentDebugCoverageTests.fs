module SageFs.Tests.HostAgentDebugCoverageTests

// A test run under a debugger executes instrumented code, and the probe bits it sets belong to no reading anyone asked for.
// If they stay, the next ordinary test's coverage carries the debugged test's lines, and the dependency graph learns that
// test covers code it never touched. These cases pin that the host clears the probes around a debug run, however the run
// ends, and that a real agent's next reading is clean after one.

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.HostAgent
open SageFs.HostAgent.TestDebug

let private aTest : TestCase =
  { Id = TestId.create "suite/adds" TestFramework.Expecto
    FullName = "suite/adds"
    DisplayName = "suite/adds"
    NamePath = []
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

/// A probe whose debugger is always attached, so a released test runs at once.
let private attachedProbe : DebuggerProbe =
  { Presence = fun () -> DebuggerPresence.DebuggerAttached
    AwaitAttach = fun _ -> async { return DebuggerPresence.DebuggerAttached }
    OpenForAttach = fun () -> AttachAccess.Open
    CloseForAttach = ignore }

let private noAssemblies () : System.Reflection.Assembly[] = [||]

let private emptyInit : AgentInit =
  { Projects = []
    ResolveFrom = []
    ValueReads = SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads
    WebDevReload = SageFs.HostAgent.WebDevReload.Off }

/// The probe field of an instrumented assembly: a real file on disk (the agent only looks in assemblies that have a location)
/// holding the type the instrumenter writes, `__SageFsCoverage`, with its public static `Hits` array.
let private instrumentedProbes () : Assembly * FieldInfo =
  let dir = Directory.CreateTempSubdirectory("sagefs-debug-coverage-").FullName
  let path = Path.Combine(dir, "Instrumented.dll")
  let builder = PersistedAssemblyBuilder(AssemblyName "Instrumented", typeof<obj>.Assembly)
  let moduleBuilder = builder.DefineDynamicModule "Instrumented"
  let tracker = moduleBuilder.DefineType("__SageFsCoverage", TypeAttributes.Public ||| TypeAttributes.Sealed ||| TypeAttributes.Abstract)
  tracker.DefineField("Hits", typeof<bool array>, FieldAttributes.Public ||| FieldAttributes.Static) |> ignore
  tracker.CreateType() |> ignore
  builder.Save path
  let assembly = Assembly.LoadFrom path
  Directory.Delete(dir, true)
  assembly, assembly.GetType("__SageFsCoverage").GetField "Hits"

/// A reading of a probe array with this many probes and none hit.
let private cleanReading (probes: int) : CoverageReading =
  let bitmap = CoverageBitmap.ofBoolArray (Array.zeroCreate probes)
  CoverageTaken(bitmap.Count, CoverageBitmap.toBase64 bitmap)

[<Tests>]
let tests =
  testList "a debug run leaves the coverage probes clean" [

    testTask "WHY - the probes are cleared before the run starts and again after it ends, in that order" {
      let events = ResizeArray<string>()
      let run = async { events.Add "run"; return 7 }
      let! result = discardingProbeHits (fun () -> events.Add "discard") run |> Async.StartAsTask
      result |> Expect.equal "the run's own answer comes back" 7
      List.ofSeq events |> Expect.equal "discard, run, discard" [ "discard"; "run"; "discard" ]
    }

    testTask "WHY - however the run ends, the probes are cleared exactly twice and the failure is not swallowed" {
      for fails in [ true; false ] do
        for value in [ 0; 1; -7; 1000 ] do
          let discards = ref 0
          let run : Async<int> = async { match fails with | true -> return failwith "the test threw" | false -> return value }
          let! outcome =
            task {
              try
                let! answer = discardingProbeHits (fun () -> discards.Value <- discards.Value + 1) run |> Async.StartAsTask
                return Result.Ok answer
              with ex ->
                return Result.Error ex.Message
            }
          match fails, outcome with
          | true, Result.Error message -> message |> Expect.equal "the failure comes out unchanged" "the test threw"
          | false, Result.Ok answer -> answer |> Expect.equal "the answer comes back" value
          | _ -> failtestf "fails=%b gave %A" fails outcome
          discards.Value |> Expect.equal (sprintf "fails=%b value=%d: cleared before and after" fails value) 2
    }

    testTask "WHY - probe bits set while a test is held are not in the next ordinary reading" {
      let assembly, hits = instrumentedProbes ()
      let sources : AssemblySources = { Dynamic = noAssemblies; Loaded = fun () -> [| assembly |] }
      let agent = Agent(emptyInit, sources, [], TestTimeouts.patienceBrief, attachedProbe)
      hits.SetValue(null, box (Array.zeroCreate<bool> 4))
      let ticket =
        match agent.DebugBegin aTest with
        | DebugBegin.Held target -> target.Ticket
        | other -> failtestf "expected a held test, got %A" other
      // The code the debugged test ran, as the probes record it.
      let probes = hits.GetValue null :?> bool array
      probes.[1] <- true
      probes.[3] <- true
      let! progress = agent.DebugContinue(ticket, TestTimeouts.patienceBrief) |> Async.StartAsTask
      match progress with
      | DebugProgress.Ended(DebugEnd.Attached _) -> ()
      | other -> failtestf "expected the run to end attached, got %A" other
      agent.TakeCoverage() |> Expect.equal "the next reading starts clean" (cleanReading 4)
    }
  ]
