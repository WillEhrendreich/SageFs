/// What a clicked getter does to a process that was not built to survive it. A stack overflow ends the process, so these
/// run in a child: the test host starts this assembly again with `--guard-child <scenario>`, the child clicks one getter
/// through the real evaluator and the real patcher, prints one line saying what came of it, and exits. The parent
/// reads the line, or reads that there was none because the child died.
module SageFs.Tests.GuardChild

open System
open System.Reflection
open System.Threading
open System.Runtime.CompilerServices
open SageFs.Features
open SageFs.Features.LiveValueTree
open SageFs.Features.MemberEvaluation

/// The getters a child can click. A closed set, with one way to write each as the argument.
type GuardScenario =
  | Returns
  | RecursesForever
  | SpinsForever
  | SpinsInsideACatchAll
  | SpinsInAHelper
  | BlocksOnAWait
  | SpinsInAStateMachine
  | RecursesForeverUnguarded
  | SpinsForeverUnguarded

module GuardScenario =

  let all : GuardScenario list =
    [ Returns; RecursesForever; SpinsForever; SpinsInsideACatchAll; SpinsInAHelper; BlocksOnAWait; SpinsInAStateMachine
      RecursesForeverUnguarded; SpinsForeverUnguarded ]

  let toArgument (scenario: GuardScenario) : string =
    match scenario with
    | Returns -> "returns"
    | RecursesForever -> "recurses-forever"
    | SpinsForever -> "spins-forever"
    | SpinsInsideACatchAll -> "spins-inside-a-catch-all"
    | SpinsInAHelper -> "spins-in-a-helper"
    | BlocksOnAWait -> "blocks-on-a-wait"
    | SpinsInAStateMachine -> "spins-in-a-state-machine"
    | RecursesForeverUnguarded -> "recurses-forever-unguarded"
    | SpinsForeverUnguarded -> "spins-forever-unguarded"

  /// The getter's name on `Victim`.
  let getterName (scenario: GuardScenario) : string =
    match scenario with
    | Returns -> "Fine"
    | RecursesForever
    | RecursesForeverUnguarded -> "Depth"
    | SpinsForever
    | SpinsForeverUnguarded -> "Spin"
    | SpinsInsideACatchAll -> "SwallowingSpin"
    | SpinsInAHelper -> "ViaHelper"
    | BlocksOnAWait -> "Blocked"
    | SpinsInAStateMachine -> "MachineSpin"

  /// The controls run with guards off, to show the scenario really would end the process or run forever.
  let guardingFor (scenario: GuardScenario) : Guarding =
    match scenario with
    | RecursesForeverUnguarded
    | SpinsForeverUnguarded -> GuardsOff
    | Returns
    | RecursesForever
    | SpinsForever
    | SpinsInsideACatchAll
    | SpinsInAHelper
    | BlocksOnAWait
    | SpinsInAStateMachine -> GuardsOn GuardPatcher.prepare

  let parse (argument: string) : Result<GuardScenario, string> =
    match all |> List.tryFind (fun scenario -> toArgument scenario = argument) with
    | Some scenario -> Result.Ok scenario
    | None -> Result.Error (sprintf "unknown guard scenario %s" argument)

/// A helper in another class with a loop in it, so the getter alone has no loop.
[<AbstractClass; Sealed>]
type Helpers =
  static member SpinHelper(n: int) : int =
    let mutable i = n
    while true do
      i <- i + 1
    i

/// What a compiler makes for an async method (marked as it marks its own), with a `MoveNext` that never ends. The F#
/// `task` builder makes one of these in a Release build and closures in a Debug build, so the child builds its own: the
/// rule under test is that a `MoveNext` is never guarded, and that has to be the same in both.
[<System.Runtime.CompilerServices.CompilerGenerated>]
type SpinningMachine() =
  interface IAsyncStateMachine with
    member _.MoveNext() =
      let mutable i = 0
      while true do
        i <- i + 1
    member _.SetStateMachine(_) = ()

/// The getters. Plain code, the way a user writes it.
type Victim() =
  static let neverSet = new ManualResetEventSlim(false)
  member _.Fine : int = 42
  /// Not a tail call: the stack grows until it ends.
  member this.Depth : int = 1 + this.Depth
  member _.Spin : int =
    let mutable i = 0
    while true do
      i <- i + 1
    i
  member _.SwallowingSpin : int =
    let mutable i = 0
    while true do
      try
        while true do
          i <- i + 1
      with _ -> ()
    i
  member _.ViaHelper : int = Helpers.SpinHelper 5
  member _.Blocked : int =
    neverSet.Wait()
    0
  member _.MachineSpin : int =
    let machine = SpinningMachine()
    (machine :> IAsyncStateMachine).MoveNext()
    0

/// What the parent reads. One line, `key=value` pairs, nothing else on it.
let private outcomeLine (scenario: GuardScenario) (evaluated: MemberEvaluated) : string =
  let result =
    match evaluated.Outcome with
    | Ok value -> sprintf "ok:%O" value
    | Error (MemberFailure.MemberThrew message) -> "threw:" + message.Replace('\n', ' ')
    | Error MemberFailure.MemberTimedOut -> "timed-out"
    | Error (MemberFailure.MemberNotContained why) -> "not-contained:" + why
  let trip =
    match evaluated.Guards.Trip with
    | GuardTrip.NotTripped -> "none"
    | GuardTrip.StackLimitReached -> "stack"
    | GuardTrip.LoopStopped -> "loop"
  sprintf "GUARDCHILD scenario=%s result=%s trip=%s guarded=%d pending=%d"
    (GuardScenario.toArgument scenario) result trip (GuardCoverage.guardedCount evaluated.Guards.Coverage) Guard.Pending

/// The deadline and grace a child uses: short, because a child that is stopped says so within milliseconds.
let private childLimits : Limits =
  { Deadline = TestTimeouts.blockedGetterBudget
    InterruptGrace = TestTimeouts.interruptGrace
    MaxAbandoned = 4 }

/// What running an argument list came to.
type ChildRun =
  /// This was a child: it ran its scenario and this is the exit code.
  | Ran of exitCode: int
  /// Not a child: the arguments are the test runner's.
  | NotAChild

let tryRun (argv: string[]) : ChildRun =
  match argv with
  | [| "--guard-child"; argument |] ->
    match GuardScenario.parse argument with
    | Result.Error message ->
      eprintfn "%s" message
      ChildRun.Ran 2
    | Result.Ok scenario ->
      let evaluator = Evaluator(childLimits, unfiltered, GuardScenario.guardingFor scenario)
      let property = typeof<Victim>.GetProperty (GuardScenario.getterName scenario)
      let evaluated = evaluator.RunGuarded property (box (Victim()))
      printfn "%s" (outcomeLine scenario evaluated)
      stdout.Flush()
      // A spinning thread that was given up on is a background thread: leaving the process does not wait for it.
      ChildRun.Ran 0
  | _ -> ChildRun.NotAChild
