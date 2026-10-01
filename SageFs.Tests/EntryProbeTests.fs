/// The host-side half of patch confirmation: a probe that records when a
/// re-pointed function's NEW body is entered, the stub a detour is pointed at,
/// and the wait that answers "has it run yet" without polling.
module SageFs.Tests.EntryProbeTests

open System
open System.Reflection
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Middleware.EntryProbes


type ProbeTargets =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Double(x: int) : int = x * 2

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Join(a: string, b: string) : string = a + "|" + b

/// An object whose member a closure or an instance member edit re-points.
type ProbeInstance(prefix: string) =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  member _.Say(word: string) : string = prefix + word

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  member _.Echo<'T>(value: 'T) : 'T = value

let private statusOf (reading: EntryReading) (id: int64) : ProbeStatus =
  reading.Sightings |> List.find (fun s -> s.Probe = id) |> fun s -> s.Status

let private method' (name: string) : MethodInfo = typeof<ProbeTargets>.GetMethod name

[<Tests>]
let tests =
  testList "entry probes" [
    testList "the registry" [
      testCase "WHY — a probe nobody has entered reads NotEntered, so silence is never read as a hit" <| fun _ ->
        let registry = ProbeRegistry()
        let probe = registry.Allocate "App.render"
        statusOf (registry.Read [ probe.Id ]) probe.Id |> Expect.equal "nothing entered it" ProbeStatus.NotEntered

      testCase "WHY — entering a probe marks it Entered, and entering it again changes nothing" <| fun _ ->
        let registry = ProbeRegistry()
        let probe = registry.Allocate "App.render"
        registry.Enter probe.Id
        registry.Enter probe.Id
        statusOf (registry.Read [ probe.Id ]) probe.Id |> Expect.equal "seen running" ProbeStatus.Entered

      testCase "WHY — every probe gets its own id, so two saves of one function are told apart" <| fun _ ->
        let registry = ProbeRegistry()
        let a = registry.Allocate "App.render"
        let b = registry.Allocate "App.render"
        a.Id |> Expect.notEqual "distinct ids" b.Id

      testCase "WHY — a newer patch of the same function supersedes the older probe that never ran, and leaves one that did run" <| fun _ ->
        let registry = ProbeRegistry()
        let ran = registry.Allocate "App.render"
        registry.Enter ran.Id
        let silent = registry.Allocate "App.render"
        let other = registry.Allocate "App.other"
        let newest = registry.Allocate "App.render"
        registry.Commit newest
        let reading = registry.Read [ ran.Id; silent.Id; other.Id; newest.Id ]
        statusOf reading ran.Id |> Expect.equal "what was seen running stays seen" ProbeStatus.Entered
        statusOf reading silent.Id |> Expect.equal "its body is no longer the one that will run" ProbeStatus.Superseded
        statusOf reading other.Id |> Expect.equal "another function is untouched" ProbeStatus.NotEntered
        statusOf reading newest.Id |> Expect.equal "the newest is waiting" ProbeStatus.NotEntered

      testCase "WHY — an unknown probe id reads NotEntered, never an exception" <| fun _ ->
        let registry = ProbeRegistry()
        statusOf (registry.Read [ 424242L ]) 424242L |> Expect.equal "unknown is not seen" ProbeStatus.NotEntered

      testTask "WHY — a wait for probes that already ran answers at once, without waiting for the bound" {
        let registry = ProbeRegistry(fun _ -> TaskCompletionSource<unit>().Task :> Task)
        let probe = registry.Allocate "App.render"
        registry.Enter probe.Id
        let! reading = registry.Await([ probe.Id ], TestTimeouts.unreachedBound) |> Async.StartAsTask
        statusOf reading probe.Id |> Expect.equal "already seen" ProbeStatus.Entered
      }

      testTask "WHY — a wait ends the moment the probe is entered, not when the bound runs out" {
        let neverElapses = TaskCompletionSource<unit>()
        let registry = ProbeRegistry(fun _ -> neverElapses.Task :> Task)
        let probe = registry.Allocate "App.render"
        let waiting = registry.Await([ probe.Id ], TestTimeouts.unreachedBound) |> Async.StartAsTask
        waiting.IsCompleted |> Expect.isFalse "nothing has run, so it is still waiting"
        registry.Enter probe.Id
        let! reading = waiting
        statusOf reading probe.Id |> Expect.equal "woken by the entry" ProbeStatus.Entered
      }

      testTask "WHY — a wait that reaches its bound reports what it saw, which for a silent probe is NotEntered" {
        let bound = TaskCompletionSource<unit>()
        let registry = ProbeRegistry(fun _ -> bound.Task :> Task)
        let ran = registry.Allocate "App.a"
        let silent = registry.Allocate "App.b"
        registry.Enter ran.Id
        let waiting = registry.Await([ ran.Id; silent.Id ], SageFs.Timeouts.patchConfirmation) |> Async.StartAsTask
        waiting.IsCompleted |> Expect.isFalse "one probe is still silent, so it keeps waiting"
        bound.SetResult()
        let! reading = waiting
        statusOf reading ran.Id |> Expect.equal "the one that ran" ProbeStatus.Entered
        statusOf reading silent.Id |> Expect.equal "the one that did not" ProbeStatus.NotEntered
      }

      testCase "WHY - committing only the OLDEST probe of a save supersedes the earlier saves' probes and leaves the save's own other probes, which is how several closures of one declaration are watched" <| fun _ ->
        let registry = ProbeRegistry()
        let earlier = registry.Allocate "App.routes"
        let first = registry.Allocate "App.routes"
        let second = registry.Allocate "App.routes"
        registry.Commit first
        let reading = registry.Read [ earlier.Id; first.Id; second.Id ]
        statusOf reading earlier.Id |> Expect.equal "an earlier save's closure is replaced" ProbeStatus.Superseded
        statusOf reading first.Id |> Expect.equal "this save's own closure waits to run" ProbeStatus.NotEntered
        statusOf reading second.Id |> Expect.equal "so does its sibling" ProbeStatus.NotEntered

      testTask "WHY — superseding a silent probe ends the wait for it, because it will never run" {
        let registry = ProbeRegistry(fun _ -> TaskCompletionSource<unit>().Task :> Task)
        let old = registry.Allocate "App.render"
        let waiting = registry.Await([ old.Id ], TestTimeouts.unreachedBound) |> Async.StartAsTask
        let newer = registry.Allocate "App.render"
        registry.Commit newer
        let! reading = waiting
        statusOf reading old.Id |> Expect.equal "replaced by a newer patch" ProbeStatus.Superseded
      }
    ]

    testList "the stub a detour points at" [
      testCase "WHY — calling the stub runs the new body and records that it was entered" <| fun _ ->
        let probe = ProbeRegistry.Shared.Allocate "ProbeTargets.Double"
        match stubFor probe (method' "Double") with
        | Result.Error failure -> failtestf "a plain static method must get a stub: %s" (StubFailure.describe failure)
        | Result.Ok stub ->
          statusOf (ProbeRegistry.Shared.Read [ probe.Id ]) probe.Id |> Expect.equal "not called yet" ProbeStatus.NotEntered
          stub.Invoke(null, [| box 21 |]) |> Expect.equal "the new body's result passes through" (box 42)
          statusOf (ProbeRegistry.Shared.Read [ probe.Id ]) probe.Id |> Expect.equal "called, so entered" ProbeStatus.Entered

      testCase "WHY — the stub has the target's exact signature, so a detour can point at it in place of the target" <| fun _ ->
        let probe = ProbeRegistry.Shared.Allocate "ProbeTargets.Join"
        match stubFor probe (method' "Join") with
        | Result.Error failure -> failtestf "a plain static method must get a stub: %s" (StubFailure.describe failure)
        | Result.Ok stub ->
          stub.ReturnType |> Expect.equal "same return type" typeof<string>
          stub.GetParameters() |> Array.map _.ParameterType |> Expect.equal "same parameters" [| typeof<string>; typeof<string> |]
          stub.Invoke(null, [| box "a"; box "b" |]) |> Expect.equal "arguments reach the target in order" (box "a|b")

      testCase "WHY - an instance member's stub takes the object first, so a detour from one object's method passes the object straight through" <| fun _ ->
        let probe = ProbeRegistry.Shared.Allocate "ProbeInstance.Say"
        let target = typeof<ProbeInstance>.GetMethod "Say"
        match stubFor probe target with
        | Result.Error failure -> failtestf "an instance member of a class must get a stub: %s" (StubFailure.describe failure)
        | Result.Ok stub ->
          stub.GetParameters() |> Array.map _.ParameterType
          |> Expect.equal "the object, then the member's own parameters" [| typeof<ProbeInstance>; typeof<string> |]
          stub.Invoke(null, [| box (ProbeInstance "hello "); box "world" |])
          |> Expect.equal "the member ran against the object it was handed" (box "hello world")
          statusOf (ProbeRegistry.Shared.Read [ probe.Id ]) probe.Id |> Expect.equal "called, so entered" ProbeStatus.Entered

      testCase "WHY - an open generic method gets no stub, because it has no code to call until its type arguments are known" <| fun _ ->
        let probe = ProbeRegistry.Shared.Allocate "ProbeInstance.Echo"
        match stubFor probe (typeof<ProbeInstance>.GetMethod "Echo") with
        | Result.Ok _ -> failtest "an open generic method must not be given a stub"
        | Result.Error (StubFailure.NotDetourable _) -> ()
        | Result.Error other -> failtestf "expected NotDetourable, got %A" other

      testCase "WHY - a generic method closed over its type arguments gets a stub, which is how a value-type instantiation is detoured" <| fun _ ->
        let probe = ProbeRegistry.Shared.Allocate "ProbeInstance.Echo<int>"
        let closed = typeof<ProbeInstance>.GetMethod("Echo").MakeGenericMethod typeof<int>
        match stubFor probe closed with
        | Result.Error failure -> failtestf "a closed generic method must get a stub: %s" (StubFailure.describe failure)
        | Result.Ok stub ->
          stub.Invoke(null, [| box (ProbeInstance "x"); box 7 |]) |> Expect.equal "the closed method ran against the object" (box 7)
          statusOf (ProbeRegistry.Shared.Read [ probe.Id ]) probe.Id |> Expect.equal "called, so entered" ProbeStatus.Entered

      testCase "WHY — a stub that is never called leaves its probe NotEntered" <| fun _ ->
        let probe = ProbeRegistry.Shared.Allocate "ProbeTargets.Double.unused"
        stubFor probe (method' "Double") |> Expect.isOk "a stub is made"
        statusOf (ProbeRegistry.Shared.Read [ probe.Id ]) probe.Id |> Expect.equal "built is not entered" ProbeStatus.NotEntered
    ]
  ]
