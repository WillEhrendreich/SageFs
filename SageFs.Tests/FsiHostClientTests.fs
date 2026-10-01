module SageFs.Tests.FsiHostClientTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.FsiHost.FsiProtocol
open SageFs.FsiHostBuild
open SageFs.FsiHostClient
open SageFs.Tests.TestInfrastructure

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private dotnet =
  match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
  | null
  | "" -> "dotnet"
  | path -> path

/// One host build shared by every test in this file (keyed by SDK + sources, so it is rebuilt when they change).
let private hostBuild : Lazy<string * int> =
  lazy
    (let cache = Path.Combine(Path.GetTempPath(), "sagefs-fsihost-test-cache")
     match resolveSdkVersion dotnet repoRoot with
     | Result.Error reason -> failwith (describeBuildError reason)
     | Result.Ok sdk ->
       match ensureBuilt dotnet sdk cache with
       | Result.Ok(Built dll)
       | Result.Ok(Reused dll) -> dll, int (sdk.Split('.').[0])
       | Result.Error reason -> failwith (describeBuildError reason))

/// Code that sleeps for `TestTimeouts.runawayEval`, for the tests that must interrupt or kill it.
let private runawayEvalCode =
  sprintf "System.Threading.Thread.Sleep %d;;" (int TestTimeouts.runawayEval.TotalMilliseconds)

type private Started =
  { Session: FsiHostSession
    Output: ConcurrentQueue<string> }

let private startHost () : Async<Started> =
  async {
    let dll, _ = hostBuild.Force()
    let output = ConcurrentQueue<string>()
    let options =
      { HostDll = dll
        Dotnet = dotnet
        FsiArgs = [ "fsi"; "--noninteractive"; "--nologo"; "--readline-" ]
        WorkingDir = repoRoot
        Environment = []
        Libraries = SageFs.HostAdaptation.HostLibraries.AsBuilt
        OnOutput = fun _ text -> output.Enqueue text
        OnLog = ignore
        StartupTimeoutMs = TestTimeouts.asMs TestTimeouts.processStartPatience }
    match! start options with
    | Result.Ok session -> return { Session = session; Output = output }
    | Result.Error reason -> return failtest (describeStartError reason)
  }

let private withHost (body: Started -> Async<unit>) : Async<unit> =
  async {
    let! started = startHost ()
    try do! body started
    finally (started.Session :> IDisposable).Dispose()
  }

let private evalOk (session: FsiHostSession) (code: string) : Async<unit> =
  async {
    match! session.Eval(code, CancellationToken.None) with
    | Completed(EvalSucceeded, _) -> ()
    | other -> failtestf "expected %s to succeed but got %A" code other
  }

let private joined (output: ConcurrentQueue<string>) = String.Join("", output.ToArray())

/// Where a syscall filter can be installed the click says it ran under one; where it cannot, it says so, never silently.
let private expectContainmentMatchesPlatform (containment: Containment) =
  match SageFs.ThreadSandbox.availability (), containment with
  | Result.Ok _, ContainedBy policy -> policy |> Expect.equal "the policy the host chose" SageFs.SandboxPolicy.NoNetworkNoWritesNoSpawn
  | Result.Error _, NotContained _ -> ()
  | availability, other -> failtestf "containment %A does not match this platform (%A)" other availability

[<Tests>]
let tests =
  testList "FsiHostClient" [
    testList "describeStartError" [
      testCase "every failure that has host output includes it" <| fun _ ->
        let errors =
          [ NoPortReported(1, "HOSTLINE"); ConnectFailed("x", "HOSTLINE"); NotReady(1, "HOSTLINE"); ClosedBeforeReady "HOSTLINE" ]
        for error in errors do
          Expect.stringContains "host output is part of the message" "HOSTLINE" (describeStartError error)

      testCase "a start that fails before there is any output says only what happened" <| fun _ ->
        Expect.isFalse "no empty 'Host output' section" ((describeStartError (ClosedBeforeReady "")).Contains "Host output")
    ]

    Integration.hostList "isolated FSI host over the wire" [
      testAsync "Ready reports the SDK's own runtime and FSharp.Core" {
        do!
          withHost (fun started ->
            async {
              let _, major = hostBuild.Force()
              Expect.stringStarts (sprintf "runtime %s" started.Session.Runtime) (sprintf ".NET %d." major) started.Session.Runtime
              Expect.stringStarts (sprintf "FSharp.Core %s" started.Session.FSharpCoreVersion) (sprintf "%d." major) started.Session.FSharpCoreVersion
            })
      }

      testAsync "evaluates a submission and keeps state between submissions" {
        do!
          withHost (fun started ->
            async {
              do! evalOk started.Session "let x = 21 * 2;;"
              do! evalOk started.Session "printfn \"x is %d\" x;;"
              Expect.stringContains "user output reaches the client" "x is 42" (joined started.Output)
            })
      }

      testAsync "a type error is EvalFailed with structured diagnostics" {
        do!
          withHost (fun started ->
            async {
              match! started.Session.Eval("let y : int = \"not an int\";;", CancellationToken.None) with
              | Completed(EvalFailed _, diagnostics) ->
                Expect.isNonEmpty "at least one diagnostic" diagnostics
                let first = diagnostics |> List.find (fun d -> d.Severity = DiagError)
                Expect.equal "points at line 1" 1 first.StartLine
                Expect.isTrue "has an error number" (first.ErrorNumber > 0)
              | other -> failtestf "expected a compile failure, got %A" other
            })
      }

      testAsync "a runtime exception is EvalFailed carrying the exception message" {
        do!
          withHost (fun started ->
            async {
              // (failwith ... : unit) — a bare `failwith` at top level trips F#'s value restriction instead.
              match! started.Session.Eval("(failwith \"boom\" : unit);;", CancellationToken.None) with
              | Completed(EvalFailed message, _) -> Expect.stringContains "the exception message" "boom" message
              | other -> failtestf "expected EvalFailed, got %A" other
            })
      }

      testAsync "cancelling interrupts the running eval and the session stays usable" {
        do!
          withHost (fun started ->
            async {
              use cancel = new CancellationTokenSource(TestTimeouts.cancelAfter)
              let stopwatch = Stopwatch.StartNew()
              match! started.Session.Eval(runawayEvalCode, cancel.Token) with
              | Completed(EvalInterrupted, _) -> ()
              | other -> failtestf "expected EvalInterrupted, got %A" other
              Expect.isLessThan "interrupted long before the sleep would end" (stopwatch.Elapsed, TestTimeouts.patience)
              do! evalOk started.Session "1 + 1;;"
            })
      }

      testAsync "a killed host completes the running eval with HostCrashed instead of hanging" {
        do!
          withHost (fun started ->
            async {
              let running = started.Session.Eval(runawayEvalCode, CancellationToken.None) |> Async.StartAsTask
              do! Async.Sleep (TestTimeouts.asMs TestTimeouts.cancelAfter)
              Process.GetProcessById(started.Session.ProcessId).Kill true
              let! finished = Task.WhenAny(running, Task.Delay(TestTimeouts.patience)) |> Async.AwaitTask
              Expect.isTrue "the pending eval completed" (obj.ReferenceEquals(finished, running))
              match running.Result with
              | HostCrashed _ -> ()
              | other -> failtestf "expected HostCrashed, got %A" other
              match! started.Session.Eval("1;;", CancellationToken.None) with
              | HostCrashed _ -> ()
              | other -> failtestf "later calls should also be HostCrashed, got %A" other
            })
      }

      testAsync "a click runs one held getter in a real host, a looping getter times out, and the host keeps serving" {
        do!
          withHost (fun started ->
            async {
              // `Len` calls other code (held), `Spin` loops (held), `Const` is a constant (shown without a click).
              do!
                evalOk started.Session
                  "type Gadget(name: string) =\n  member _.Name = name\n  member _.Const = 42\n  member this.Len = this.Name.Length\n  member _.Spin : int =\n    while true do ()\n    0;;"
              do! evalOk started.Session "let g = Gadget(\"abcd\");;"
              let childOf (label: string) (binding: SageFs.Features.LiveValueTree.LiveBindingValue) =
                binding.Root.Children |> List.tryFind (fun c -> c.Label = label)
              let! before = started.Session.ReadLiveValues 1L
              match before with
              | Answered snapshot ->
                let g = snapshot.Bindings |> List.find (fun b -> b.Name = "g")
                childOf "Len" g |> Option.map (fun c -> c.Kind)
                |> Expect.equal "Len is listed, not run" (Some (SageFs.Features.LiveValueTree.NodeKind.NotEvaluated SageFs.Features.LiveValueTree.NotEvaluatedReason.GetterRunsCode))
                childOf "Spin" g |> Option.map (fun c -> c.Kind)
                |> Expect.equal "Spin is listed as a loop" (Some (SageFs.Features.LiveValueTree.NodeKind.NotEvaluated SageFs.Features.LiveValueTree.NotEvaluatedReason.GetterLoops))
              | other -> failtestf "expected a snapshot, got %A" other

              match! started.Session.EvaluateMember("g", [ "Len" ]) with
              | Answered (MemberShown (binding, containment, guards)) ->
                childOf "Len" binding |> Option.map (fun c -> c.Preview) |> Expect.equal "the click ran Len" (Some "4")
                childOf "Spin" binding |> Option.map (fun c -> c.Kind)
                |> Expect.equal "Spin was not run by the click on Len" (Some (SageFs.Features.LiveValueTree.NodeKind.NotEvaluated SageFs.Features.LiveValueTree.NotEvaluatedReason.GetterLoops))
                expectContainmentMatchesPlatform containment
                match guards.Coverage with
                | SageFs.Features.GuardCoverage.Guarded _ -> ()
                | other -> failtestf "the click should have run with guards on, got %A" other
              | other -> failtestf "expected the walked binding, got %A" other

              match! started.Session.EvaluateMember("g", [ "Spin" ]) with
              | Answered (MemberShown (binding, _, guards)) ->
                guards.Trip |> Expect.equal "a guard stopped the loop, so no thread is left spinning" SageFs.Features.GuardTrip.LoopStopped
                childOf "Spin" binding |> Option.map (fun c -> c.Kind)
                |> Expect.equal "the looping getter is given up on, with the reason" (Some (SageFs.Features.LiveValueTree.NodeKind.NotEvaluated SageFs.Features.LiveValueTree.NotEvaluatedReason.EvaluationTimedOut))
              | other -> failtestf "expected the walked binding, got %A" other

              // The host is still serving: the abandoned getter did not take it down.
              match! started.Session.EvaluateMember("g", [ "Len" ]) with
              | Answered (MemberShown (binding, _, _)) ->
                childOf "Len" binding |> Option.map (fun c -> c.Preview) |> Expect.equal "still answers after a timeout" (Some "4")
              | other -> failtestf "expected the walked binding, got %A" other

              match! started.Session.EvaluateMember("missing", [ "Len" ]) with
              | Answered (BindingNotFound name) -> name |> Expect.equal "names the binding" "missing"
              | other -> failtestf "expected BindingNotFound, got %A" other
            })
      }

      testAsync "disposing shuts the host down: the process exits" {
        let! started = startHost ()
        let pid = started.Session.ProcessId
        (started.Session :> IDisposable).Dispose()
        let! finished = Task.WhenAny(started.Session.Exited, Task.Delay(TestTimeouts.patience)) |> Async.AwaitTask
        Expect.isTrue "Exited completed" (obj.ReferenceEquals(finished, started.Session.Exited))
        Expect.isTrue "no process left behind" (try Process.GetProcessById(pid).HasExited with :? ArgumentException -> true)
      }
    ]
  ]
