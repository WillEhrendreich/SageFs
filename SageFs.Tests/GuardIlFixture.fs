/// A tiny structured language of loops and try/catch blocks, three ways of running it: a plain interpreter (the
/// oracle), IL written for it (the original), and that IL after the guard weave. The weave must not change what a
/// program computes, so all three agree; and it must stop every loop, so a thread that was asked to stop never finishes
/// one that iterates.
module SageFs.Tests.GuardIlFixture

open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit
open SageFs.Features
open FsCheck
open FsCheck.FSharp

type Shape =
  /// acc <- acc + k
  | Add of int
  /// while (i < count) { body; i++ }. The jump back is an unconditional `br`.
  | PreLoop of count: int * body: Shape list
  /// do { body; i++ } while (i < count). The jump back is a conditional `blt`, taken only while i < count.
  | PostLoop of count: int * body: Shape list
  /// try { body } catch (Exception) { }
  | Swallow of body: Shape list
  /// Asks the running thread's own cell to stop, from inside the program: what the evaluator's watchdog does at the
  /// deadline, but at a known point. Everything after it runs with a stop pending.
  | StopHere

/// What a region mark means in this fixture's IL. The weaver never looks at it.
type FixtureRegion =
  | TryBegin
  | CatchBegin
  | TryEnd

/// What `StopHere` calls. The cell is per thread, so programs running on different test threads do not meet.
[<AbstractClass; Sealed>]
type Tripwire =
  [<ThreadStatic; DefaultValue>]
  static val mutable private cell: GuardCell
  static member Arm(cell: GuardCell) : unit = Tripwire.cell <- cell
  static member Fire() : unit =
    match Tripwire.cell with
    | null -> ()
    | armed -> Guard.RequestStop armed

let private fire : MethodInfo = typeof<Tripwire>.GetMethod "Fire"

/// The largest loop count the IL is written with, so a generated program always ends.
let private bounded (n: int) = max 0 (min n 20)

/// The oracle: what the program computes, and how many times a loop body ran.
let rec private interpret (shapes: Shape list) (acc: int) (iterations: int) : int * int =
  shapes
  |> List.fold
       (fun (a, n) shape ->
         match shape with
         | Add k -> a + k, n
         | PreLoop (count, body) ->
           [ 1 .. bounded count ] |> List.fold (fun (a2, n2) _ -> interpret body a2 (n2 + 1)) (a, n)
         | PostLoop (count, body) ->
           [ 1 .. max 1 (bounded count) ] |> List.fold (fun (a2, n2) _ -> interpret body a2 (n2 + 1)) (a, n)
         | Swallow body -> interpret body a n
         | StopHere -> a, n)
       (acc, iterations)

let run (shapes: Shape list) (x: int) : int = fst (interpret shapes x 0)

/// How many times a loop body ran, which is how many times a back-edge check runs (a post-test loop checks before the
/// jump whether or not it is taken).
let loopIterations (shapes: Shape list) : int = snd (interpret shapes 0 0)

/// The IL for a program: one int argument, one int result. Locals: 0 is the accumulator, then one counter per loop.
let compile (shapes: Shape list) : IlInstruction list =
  let out = ResizeArray<IlInstruction>()
  let mutable nextLabel = 0
  let mutable nextLocal = 1
  let newLabel () =
    nextLabel <- nextLabel + 1
    IlLabel nextLabel
  let newLocal () =
    nextLocal <- nextLocal + 1
    nextLocal - 1
  let instruction (op: OpCode) (operand: obj) : IlInstruction =
    { Marks = []; Opens = []; Closes = []; Opcode = op; Operand = operand; Target = IlTarget.NoJump }
  let emit (op: OpCode) (operand: obj) = out.Add(instruction op operand)
  let jump (op: OpCode) (label: IlLabel) = out.Add { instruction op null with Target = IlTarget.Jump label }
  let rec statements (body: Shape list) =
    for shape in body do
      match shape with
      | Add k ->
        emit OpCodes.Ldloc 0
        emit OpCodes.Ldc_I4 k
        emit OpCodes.Add null
        emit OpCodes.Stloc 0
      | PreLoop (count, inner) ->
        let counter = newLocal ()
        let head = newLabel ()
        let exit = newLabel ()
        emit OpCodes.Ldc_I4 0
        emit OpCodes.Stloc counter
        let headAt = out.Count
        emit OpCodes.Ldloc counter
        emit OpCodes.Ldc_I4 (bounded count)
        jump OpCodes.Bge exit
        out.[headAt] <- { out.[headAt] with Marks = head :: out.[headAt].Marks }
        statements inner
        emit OpCodes.Ldloc counter
        emit OpCodes.Ldc_I4 1
        emit OpCodes.Add null
        emit OpCodes.Stloc counter
        jump OpCodes.Br head
        let exitAt = out.Count
        emit OpCodes.Nop null
        out.[exitAt] <- { out.[exitAt] with Marks = exit :: out.[exitAt].Marks }
      | PostLoop (count, inner) ->
        let counter = newLocal ()
        let head = newLabel ()
        emit OpCodes.Ldc_I4 0
        emit OpCodes.Stloc counter
        let headAt = out.Count
        emit OpCodes.Nop null
        out.[headAt] <- { out.[headAt] with Marks = head :: out.[headAt].Marks }
        statements inner
        emit OpCodes.Ldloc counter
        emit OpCodes.Ldc_I4 1
        emit OpCodes.Add null
        emit OpCodes.Stloc counter
        emit OpCodes.Ldloc counter
        emit OpCodes.Ldc_I4 (max 1 (bounded count))
        jump OpCodes.Blt head
      | StopHere -> emit OpCodes.Call (box fire)
      | Swallow inner ->
        let beginAt = out.Count
        emit OpCodes.Nop null
        out.[beginAt] <- { out.[beginAt] with Opens = [ IlRegionMark (box TryBegin) ] }
        statements inner
        let catchAt = out.Count
        emit OpCodes.Nop null
        out.[catchAt] <- { out.[catchAt] with Opens = [ IlRegionMark (box CatchBegin) ] }
        emit OpCodes.Pop null
        let endAt = out.Count - 1
        out.[endAt] <- { out.[endAt] with Closes = [ IlRegionMark (box TryEnd) ] }
  emit OpCodes.Ldarg_0 null
  emit OpCodes.Stloc 0
  statements shapes
  emit OpCodes.Ldloc 0
  emit OpCodes.Ret null
  List.ofSeq out

/// How many locals a compiled program needs.
let private localCount (instructions: IlInstruction list) : int =
  instructions
  |> List.sumBy (fun i -> match i.Opcode = OpCodes.Stloc with | true -> 1 | false -> 0)

/// Writes the IL into a dynamic method (int -> int) and gives back the delegate. A program the runtime cannot JIT
/// throws here or when it is called, which is the "still verifiable enough" check.
let toDelegate (instructions: IlInstruction list) : Func<int, int> =
  let method = DynamicMethod("guard-fixture", typeof<int>, [| typeof<int> |], typeof<GuardCell>.Module, true)
  let il = method.GetILGenerator()
  let labels = Dictionary<IlLabel, Label>()
  let label (l: IlLabel) =
    match labels.TryGetValue l with
    | true, found -> found
    | false, _ ->
      let made = il.DefineLabel()
      labels.[l] <- made
      made
  for _ in 0 .. localCount instructions do il.DeclareLocal typeof<int> |> ignore
  for instruction in instructions do
    for mark in instruction.Marks do il.MarkLabel(label mark)
    for IlRegionMark region in instruction.Opens do
      match region :?> FixtureRegion with
      | TryBegin -> il.BeginExceptionBlock() |> ignore
      | CatchBegin -> il.BeginCatchBlock typeof<Exception>
      | TryEnd -> ()
    match instruction.Target with
    | IlTarget.Jump target -> il.Emit(instruction.Opcode, label target)
    | IlTarget.JumpTable targets -> il.Emit(instruction.Opcode, targets |> List.map label |> List.toArray)
    | IlTarget.NoJump ->
      match instruction.Operand with
      | null -> il.Emit instruction.Opcode
      | :? int as n when instruction.Opcode = OpCodes.Ldc_I4 -> il.Emit(instruction.Opcode, n)
      | :? int as n -> il.Emit(instruction.Opcode, int16 n)
      | :? MethodInfo as m -> il.Emit(instruction.Opcode, m)
      | other -> failwithf "the fixture does not know the operand %A" other
    for IlRegionMark region in instruction.Closes do
      match region :?> FixtureRegion with
      | TryEnd -> il.EndExceptionBlock()
      | TryBegin
      | CatchBegin -> ()
  method.CreateDelegate(typeof<Func<int, int>>) :?> Func<int, int>

let entryGuard : MethodInfo = typeof<Guard>.GetMethod "EnterEnsure"
let backEdgeGuard : MethodInfo = typeof<Guard>.GetMethod "Check"

/// The program after the guard weave.
let woven (shapes: Shape list) : IlInstruction list * WeaveReport =
  GuardIl.weave entryGuard backEdgeGuard (compile shapes)

/// Whether a catch-all can sit in the program. With one, a stop thrown inside its try is swallowed and the program may
/// finish, which is the user's own doing and not a hole in the guards.
type Swallowing =
  | MaySwallow
  | NeverSwallows

let rec private shapeGen (swallowing: Swallowing) (size: int) : Gen<Shape> =
  let leaf = Gen.map Add (Gen.choose (-5, 5))
  match size <= 0 with
  | true -> leaf
  | false ->
    let inner = Gen.listOfLength 2 (shapeGen swallowing (size / 2))
    let loops =
      [ 3, leaf
        2, Gen.map2 (fun n body -> PreLoop (n, body)) (Gen.choose (0, 5)) inner
        2, Gen.map2 (fun n body -> PostLoop (n, body)) (Gen.choose (1, 5)) inner ]
    match swallowing with
    | MaySwallow -> Gen.frequency (loops @ [ (2, Gen.map Swallow inner) ])
    | NeverSwallows -> Gen.frequency loops

let programGen (swallowing: Swallowing) : Gen<Shape list> =
  Gen.sized (fun size -> Gen.listOfLength 3 (shapeGen swallowing (min size 8)))

let programs (swallowing: Swallowing) : Arbitrary<Shape list> = Arb.fromGen (programGen swallowing)
