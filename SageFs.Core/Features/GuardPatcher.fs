namespace SageFs.Features

// BCL, FSharp.Core and Harmony only, on purpose: compiled into SageFs.Core and embedded into the isolated FSI host (see
// the GuardPatcher entries in SageFs.Core.fsproj, FsiHostBuild.fs and FsiHost.fsproj), where Harmony is the host's own
// renamed copy. No new package.

open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit
open System.Threading
open HarmonyLib
open SageFs.Utils

/// What a click got: what is guarded and what is not, and the one way to take the guards off again. `Release` is safe to
/// call twice and from any thread.
type GuardLease =
  { Coverage: GuardCoverage
    Release: unit -> unit }

/// The two guard methods the woven code calls.
module internal GuardMethods =
  let entry : MethodInfo = typeof<Guard>.GetMethod "EnterEnsure"
  let backEdge : MethodInfo = typeof<Guard>.GetMethod "Check"

/// The Harmony half of the transpiler: Harmony's instructions into the pure model and back out.
module internal GuardTranspilation =

  let private isBegin (block: ExceptionBlock) : bool = block.blockType <> ExceptionBlockType.EndExceptionBlock

  let private isJump (opcode: OpCode) : bool =
    opcode.OperandType = OperandType.InlineBrTarget || opcode.OperandType = OperandType.ShortInlineBrTarget

  let weave (instructions: IEnumerable<CodeInstruction>) : IEnumerable<CodeInstruction> =
    let source = List<CodeInstruction>(instructions)
    let ids = Dictionary<Label, int>()
    let labels = Dictionary<int, Label>()
    let idOf (label: Label) : IlLabel =
      match ids.TryGetValue label with
      | true, id -> IlLabel id
      | false, _ ->
        let id = ids.Count
        ids.[label] <- id
        labels.[id] <- label
        IlLabel id
    let labelOf (IlLabel id) : Label = labels.[id]
    let region (block: ExceptionBlock) : IlRegionMark = IlRegionMark (box block)
    let toModel (instruction: CodeInstruction) : IlInstruction =
      let target, operand =
        match instruction.operand with
        | :? Label as label when isJump instruction.opcode -> IlTarget.Jump (idOf label), null
        | :? (Label[]) as table when instruction.opcode = OpCodes.Switch ->
          IlTarget.JumpTable (table |> Array.map idOf |> Array.toList), null
        | other -> IlTarget.NoJump, other
      { Marks = instruction.labels |> Seq.map idOf |> Seq.toList
        Opens = instruction.blocks |> Seq.filter isBegin |> Seq.map region |> Seq.toList
        Closes = instruction.blocks |> Seq.filter (isBegin >> not) |> Seq.map region |> Seq.toList
        Opcode = instruction.opcode
        Operand = operand
        Target = target }
    let toInstruction (model: IlInstruction) : CodeInstruction =
      let operand : obj =
        match model.Target with
        | IlTarget.NoJump -> model.Operand
        | IlTarget.Jump target -> box (labelOf target)
        | IlTarget.JumpTable targets -> box (targets |> List.map labelOf |> List.toArray)
      let made = CodeInstruction(model.Opcode, operand)
      made.labels.AddRange(model.Marks |> List.map labelOf)
      made.blocks.AddRange(model.Opens |> List.map (fun (IlRegionMark b) -> b :?> ExceptionBlock))
      made.blocks.AddRange(model.Closes |> List.map (fun (IlRegionMark b) -> b :?> ExceptionBlock))
      made
    match source.Count with
    | 0 -> source :> IEnumerable<CodeInstruction>
    | _ ->
      let woven, _ = GuardIl.weave GuardMethods.entry GuardMethods.backEdge (source |> Seq.map toModel |> Seq.toList)
      woven |> List.map toInstruction :> IEnumerable<CodeInstruction>

/// The Harmony transpiler: the static method Harmony calls with a method's IL. Named and shaped the way Harmony wants.
type GuardTranspiler =
  static member Weave(instructions: IEnumerable<CodeInstruction>) : IEnumerable<CodeInstruction> =
    GuardTranspilation.weave instructions

/// Which methods have our patch on them, and for whom. A patch goes on when the first lease wants a method and comes
/// off when the last one lets go, so two clicks on getters that share a helper do not take each other's guards off.
module internal GuardPatches =

  [<RequireQualifiedAccess>]
  type Acquired =
    | Held
    | Refused of SkipReason

  let private harmony = lazy (Harmony "sagefs.getter-guards")
  let private gate = obj ()
  let private held = Dictionary<nativeint, MethodBase * HashSet<int>>()
  let private weaveMethod = lazy (HarmonyMethod(typeof<GuardTranspiler>.GetMethod "Weave"))

  let private identity (m: MethodBase) : nativeint =
    try m.MethodHandle.Value
    with _ -> 0n

  /// Takes our patch off `m`. The caller holds the gate. A patch that will not come off stays on, which is harmless
  /// (the guards only ever throw on a thread that was asked to stop) and is said once.
  let private unpatch (m: MethodBase) : unit =
    try harmony.Value.Unpatch(m, HarmonyPatchType.Transpiler, harmony.Value.Id)
    with ex -> Log.warn "[Guards] could not take the guards off %s: %s" m.Name ex.Message

  /// `lease` wants `m` guarded. A method hot reload has re-pointed is refused here, under the same gate that
  /// `releaseBeforeDetour` takes, so a detour and a patch never interleave on one method.
  let acquire (lease: int) (m: MethodBase) : Acquired =
    lock gate (fun () ->
      match DetourLedger.resolve m with
      | Detour.DetouredTo _
      | Detour.DetouredElsewhere -> Acquired.Refused SkipReason.DetouredByHotReload
      | Detour.NotDetoured ->
        let key = identity m
        match held.TryGetValue key with
        | true, (_, owners) ->
          owners.Add lease |> ignore
          Acquired.Held
        | false, _ ->
          try
            harmony.Value.Patch(m, transpiler = weaveMethod.Value) |> ignore
            held.[key] <- (m, HashSet<int>([ lease ]))
            Acquired.Held
          with ex -> Acquired.Refused (SkipReason.PatchRefused ex.Message))

  /// `lease` is done with `m`. Idempotent. The patch comes off when no lease is left.
  let release (lease: int) (m: MethodBase) : unit =
    lock gate (fun () ->
      let key = identity m
      match held.TryGetValue key with
      | true, (patched, owners) ->
        owners.Remove lease |> ignore
        match owners.Count with
        | 0 ->
          held.Remove key |> ignore
          unpatch patched
        | _ -> ()
      | false, _ -> ())

  /// Hot reload is about to detour `m`: our patch comes off whoever holds it, and nothing goes back on. Taking the
  /// patch off AFTER the detour would put the old code back over it.
  let releaseBeforeDetour (m: MethodBase) : unit =
    DetourLedger.markDetoured m
    lock gate (fun () ->
      let key = identity m
      match held.TryGetValue key with
      | true, (patched, _) ->
        held.Remove key |> ignore
        unpatch patched
      | false, _ -> ())

  /// How many methods carry our patch right now.
  let count () : int = lock gate (fun () -> held.Count)

module GuardPatcher =

  let private callOpcodes = [ OpCodes.Call; OpCodes.Callvirt; OpCodes.Newobj; OpCodes.Ldftn; OpCodes.Ldvirtftn; OpCodes.Jmp ]

  /// The methods one method calls, constructs or takes a pointer to, from its IL, read by Harmony's reader.
  let calleesOf (m: MethodBase) : CalleeRead =
    try
      PatchProcessor.ReadMethodBody m
      |> Seq.choose (fun pair ->
        match pair.Value with
        | :? MethodBase as callee when List.contains pair.Key callOpcodes -> Some callee
        | _ -> None)
      |> Seq.toList
      |> CalleeRead.Callees
    with ex -> CalleeRead.Unreadable ex.Message

  /// How many methods carry our patch right now.
  let patchedCount () : int = GuardPatches.count ()

  /// Hot reload is about to detour `m`: the guards on it come off first. Hot reload calls this from `detourMethod`.
  let releaseBeforeDetour (m: MethodBase) : unit = GuardPatches.releaseBeforeDetour m

  let private nextLease = ref 0

  let private nameOf = GuardReachability.nameOf

  /// Guards everything `root` can reach in the given world, for as long as the lease lasts.
  let prepareWith (world: WalkWorld) (budget: WalkBudget) (root: MethodBase) : GuardLease =
    let lease = Interlocked.Increment(&nextLease.contents)
    let walk = GuardReachability.walk world budget root
    let acquired = List<MethodBase>()
    let refused = List<SkippedMethod>()
    let releaseAll () = for m in acquired.ToArray() do GuardPatches.release lease m
    try
      for m in walk.Eligible do
        match GuardPatches.acquire lease m with
        | GuardPatches.Acquired.Held -> acquired.Add m
        | GuardPatches.Acquired.Refused reason -> refused.Add { Method = nameOf m; Reason = reason }
      let skipped =
        (walk.Skipped |> List.map (fun (m, reason) -> { Method = nameOf m; Reason = reason })) @ List.ofSeq refused
      let coverage =
        match acquired.Count, skipped with
        | 0, first :: _ -> GuardCoverage.NotGuarded (NotGuardedReason.GetterSkipped first.Reason)
        | 0, [] -> GuardCoverage.NotGuarded (NotGuardedReason.PreparationFailed "the walk found nothing to guard")
        | _ -> GuardCoverage.Guarded { Methods = acquired |> Seq.map nameOf |> Seq.toList; Skipped = skipped }
      { Coverage = coverage; Release = releaseAll }
    with ex ->
      releaseAll ()
      { Coverage = GuardCoverage.NotGuarded (NotGuardedReason.PreparationFailed ex.Message)
        Release = ignore }

  /// The method that really runs when `getter` is read on `target`: the most derived override, not the declaration.
  let implementationFor (target: obj) (getter: MethodInfo) : MethodInfo =
    let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly
    let declaration = getter.GetBaseDefinition()
    let rec search (t: Type) : MethodInfo =
      match t with
      | null -> getter
      | _ ->
        match t.GetMethods flags |> Array.tryFind (fun candidate -> candidate.IsVirtual && candidate.GetBaseDefinition() = declaration) with
        | Some found -> found
        | None -> search t.BaseType
    match getter.IsVirtual, target with
    | true, null -> getter
    | true, _ -> search (target.GetType())
    | false, _ -> getter

  /// Code we own: the getter's own assembly, and everything FSI has defined (each FSI submission is a dynamic assembly).
  let private ownedBy (root: MethodInfo) : Assembly list =
    root.DeclaringType.Assembly
    :: (AppDomain.CurrentDomain.GetAssemblies() |> Array.filter (fun a -> a.IsDynamic) |> Array.toList)

  /// Guards what clicking this property on this value can reach.
  let prepare (property: PropertyInfo) (target: obj) : GuardLease =
    match property.GetGetMethod true with
    | null -> { Coverage = GuardCoverage.NotGuarded (NotGuardedReason.PreparationFailed "the property has no getter"); Release = ignore }
    | getter ->
      let root = implementationFor target getter
      prepareWith (GuardReachability.worldFor (ownedBy root) calleesOf) WalkBudget.product root
