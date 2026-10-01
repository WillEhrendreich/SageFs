namespace SageFs.Features

// Dependency-free apart from System.Reflection.Emit.OpCode, on purpose: this is the pure half of the guard
// transpiler. It is compiled into SageFs.Core and embedded into the isolated FSI host (see the GuardIl entries in
// SageFs.Core.fsproj, FsiHostBuild.fs and FsiHost.fsproj). Harmony's CodeInstruction is read into this model at the
// edge (GuardPatcher.fs) and written back out of it, so the rule that decides where a guard goes is a plain function
// over a list that FsCheck can generate.

open System.Reflection.Emit

/// A place in a method body. The reader numbers them; two instructions that name the same number mean the same place.
type IlLabel = IlLabel of int

/// What a branch instruction jumps to.
[<RequireQualifiedAccess>]
type IlTarget =
  /// Not a branch: the operand, if any, is opaque to the weaver.
  | NoJump
  | Jump of IlLabel
  /// A `switch`: it jumps to one of several places.
  | JumpTable of IlLabel list

/// What the reader attached to an instruction to open or close an exception region. The weaver never looks inside, it
/// only moves it, so a guard that has to sit first in a catch handler still sits first.
type IlRegionMark = IlRegionMark of obj

/// One instruction as the reader found it.
type IlInstruction =
  { /// Every place that jumps here lands on this instruction.
    Marks: IlLabel list
    /// Regions that begin (try, catch, filter, finally) as this instruction starts. Written out before it.
    Opens: IlRegionMark list
    /// Regions that end with this instruction. Written out after it.
    Closes: IlRegionMark list
    Opcode: OpCode
    /// The instruction's own operand, whatever it is. Branch targets are in `Target`, not here.
    Operand: obj
    Target: IlTarget }

/// What a weave added.
type WeaveReport =
  { EntryGuards: int
    BackEdgeGuards: int }

module GuardIl =

  let private jumpsTo (instruction: IlInstruction) : IlLabel list =
    match instruction.Target with
    | IlTarget.NoJump -> []
    | IlTarget.Jump label -> [ label ]
    | IlTarget.JumpTable labels -> labels

  /// A jump back to somewhere already passed: it can run forever. A branch to a place not yet seen is a way forward.
  let private isBackEdge (seen: Set<IlLabel>) (instruction: IlInstruction) : bool =
    jumpsTo instruction |> List.exists seen.Contains

  /// A call to a guard: static, no arguments, nothing left on the stack, so it can go anywhere an instruction can.
  let private callTo (guard: obj) (marks: IlLabel list) (opens: IlRegionMark list) : IlInstruction =
    { Marks = marks
      Opens = opens
      Closes = []
      Opcode = OpCodes.Call
      Operand = guard
      Target = IlTarget.NoJump }

  /// Puts `entry` before the first instruction and `backEdge` before every jump that goes back.
  ///
  /// - The entry guard comes first and takes nothing from the instruction after it, so a loop that jumps back to the
  ///   top of the method does not pay for it again on every turn, and a `try` that opens on the first instruction still
  ///   opens after it.
  /// - A back-edge guard takes the marks and the opening regions of the jump it guards. Whoever jumped to that jump now
  ///   lands on the guard, and a handler that opens on that jump opens on the guard, so the guard is inside the region
  ///   the jump was in. A region that closes with the jump still closes with the jump.
  /// - Both are calls with no arguments, so the stack at the jump is exactly what it was.
  let weave (entry: obj) (backEdge: obj) (instructions: IlInstruction list) : IlInstruction list * WeaveReport =
    let rec go (seen: Set<IlLabel>) (backEdges: int) (acc: IlInstruction list) (rest: IlInstruction list) =
      match rest with
      | [] -> List.rev acc, backEdges
      | instruction :: tail ->
        let seen' = instruction.Marks |> List.fold (fun s l -> Set.add l s) seen
        match isBackEdge seen' instruction with
        | true ->
          let guard = callTo backEdge instruction.Marks instruction.Opens
          let stripped = { instruction with Marks = []; Opens = [] }
          go seen' (backEdges + 1) (stripped :: guard :: acc) tail
        | false -> go seen' backEdges (instruction :: acc) tail
    match instructions with
    | [] -> [], { EntryGuards = 0; BackEdgeGuards = 0 }
    | _ ->
      let woven, backEdges = go Set.empty 0 [] instructions
      callTo entry [] [] :: woven, { EntryGuards = 1; BackEdgeGuards = backEdges }

  /// How many jumps in this body go back. What `weave` must have guarded, counted a second way.
  let countBackEdges (instructions: IlInstruction list) : int =
    let _, count =
      instructions
      |> List.fold
           (fun (seen, count) instruction ->
             let seen' = instruction.Marks |> List.fold (fun s l -> Set.add l s) seen
             let count' = match isBackEdge seen' instruction with | true -> count + 1 | false -> count
             seen', count')
           (Set.empty, 0)
    count
