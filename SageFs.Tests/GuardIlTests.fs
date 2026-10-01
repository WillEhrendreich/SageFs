/// The pure half of the guard transpiler: where an entry guard and a back-edge guard go in a method body. The rule is
/// checked three ways at once. The IL it makes still computes what the program computes (against a plain interpreter),
/// every jump back and every entry has its guard (counted two ways), and a thread that was asked to stop does not get
/// to finish a loop.
module SageFs.Tests.GuardIlTests

open System
open System.Reflection.Emit
open System.Threading
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features
open SageFs.Features.MemberEvaluation
open SageFs.Tests.GuardIlFixture

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private isCallTo (target: System.Reflection.MethodInfo) (i: IlInstruction) =
  i.Opcode = OpCodes.Call && obj.ReferenceEquals(i.Operand, target)

let private isGuard (i: IlInstruction) = isCallTo entryGuard i || isCallTo backEdgeGuard i

/// When the thread is asked to stop.
type private StopWhen =
  /// Before it runs anything, so the first guard it meets is the one that stops it.
  | BeforeItRuns
  /// Only when the program says so (`StopHere`), so the first guards it meets let it through.
  | WhenTheProgramSays

/// What the program does on a thread that is bound to a cell and asked to stop.
let private runStopped (stopWhen: StopWhen) (f: Func<int, int>) (x: int) : Result<int, exn> =
  let cell = GuardCell()
  let result : Result<int, exn> ref = ref (Result.Error (InvalidOperationException "the thread did not run"))
  let body () =
    Guard.Bind cell
    Tripwire.Arm cell
    try
      try result.Value <- Result.Ok (f.Invoke x)
      with e -> result.Value <- Result.Error e
    finally
      Guard.Unbind()
  let thread = Thread(ThreadStart body, GetterStackBytes)
  match stopWhen with
  | BeforeItRuns -> Guard.RequestStop cell
  | WhenTheProgramSays -> ()
  try
    thread.Start()
    thread.Join TestTimeouts.patienceBrief |> Expect.isTrue "the program ended"
  finally
    Guard.Retire cell
  result.Value

let private plain : IlInstruction =
  { Marks = []; Opens = []; Closes = []; Opcode = OpCodes.Nop; Operand = null; Target = IlTarget.NoJump }

[<Tests>]
let guardIlTests =
  testList "guard transpiler (pure IL rewrite)" [

    testPropertyWithConfig cfg "WHY — the guarded program computes what the plain program computes, for any input, when nothing asks it to stop"
      (Prop.forAll (programs MaySwallow) (fun shapes ->
        Prop.forAll (Arb.fromGen (Gen.choose (-1000, 1000))) (fun x ->
          let guarded, _ = woven shapes
          let expected = run shapes x
          (toDelegate (compile shapes)).Invoke x = expected && (toDelegate guarded).Invoke x = expected)))

    testPropertyWithConfig cfg "WHY — one entry guard comes first and every jump back has a check before it, counted two ways"
      (Prop.forAll (programs MaySwallow) (fun shapes ->
        let original = compile shapes
        let guarded, report = woven shapes
        let backEdges = GuardIl.countBackEdges original
        let checks = guarded |> List.filter (isCallTo backEdgeGuard) |> List.length
        let entries = guarded |> List.filter (isCallTo entryGuard) |> List.length
        isCallTo entryGuard (List.head guarded)
        && entries = 1
        && checks = backEdges
        && report = { EntryGuards = 1; BackEdgeGuards = backEdges }
        && List.length guarded = List.length original + 1 + backEdges))

    testPropertyWithConfig cfg "WHY — nothing of the original is dropped or reordered, only guard calls are added"
      (Prop.forAll (programs MaySwallow) (fun shapes ->
        let original = compile shapes
        let guarded, _ = woven shapes
        let strip (instructions: IlInstruction list) =
          instructions |> List.filter (isGuard >> not) |> List.map (fun i -> i.Opcode.Name, i.Operand, i.Target)
        strip guarded = strip original))

    testPropertyWithConfig cfg "WHY — every label and every region mark of the original is still there exactly once"
      (Prop.forAll (programs MaySwallow) (fun shapes ->
        let original = compile shapes
        let guarded, _ = woven shapes
        let marks (instructions: IlInstruction list) = instructions |> List.collect (fun i -> i.Marks) |> List.sort
        let opens (instructions: IlInstruction list) = instructions |> List.collect (fun i -> i.Opens) |> List.length
        let closes (instructions: IlInstruction list) = instructions |> List.collect (fun i -> i.Closes) |> List.length
        marks guarded = marks original && opens guarded = opens original && closes guarded = closes original))

    testPropertyWithConfig cfg "WHY — a thread that was asked to stop is stopped at the entry guard, whatever the program is"
      (Prop.forAll (programs MaySwallow) (fun shapes ->
        let guarded, _ = woven shapes
        match runStopped BeforeItRuns (toDelegate guarded) 7 with
        | Result.Error (:? GuardAbortedException) -> true
        | _ -> false))

    testPropertyWithConfig cfg "WHY — a stop that arrives mid-run ends the first loop after it, and a program with no loop after it is not disturbed"
      (Prop.forAll (programs NeverSwallows) (fun shapes ->
        let guarded, _ = woven (StopHere :: shapes)
        match runStopped WhenTheProgramSays (toDelegate guarded) 7, loopIterations shapes with
        | Result.Ok value, 0 -> value = run shapes 7
        | Result.Error (:? GuardAbortedException), n -> n > 0
        | _ -> false))

    testPropertyWithConfig cfg "WHY — even with a catch-all in the program, the only thing a stop ever raises is the guard's own exception"
      (Prop.forAll (programs MaySwallow) (fun shapes ->
        let guarded, _ = woven (StopHere :: shapes)
        match runStopped WhenTheProgramSays (toDelegate guarded) 7 with
        | Result.Ok _ -> true
        | Result.Error (:? GuardAbortedException) -> true
        | Result.Error _ -> false))

    testCase "WHY — a catch-all inside a loop cannot swallow the stop: the loop's own check, outside the try, throws it again" <| fun _ ->
      let shapes = [ StopHere; PreLoop (5, [ Swallow [ PreLoop (5, [ Add 1 ]) ] ]) ]
      let guarded, _ = woven shapes
      match runStopped WhenTheProgramSays (toDelegate guarded) 0 with
      | Result.Error (:? GuardAbortedException) -> ()
      | other -> failtestf "expected the stop to get through the catch-all, got %A" other

    testCase "WHY — the same program, unguarded, runs straight through the catch-all: the guard is what stopped it" <| fun _ ->
      let shapes = [ StopHere; PreLoop (5, [ Swallow [ PreLoop (5, [ Add 1 ]) ] ]) ]
      match runStopped WhenTheProgramSays (toDelegate (compile shapes)) 0 with
      | Result.Ok value -> value |> Expect.equal "all 25 additions ran" 25
      | other -> failtestf "expected the unguarded program to finish, got %A" other

    testCase "WHY — a try that opens on the first instruction still opens after the entry guard, so the guard is outside it" <| fun _ ->
      let region = IlRegionMark (box TryBegin)
      let body = [ { plain with Opens = [ region ] }; { plain with Opcode = OpCodes.Ret } ]
      let guarded, _ = GuardIl.weave entryGuard backEdgeGuard body
      guarded.[0].Opens |> Expect.isEmpty "the entry guard opens nothing"
      guarded.[0].Marks |> Expect.isEmpty "the entry guard takes no label, so a jump to the top does not pay for it again"
      guarded.[1].Opens |> Expect.equal "the first real instruction keeps its region" [ region ]

    testCase "WHY — a catch handler that opens on a jump back opens on the guard, and a region that closes with the jump still does" <| fun _ ->
      let top = IlLabel 1
      let opens = IlRegionMark (box CatchBegin)
      let closes = IlRegionMark (box TryEnd)
      let body =
        [ { plain with Marks = [ top ] }
          { plain with Opens = [ opens ]; Closes = [ closes ]; Opcode = OpCodes.Leave; Target = IlTarget.Jump top } ]
      let guarded, _ = GuardIl.weave entryGuard backEdgeGuard body
      isCallTo backEdgeGuard guarded.[2] |> Expect.isTrue "the check is right before the jump"
      guarded.[2].Opens |> Expect.equal "the handler opens on the check" [ opens ]
      guarded.[3].Opens |> Expect.isEmpty "and not on the jump"
      guarded.[3].Closes |> Expect.equal "the jump still closes the region" [ closes ]

    testCase "WHY — a jump back to the very top of the method lands on the back-edge guard, not on the entry guard" <| fun _ ->
      let top = IlLabel 1
      let body =
        [ { plain with Marks = [ top ]; Opcode = OpCodes.Nop }
          { plain with Opcode = OpCodes.Br; Target = IlTarget.Jump top } ]
      let guarded, report = GuardIl.weave entryGuard backEdgeGuard body
      report |> Expect.equal "one of each" { EntryGuards = 1; BackEdgeGuards = 1 }
      isCallTo entryGuard guarded.[0] |> Expect.isTrue "the entry guard is first"
      guarded.[0].Marks |> Expect.isEmpty "and takes no label"
      guarded.[1].Marks |> Expect.equal "the original first instruction keeps the label" [ top ]
      isCallTo backEdgeGuard guarded.[2] |> Expect.isTrue "the check sits right before the jump"

    testCase "WHY — a jump forward gets no check, and a switch that jumps back gets one" <| fun _ ->
      let ahead = IlLabel 1
      let top = IlLabel 2
      let forward =
        [ { plain with Opcode = OpCodes.Br; Target = IlTarget.Jump ahead }
          { plain with Marks = [ ahead ]; Opcode = OpCodes.Ret } ]
      snd (GuardIl.weave entryGuard backEdgeGuard forward) |> Expect.equal "no check" { EntryGuards = 1; BackEdgeGuards = 0 }
      let backward =
        [ { plain with Marks = [ top ]; Opcode = OpCodes.Ldc_I4_0; Operand = null }
          { plain with Opcode = OpCodes.Switch; Target = IlTarget.JumpTable [ ahead; top ] }
          { plain with Marks = [ ahead ]; Opcode = OpCodes.Ret } ]
      snd (GuardIl.weave entryGuard backEdgeGuard backward) |> Expect.equal "the switch is a way back" { EntryGuards = 1; BackEdgeGuards = 1 }

    testCase "WHY — an empty body gets no guard at all" <| fun _ ->
      GuardIl.weave entryGuard backEdgeGuard [] |> Expect.equal "nothing to guard" ([], { EntryGuards = 0; BackEdgeGuards = 0 })
  ]
