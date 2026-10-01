/// What the live-values walk is allowed to run. In `Safe` mode it reads fields and runs a getter only when the
/// compiled body can do nothing (a field read, a constant, or straight-line arithmetic), so looking at a value
/// cannot hang, loop, overflow or change anything. Every other getter shows as "not evaluated", with why.
module SageFs.Tests.LiveValueGetterPolicyTests

open System
open System.Diagnostics
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs.Features.LiveValueTree

/// Counts how often a getter or a sequence ran, so a case can prove it did not. One per case: Expecto runs the
/// cases of a list in parallel, so a shared counter would count another case's runs.
type Counter() =
  let mutable count = 0
  member _.Count = count
  member _.Bump() : int =
    count <- count + 1
    count

/// One getter of each shape the classifier has to tell apart. None of the "bad" ones is ever read by a Safe walk.
type PolicyProbe(name: string, n: int, counter: Counter) =
  member _.Name = name
  member val Auto = 1 with get, set
  member _.Const = 42
  member _.Twice = n + n
  member this.Len = this.Name.Length
  member _.Next = counter.Bump()
  [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
  member _.Hidden = 7
  /// A self tail call: F# compiles it to a loop, so reading it never returns.
  member this.Loops : int = this.Loops

/// Only the effectful getter, for the `Everything` case: that mode really does run every getter, so a
/// type with a looping one would never return.
type EffectOnly(counter: Counter) =
  member _.Next = counter.Bump()

type Person = { Name: string; Age: int }

let private probeWith (counter: Counter) = box (PolicyProbe("x", 2, counter))

let private probe () = probeWith (Counter())

let private childOf (label: string) (node: LiveValueNode) =
  node.Children |> List.tryFind (fun c -> c.Label = label)

let private shapeOf (propertyName: string) =
  classifyGetter (typeof<PolicyProbe>.GetProperty propertyName)

let private notEvaluated (reason: NotEvaluatedReason) = NodeKind.NotEvaluated reason

[<Tests>]
let getterShapeTests =
  testList "getter shapes (read from the compiled body)" [

    testCase "WHY — a getter that returns a constructor-captured field is a field read, and names the field it reads" <| fun _ ->
      match shapeOf "Name" with
      | GetterShape.ReturnsField field ->
        typeof<PolicyProbe>.GetField(field, BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public)
        |> Expect.isNotNull "the name is a real field of the type"
      | other -> failtestf "expected a field read, got %A" other

    testCase "WHY — an auto-property is a field read too" <| fun _ ->
      match shapeOf "Auto" with
      | GetterShape.ReturnsField _ -> ()
      | other -> failtestf "expected a field read, got %A" other

    testCase "WHY — a getter that returns a literal is a constant" <| fun _ ->
      shapeOf "Const" |> Expect.equal "constant" GetterShape.ReturnsConstant

    testCase "WHY — arithmetic on fields with no call is straight-line and cannot do anything" <| fun _ ->
      shapeOf "Twice" |> Expect.equal "straight line" GetterShape.PureStraightLine

    testCase "WHY — a getter that calls another getter or any method is not provably harmless" <| fun _ ->
      shapeOf "Len" |> Expect.equal "calls other code" GetterShape.CallsOtherCode
      shapeOf "Next" |> Expect.equal "has an effect, so it calls other code" GetterShape.CallsOtherCode

    testCase "WHY — a self-recursive getter compiles to a loop and is flagged as one" <| fun _ ->
      shapeOf "Loops" |> Expect.equal "contains a loop" GetterShape.ContainsLoop

    testCase "WHY — DebuggerBrowsable(Never) is the author saying do not show this" <| fun _ ->
      shapeOf "Hidden" |> Expect.equal "hidden by the author" GetterShape.HiddenByAuthor
  ]

[<Tests>]
let safeWalkTests =
  testList "Safe walk never runs a getter it cannot prove harmless" [

    testCase "WHY — a computed getter with an effect is not run, so looking at a value cannot change it" <| fun _ ->
      let counter = Counter()
      buildValueNodeIn WalkMode.Safe "p" (probeWith counter) |> ignore
      counter.Count |> Expect.equal "no getter ran" 0

    testCase "WHY — Everything mode is today's behavior and does run it, once" <| fun _ ->
      let counter = Counter()
      buildValueNodeIn WalkMode.Everything "p" (box (EffectOnly counter)) |> ignore
      counter.Count |> Expect.equal "the effectful getter ran once" 1

    testCase "WHY — a class is shown by its fields, which are its real state" <| fun _ ->
      let node = buildValueNodeIn WalkMode.Safe "p" (probe ())
      childOf "name" node |> Option.map (fun c -> c.Preview) |> Expect.equal "the captured field" (Some "\"x\"")
      childOf "Auto" node |> Option.map (fun c -> c.Preview) |> Expect.equal "the auto-property's field, under the property's name" (Some "1")

    testCase "WHY — a getter that only returns a shown field is not shown a second time" <| fun _ ->
      let node = buildValueNodeIn WalkMode.Safe "p" (probe ())
      childOf "Name" node |> Expect.isNone "the Name getter returns the name field, which is already shown"
      node.Children |> List.filter (fun c -> c.Label = "Auto") |> List.length |> Expect.equal "Auto appears once" 1

    testCase "WHY — a constant and straight-line getter are run, because their bodies can do nothing" <| fun _ ->
      let node = buildValueNodeIn WalkMode.Safe "p" (probe ())
      childOf "Const" node |> Option.map (fun c -> c.Preview) |> Expect.equal "constant" (Some "42")
      childOf "Twice" node |> Option.map (fun c -> c.Preview) |> Expect.equal "n + n" (Some "4")

    testCase "WHY — a getter that calls code is listed as not evaluated, with the reason" <| fun _ ->
      let node = buildValueNodeIn WalkMode.Safe "p" (probe ())
      childOf "Len" node |> Option.map (fun c -> c.Kind) |> Expect.equal "calls code" (Some (notEvaluated NotEvaluatedReason.GetterRunsCode))
      childOf "Next" node |> Option.map (fun c -> c.Kind) |> Expect.equal "has an effect" (Some (notEvaluated NotEvaluatedReason.GetterRunsCode))

    testCase "WHY — a getter that loops is listed as not evaluated, and says it loops" <| fun _ ->
      let node = buildValueNodeIn WalkMode.Safe "p" (probe ())
      childOf "Loops" node |> Option.map (fun c -> c.Kind) |> Expect.equal "loops" (Some (notEvaluated NotEvaluatedReason.GetterLoops))

    testCase "WHY — a property the author hid is not shown at all" <| fun _ ->
      childOf "Hidden" (buildValueNodeIn WalkMode.Safe "p" (probe ())) |> Expect.isNone "hidden"

    testCase "WHY — a lazy sequence is not enumerated, because enumerating it runs the code that makes it" <| fun _ ->
      let counter = Counter()
      let lazySeq = seq { yield counter.Bump() }
      let node = buildValueNodeIn WalkMode.Safe "s" (box lazySeq)
      node.Kind |> Expect.equal "not enumerated" (notEvaluated NotEvaluatedReason.SequenceNotEnumerated)
      counter.Count |> Expect.equal "the sequence never ran" 0

    testCase "WHY — collections that already hold their items are still shown" <| fun _ ->
      for value in [ box [ 1; 2; 3 ]; box [| 1; 2; 3 |]; box (ResizeArray [ 1; 2; 3 ]); box (Set.ofList [ 1; 2; 3 ]) ] do
        let node = buildValueNodeIn WalkMode.Safe "c" value
        node.Children |> List.length |> Expect.equal (sprintf "%s shows its three items" (value.GetType().Name)) 3

    testCase "WHY — Everything mode still enumerates a lazy sequence, as it always did" <| fun _ ->
      let counter = Counter()
      let node = buildValueNodeIn WalkMode.Everything "s" (box (seq { yield counter.Bump() }))
      node.Children |> List.length |> Expect.equal "one item shown" 1
      counter.Count |> Expect.equal "it ran once" 1

    testCase "WHY — a pending Task is shown by its status in every mode, never waited on" <| fun _ ->
      let pending = Threading.Tasks.TaskCompletionSource<int>().Task
      for mode in [ WalkMode.Safe; WalkMode.Everything; WalkMode.Off ] do
        (buildValueNodeIn mode "t" (box pending)).Preview |> Expect.stringContains (sprintf "%A shows the status" mode) "WaitingForActivation"
  ]

[<Tests>]
let offModeTests =
  testList "Off walk" [

    testCase "WHY — a class instance is collapsed and nothing of it is read" <| fun _ ->
      let counter = Counter()
      let node = buildValueNodeIn WalkMode.Off "p" (probeWith counter)
      node.Kind |> Expect.equal "collapsed" (notEvaluated NotEvaluatedReason.ClassesCollapsed)
      node.Children |> Expect.isEmpty "no children"
      counter.Count |> Expect.equal "nothing ran" 0

    testCase "WHY — records, unions, tuples, lists and maps look the same in every mode, because reading them runs no user code" <| fun _ ->
      let values : obj list =
        [ box { Name = "Ada"; Age = 37 }
          box (Some 3)
          box (1, "two", 3.0)
          box [ 1; 2; 3 ]
          box (Map.ofList [ "a", 1; "b", 2 ]) ]
      for value in values do
        let everything = buildValueNodeIn WalkMode.Everything "v" value
        buildValueNodeIn WalkMode.Safe "v" value |> Expect.equal "Safe matches Everything" everything
        buildValueNodeIn WalkMode.Off "v" value |> Expect.equal "Off matches Everything" everything
  ]

let private forced (path: string list) (calls: ResizeArray<string>) (outcome: Result<obj, MemberFailure>) =
  let run (property: PropertyInfo) (_target: obj) =
    calls.Add property.Name
    outcome
  { Mode = WalkMode.Safe; Force = ForcedMember.At (path, run) }

[<Tests>]
let forcedMemberTests =
  testList "forcing one member" [

    testCase "WHY — a click runs exactly the clicked getter, through the runner it is given, and shows the value" <| fun _ ->
      let counter = Counter()
      let calls = ResizeArray<string>()
      let node = buildValueNodeWith (forced [ "p"; "Len" ] calls (Ok (box 4))) "p" (probeWith counter)
      childOf "Len" node |> Option.map (fun c -> c.Preview) |> Expect.equal "the runner's value" (Some "4")
      calls |> Seq.toList |> Expect.equal "only Len was run" [ "Len" ]

    testCase "WHY — the other getters that run code stay unrun and still say why" <| fun _ ->
      let counter = Counter()
      let node = buildValueNodeWith (forced [ "p"; "Len" ] (ResizeArray()) (Ok (box 4))) "p" (probeWith counter)
      childOf "Next" node |> Option.map (fun c -> c.Kind) |> Expect.equal "still held" (Some (notEvaluated NotEvaluatedReason.GetterRunsCode))
      counter.Count |> Expect.equal "the effectful getter did not run" 0

    testCase "WHY — a click that times out is shown as unknown, with the reason, never as empty" <| fun _ ->
      let node = buildValueNodeWith (forced [ "p"; "Len" ] (ResizeArray()) (Error MemberFailure.MemberTimedOut)) "p" (probe ())
      childOf "Len" node |> Option.map (fun c -> c.Kind) |> Expect.equal "timed out" (Some (notEvaluated NotEvaluatedReason.EvaluationTimedOut))

    testCase "WHY — a getter that throws is shown with what it threw" <| fun _ ->
      let node = buildValueNodeWith (forced [ "p"; "Len" ] (ResizeArray()) (Error (MemberFailure.MemberThrew "boom"))) "p" (probe ())
      let child = childOf "Len" node
      child |> Option.map (fun c -> c.Kind) |> Expect.equal "threw" (Some (notEvaluated (NotEvaluatedReason.EvaluationThrew "boom")))
      child |> Option.map (fun c -> c.Preview) |> Option.defaultValue "" |> Expect.stringContains "the message is on the row" "boom"

    testCase "WHY — a refusal to contain the getter says why it was not run" <| fun _ ->
      let node = buildValueNodeWith (forced [ "p"; "Len" ] (ResizeArray()) (Error (MemberFailure.MemberNotContained "no sandbox here"))) "p" (probe ())
      childOf "Len" node |> Option.map (fun c -> c.Kind)
      |> Expect.equal "not contained" (Some (notEvaluated (NotEvaluatedReason.EvaluationNotContained "no sandbox here")))

    testCase "WHY — a path that names nothing runs nothing" <| fun _ ->
      let calls = ResizeArray<string>()
      buildValueNodeWith (forced [ "p"; "NoSuchMember" ] calls (Ok (box 1))) "p" (probe ()) |> ignore
      buildValueNodeWith (forced [ "other"; "Len" ] calls (Ok (box 1))) "p" (probe ()) |> ignore
      calls |> Seq.toList |> Expect.isEmpty "the runner was never called"

    testCase "WHY — with nothing forced the walk is exactly the Safe walk" <| fun _ ->
      let safe = buildValueNodeIn WalkMode.Safe "p" (probe ())
      buildValueNodeWith { Mode = WalkMode.Safe; Force = ForcedMember.Nothing } "p" (probe ()) |> Expect.equal "same tree" safe

    testCase "WHY — a click finds a member inside a record that holds the object" <| fun _ ->
      let calls = ResizeArray<string>()
      let holder = box (Some (probe ()))
      let node = buildValueNodeWith (forced [ "h"; "Value"; "Len" ] calls (Ok (box 4))) "h" holder
      calls |> Seq.toList |> Expect.equal "Len inside Some(probe) was run" [ "Len" ]
      node |> ignore
  ]
