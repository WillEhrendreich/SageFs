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
        OnOutput = fun _ text -> output.Enqueue text
        OnLog = ignore
        StartupTimeoutMs = 60_000 }
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
              use cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds 700.0)
              let stopwatch = Stopwatch.StartNew()
              match! started.Session.Eval("System.Threading.Thread.Sleep 60000;;", cancel.Token) with
              | Completed(EvalInterrupted, _) -> ()
              | other -> failtestf "expected EvalInterrupted, got %A" other
              Expect.isLessThan "interrupted long before the sleep would end" (stopwatch.Elapsed, TimeSpan.FromSeconds 20.0)
              do! evalOk started.Session "1 + 1;;"
            })
      }

      testAsync "a killed host completes the running eval with HostLost instead of hanging" {
        do!
          withHost (fun started ->
            async {
              let running = started.Session.Eval("System.Threading.Thread.Sleep 60000;;", CancellationToken.None) |> Async.StartAsTask
              do! Async.Sleep 500
              Process.GetProcessById(started.Session.ProcessId).Kill true
              let! finished = Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds 20.0)) |> Async.AwaitTask
              Expect.isTrue "the pending eval completed" (obj.ReferenceEquals(finished, running))
              match running.Result with
              | HostLost _ -> ()
              | other -> failtestf "expected HostLost, got %A" other
              match! started.Session.Eval("1;;", CancellationToken.None) with
              | HostLost _ -> ()
              | other -> failtestf "later calls should also be HostLost, got %A" other
            })
      }

      testAsync "disposing shuts the host down: the process exits" {
        let! started = startHost ()
        let pid = started.Session.ProcessId
        (started.Session :> IDisposable).Dispose()
        let! finished = Task.WhenAny(started.Session.Exited, Task.Delay(TimeSpan.FromSeconds 20.0)) |> Async.AwaitTask
        Expect.isTrue "Exited completed" (obj.ReferenceEquals(finished, started.Session.Exited))
        Expect.isTrue "no process left behind" (try Process.GetProcessById(pid).HasExited with :? ArgumentException -> true)
      }
    ]
  ]
