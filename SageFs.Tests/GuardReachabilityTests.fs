/// Which methods a clicked getter can reach, and which of them may get guards. The rules are skip rules, each with its
/// own reason: code we do not own, async machines, dynamic methods, generics, methods with no body. The walk is bounded
/// in depth and in count, and past either bound a method is said to be unguarded, never silently dropped.
module SageFs.Tests.GuardReachabilityTests

open System
open System.Reflection
open System.Reflection.Emit
open System.Runtime.CompilerServices
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features
open SageFs.Tests.GuardFixtures

let private testAssembly = typeof<Chain>.Assembly

let private world = GuardReachability.worldFor [ testAssembly ] GuardPatcher.calleesOf

let private method (t: Type) (name: string) : MethodBase =
  t.GetMethod(name, BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance) :> MethodBase

let private names (methods: MethodBase list) : string list = methods |> List.map (fun m -> m.Name)

let private skipReasons (walk: Walk) : SkipReason list = walk.Skipped |> List.map snd

[<Tests>]
let guardReachabilityTests =
  testList "guard reachability (what a getter can reach)" [

    testCase "WHY - the getter comes first, then what it calls, breadth first" <| fun _ ->
      let walk = GuardReachability.walk world WalkBudget.product (method typeof<Chain> "Top")
      names walk.Eligible |> Expect.equal "top, middle, leaf" [ "Top"; "Middle"; "Leaf" ]
      walk.Skipped |> Expect.isEmpty "nothing was left out"

    testCase "WHY - two methods that call each other are each looked at once" <| fun _ ->
      let walk = GuardReachability.walk world WalkBudget.product (method typeof<Mutual> "Ping")
      names walk.Eligible |> Expect.equal "ping and pong" [ "Ping"; "Pong" ]

    testCase "WHY - a call into the framework is recorded as code SageFs does not own, and not followed" <| fun _ ->
      let walk = GuardReachability.walk world WalkBudget.product (method typeof<CallsFramework> "Go")
      names walk.Eligible |> Expect.equal "only the getter" [ "Go" ]
      skipReasons walk
      |> List.exists (function | SkipReason.NotOurCode assembly -> assembly.Contains "CoreLib" | _ -> false)
      |> Expect.isTrue "the framework call says whose it is"

    testCase "WHY - a call through an abstract method is not guarded, and the implementation it can land on is" <| fun _ ->
      let walk = GuardReachability.walk world WalkBudget.product (method typeof<CallsAbstract> "Go")
      names walk.Eligible |> Expect.contains "the override is reached" "Area"
      walk.Eligible |> List.exists (fun m -> m.DeclaringType = typeof<Square>) |> Expect.isTrue "it is Square's"
      walk.Skipped
      |> List.exists (fun (m, reason) -> m.DeclaringType = typeof<Shape> && reason = SkipReason.NoBody)
      |> Expect.isTrue "the abstract method has no body to guard"

    testCase "WHY - a call through an interface reaches the class that implements it" <| fun _ ->
      let walk = GuardReachability.walk world WalkBudget.product (method typeof<CallsInterface> "Go")
      walk.Eligible |> List.exists (fun m -> m.DeclaringType = typeof<Thing>) |> Expect.isTrue "Thing's Weigh is reached"

    testCase "WHY - a closure handed to a library function is reached, because making it is how its body runs" <| fun _ ->
      let walk = GuardReachability.walk world WalkBudget.product (method typeof<MakesClosure> "Go")
      walk.Eligible
      |> List.exists (fun m -> m.Name = "Invoke" && m.DeclaringType.Name.Contains "@")
      |> Expect.isTrue "the closure's Invoke is guarded"

    testCase "WHY - a generic method is not patched, and the row says so" <| fun _ ->
      let walk = GuardReachability.walk world WalkBudget.product (method typeof<Generics> "Use")
      names walk.Eligible |> Expect.equal "only the caller" [ "Use" ]
      skipReasons walk |> Expect.contains "the generic is named" SkipReason.Generic

    testCase "WHY - the MoveNext of an async or task state machine is skipped, wherever the compiler put it" <| fun _ ->
      let map = typeof<FakeMachine>.GetInterfaceMap typeof<IAsyncStateMachine>
      let moveNext = map.TargetMethods.[Array.IndexOf(map.InterfaceMethods, typeof<IAsyncStateMachine>.GetMethod "MoveNext")]
      GuardReachability.eligibility (fun _ -> true) moveNext
      |> Expect.equal "an explicit interface implementation is still the machine's MoveNext" (Eligibility.Ineligible SkipReason.AsyncStateMachine)

    testCase "WHY - a getter that makes a state machine reaches its MoveNext and does not guard it" <| fun _ ->
      let walk = GuardReachability.walk world WalkBudget.product (method typeof<BuildsMachine> "Go")
      names walk.Eligible |> List.head |> Expect.equal "the getter comes first" "Go"
      skipReasons walk |> Expect.contains "the machine is skipped" SkipReason.AsyncStateMachine
      walk.Skipped
      |> List.exists (fun (m, reason) -> m.Name.EndsWith "MoveNext" && reason = SkipReason.AsyncStateMachine)
      |> Expect.isTrue "and it is the MoveNext that is skipped"

    testCase "WHY - whatever the compiler makes of an F# task (a state machine in Release, closures in Debug), no MoveNext of one is guarded" <| fun _ ->
      let walk = GuardReachability.walk world WalkBudget.product (method typeof<MakesTask> "Go")
      walk.Eligible
      |> List.exists (fun m -> typeof<IAsyncStateMachine>.IsAssignableFrom m.DeclaringType && m.Name.EndsWith "MoveNext")
      |> Expect.isFalse "no state machine's MoveNext is among what gets guarded"

    testCase "WHY - a dynamic method has no identity to patch and is skipped" <| fun _ ->
      let dynamicMethod = DynamicMethod("dyn", typeof<int>, [||])
      dynamicMethod.GetILGenerator().Emit OpCodes.Ldc_I4_0
      dynamicMethod.GetILGenerator().Emit OpCodes.Ret
      GuardReachability.eligibility (fun _ -> true) dynamicMethod
      |> Expect.equal "dynamic" (Eligibility.Ineligible SkipReason.DynamicMethod)

    testCase "WHY - code in an assembly nobody asked us to own is skipped, named by assembly" <| fun _ ->
      match GuardReachability.eligibility (fun _ -> false) (method typeof<Chain> "Top") with
      | Eligibility.Ineligible (SkipReason.NotOurCode assembly) -> assembly |> Expect.equal "the assembly" (testAssembly.GetName().Name)
      | other -> failtestf "expected NotOurCode, got %A" other

    testCase "WHY - a method whose IL cannot be read is not guarded, with the reader's reason" <| fun _ ->
      let unreadable =
        { world with
            Callees =
              fun m ->
                match m.Name with
                | "Middle" -> CalleeRead.Unreadable "no IL here"
                | _ -> world.Callees m }
      let walk = GuardReachability.walk unreadable WalkBudget.product (method typeof<Chain> "Top")
      names walk.Eligible |> Expect.equal "the unreadable method and what only it calls are out" [ "Top" ]
      skipReasons walk |> Expect.contains "and the reason is the reader's" (SkipReason.UnreadableBody "no IL here")

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 } "WHY - the walk never guards more than the budget allows, never reaches deeper, and accounts for every method once"
      (Prop.forAll (Arb.fromGen (Gen.map2 (fun d m -> d, m) (Gen.choose (0, 5)) (Gen.choose (0, 5)))) (fun (depth, count) ->
        let budget = { MaxDepth = depth; MaxMethods = count }
        let walk = GuardReachability.walk world budget (method typeof<Chain> "Top")
        let everything = (walk.Eligible @ (walk.Skipped |> List.map fst)) |> List.map (fun m -> m.Name)
        let chainLength = 3
        List.length walk.Eligible = min (min (depth + 1) count) chainLength
        && List.length everything = List.length (List.distinct everything)
        && (walk.Skipped |> List.forall (fun (_, reason) -> reason = SkipReason.BudgetReached))))

    testCase "WHY - past the budget a method is said to be unguarded, not dropped" <| fun _ ->
      let walk = GuardReachability.walk world { MaxDepth = 1; MaxMethods = 64 } (method typeof<Chain> "Top")
      names walk.Eligible |> Expect.equal "two deep" [ "Top"; "Middle" ]
      skipReasons walk |> Expect.contains "the rest is named" SkipReason.BudgetReached
  ]
