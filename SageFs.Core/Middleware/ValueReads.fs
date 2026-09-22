/// Where a module value's copies went, read out of the code that read it.
///
/// Rule 2 of hot reload (hot-reload-state-spec.md): a redefined immutable value
/// gets its new value, but ONLY if nothing in the running app kept a copy of the
/// old one. Patching the value's getter is easy. The hard part is knowing that
/// nobody copied the old value, and a wrong answer is a fake "Patched".
///
/// This module is the pure half of that answer:
///
///   * the IL of a method that reads the value says what it did with it, one
///     read site at a time: threw it away (`pop`, or a local nothing reads), or
///     let it escape (a field, a new object, an argument, a return, ...);
///   * a ledger records which of those readers have actually run, fed by the
///     runtime half (ValueReadTracking.fs, which owns the Harmony patches);
///   * the evidence for a value is the two put together, and the verdict is
///     SafeToPatch only when every read that happened threw the value away.
///
/// A read that escaped and has run is a capture, whenever it happened. A read
/// that escapes but whose method hasn't run yet is fine: when it runs, it reads
/// through the (patched) getter and gets the new value. That's the whole rule,
/// and it's why a `lazy` forced after startup is caught: the thunk returned the
/// value to the Lazy that cached it, and the thunk ran.
///
/// BCL + FSharp.Core only: this file is compiled into the isolated FSI host too
/// (FsiHost.fsproj), and its evidence types travel over FsiProtocol.
module SageFs.Middleware.ValueReads

open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit

// ── IL ────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type Operand =
  | NoOperand
  | Token of token: int
  /// A local or argument index (the opcode says which).
  | Variable of index: int
  | Target of offset: int
  | Targets of offsets: int list
  /// A number or string the classifier never needs.
  | Literal

type Instr = { Offset: int; Op: OpCode; Operand: Operand }

[<RequireQualifiedAccess>]
type DecodeError =
  | UnknownOpcode of offset: int * code: int
  | Truncated of offset: int

let private opcodes : Dictionary<int, OpCode> =
  let table = Dictionary<int, OpCode>()
  for field in typeof<OpCodes>.GetFields(BindingFlags.Public ||| BindingFlags.Static) do
    match field.GetValue null with
    | :? OpCode as op -> table.[int (uint16 op.Value)] <- op
    | _ -> ()
  table

/// The locals `ldloc.N`/`stloc.N` name without an operand.
let private implicitLocal (op: OpCode) : Operand =
  if op = OpCodes.Ldloc_0 || op = OpCodes.Stloc_0 then Operand.Variable 0
  elif op = OpCodes.Ldloc_1 || op = OpCodes.Stloc_1 then Operand.Variable 1
  elif op = OpCodes.Ldloc_2 || op = OpCodes.Stloc_2 then Operand.Variable 2
  elif op = OpCodes.Ldloc_3 || op = OpCodes.Stloc_3 then Operand.Variable 3
  else Operand.NoOperand

/// Decode a method body (`MethodBody.GetILAsByteArray`) into instructions.
let decode (il: byte[]) : Result<Instr list, DecodeError> =
  let n = il.Length
  let rec go (pos: int) (acc: Instr list) =
    match pos >= n with
    | true -> Ok(List.rev acc)
    | false ->
      let code, size =
        match il.[pos] = 0xFEuy && pos + 1 < n with
        | true -> 0xFE00 ||| int il.[pos + 1], 2
        | false -> int il.[pos], 1
      match opcodes.TryGetValue code with
      | false, _ -> Error(DecodeError.UnknownOpcode(pos, code))
      | true, op ->
        let at = pos + size
        let fits width = at + width <= n
        let int32At k = BitConverter.ToInt32(il, k)
        let parsed : Result<Operand * int, DecodeError> =
          match op.OperandType with
          | OperandType.InlineNone -> Ok(implicitLocal op, 0)
          | OperandType.ShortInlineVar when fits 1 -> Ok(Operand.Variable(int il.[at]), 1)
          | OperandType.InlineVar when fits 2 -> Ok(Operand.Variable(int (BitConverter.ToUInt16(il, at))), 2)
          | OperandType.ShortInlineBrTarget when fits 1 -> Ok(Operand.Target(at + 1 + int (sbyte il.[at])), 1)
          | OperandType.InlineBrTarget when fits 4 -> Ok(Operand.Target(at + 4 + int32At at), 4)
          | OperandType.InlineSwitch when fits 4 ->
            let count = int32At at
            let width = 4 + 4 * count
            match count >= 0 && fits width with
            | false -> Error(DecodeError.Truncated pos)
            | true ->
              let baseOffset = at + width
              Ok(Operand.Targets [ for k in 0 .. count - 1 -> baseOffset + int32At (at + 4 + 4 * k) ], width)
          | OperandType.InlineField
          | OperandType.InlineMethod
          | OperandType.InlineType
          | OperandType.InlineTok
          | OperandType.InlineSig when fits 4 -> Ok(Operand.Token(int32At at), 4)
          | OperandType.InlineString
          | OperandType.InlineI
          | OperandType.ShortInlineR when fits 4 -> Ok(Operand.Literal, 4)
          | OperandType.InlineI8
          | OperandType.InlineR when fits 8 -> Ok(Operand.Literal, 8)
          | OperandType.ShortInlineI when fits 1 -> Ok(Operand.Literal, 1)
          | _ -> Error(DecodeError.Truncated pos)
        match parsed with
        | Error e -> Error e
        | Ok(operand, width) -> go (at + width) ({ Offset = pos; Op = op; Operand = operand } :: acc)
  go 0 []

// ── what the classifier needs to know about a token ───────────────────────────

[<RequireQualifiedAccess>]
type Receiver =
  | Static
  | Instance

[<RequireQualifiedAccess>]
type Returns =
  | Nothing
  | AValue

/// A method or constructor a call site names, as far as the stack is concerned.
type Callee =
  { Name: string
    DeclaringType: string
    Parameters: int
    Receiver: Receiver
    Returns: Returns }

/// How a method's tokens resolve. Injected so the classifier runs over hand-built
/// instructions (the simulation) and over real method bodies (reflection) alike.
type Tokens =
  { Method: int -> Result<Callee, string>
    Member: int -> string }

// ── where one read went ───────────────────────────────────────────────────────

/// What a read did with the value when it didn't throw it away.
[<RequireQualifiedAccess>]
type Escape =
  | StoredInField of field: string
  /// Passed to `newobj`: a closure, a tuple, a record... anything built around it.
  | CapturedBy of typeName: string
  | PassedTo of methodName: string
  /// Handed back to whoever called the reader.
  | Returned
  | AddressTaken
  /// Anything the classifier doesn't follow. Counted as an escape, because a
  /// false "captured" costs a restart and a false "Patched" is a lie.
  | Untraced of what: string

[<RequireQualifiedAccess>]
type ReadFate =
  /// Popped, or stored in a local that nothing ever reads.
  | Discarded
  | Escaped of escape: Escape

[<RequireQualifiedAccess>]
type private LocalUse =
  | Load of index: int
  | Address of index: int
  | Store of index: int
  | NotLocal

let private localUse (i: Instr) : LocalUse =
  match i.Operand with
  | Operand.Variable n ->
    let op = i.Op
    if op = OpCodes.Ldloc_0 || op = OpCodes.Ldloc_1 || op = OpCodes.Ldloc_2 || op = OpCodes.Ldloc_3 || op = OpCodes.Ldloc_S || op = OpCodes.Ldloc then LocalUse.Load n
    elif op = OpCodes.Ldloca_S || op = OpCodes.Ldloca then LocalUse.Address n
    elif op = OpCodes.Stloc_0 || op = OpCodes.Stloc_1 || op = OpCodes.Stloc_2 || op = OpCodes.Stloc_3 || op = OpCodes.Stloc_S || op = OpCodes.Stloc then LocalUse.Store n
    else LocalUse.NotLocal
  | _ -> LocalUse.NotLocal

/// What an instruction does to the evaluation stack.
[<RequireQualifiedAccess>]
type private StackEffect =
  | Moves of pops: int * pushes: int
  | Unknown of why: string

let private fixedPops (b: StackBehaviour) : StackEffect =
  match b with
  | StackBehaviour.Pop0 -> StackEffect.Moves(0, 0)
  | StackBehaviour.Pop1
  | StackBehaviour.Popi
  | StackBehaviour.Popref -> StackEffect.Moves(1, 0)
  | StackBehaviour.Pop1_pop1
  | StackBehaviour.Popi_pop1
  | StackBehaviour.Popi_popi
  | StackBehaviour.Popi_popi8
  | StackBehaviour.Popi_popr4
  | StackBehaviour.Popi_popr8
  | StackBehaviour.Popref_pop1
  | StackBehaviour.Popref_popi -> StackEffect.Moves(2, 0)
  | StackBehaviour.Popi_popi_popi
  | StackBehaviour.Popref_popi_popi
  | StackBehaviour.Popref_popi_popi8
  | StackBehaviour.Popref_popi_popr4
  | StackBehaviour.Popref_popi_popr8
  | StackBehaviour.Popref_popi_popref
  | StackBehaviour.Popref_popi_pop1 -> StackEffect.Moves(3, 0)
  | other -> StackEffect.Unknown(string other)

let private fixedPushes (b: StackBehaviour) : StackEffect =
  match b with
  | StackBehaviour.Push0 -> StackEffect.Moves(0, 0)
  | StackBehaviour.Push1
  | StackBehaviour.Pushi
  | StackBehaviour.Pushi8
  | StackBehaviour.Pushr4
  | StackBehaviour.Pushr8
  | StackBehaviour.Pushref -> StackEffect.Moves(0, 1)
  | StackBehaviour.Push1_push1 -> StackEffect.Moves(0, 2)
  | other -> StackEffect.Unknown(string other)

let private isCall (op: OpCode) = op = OpCodes.Call || op = OpCodes.Callvirt

let private effectOf (tokens: Tokens) (i: Instr) : StackEffect =
  match i.Operand with
  | Operand.Token token when isCall i.Op || i.Op = OpCodes.Newobj ->
    match tokens.Method token with
    | Error why -> StackEffect.Unknown(sprintf "a call SageFs couldn't resolve (%s)" why)
    | Ok callee ->
      match i.Op = OpCodes.Newobj with
      | true -> StackEffect.Moves(callee.Parameters, 1)
      | false ->
        let receiver =
          match callee.Receiver with
          | Receiver.Static -> 0
          | Receiver.Instance -> 1
        let pushes =
          match callee.Returns with
          | Returns.Nothing -> 0
          | Returns.AValue -> 1
        StackEffect.Moves(callee.Parameters + receiver, pushes)
  | _ ->
    match fixedPops i.Op.StackBehaviourPop, fixedPushes i.Op.StackBehaviourPush with
    | StackEffect.Moves(pops, _), StackEffect.Moves(_, pushes) -> StackEffect.Moves(pops, pushes)
    | StackEffect.Unknown why, _
    | _, StackEffect.Unknown why -> StackEffect.Unknown(sprintf "%s (%s)" i.Op.Name why)

/// Control leaves the straight line here, so the value's consumer isn't simply
/// the next instruction that pops it. Not followed: the read counts as an escape.
let private leavesTheLine (op: OpCode) =
  match op.FlowControl with
  | FlowControl.Branch
  | FlowControl.Cond_Branch
  | FlowControl.Return
  | FlowControl.Throw -> true
  | _ -> false

/// How many locals deep a value is followed before the classifier gives up
/// (and calls it an escape).
[<Literal>]
let private maxLocalHops = 8

/// Follow the value a read instruction pushed (the one at `readIndex`) to
/// whatever consumes it.
///
/// The stack is simulated forward from the read: every instruction pops some
/// values and pushes some, and the first one that pops deeper than what was
/// pushed after the read is the one that takes our value. A branch before that
/// point, a call SageFs can't resolve, or running off the end all count as an
/// escape. A `stloc` is followed through every load of that local.
let fateAt (tokens: Tokens) (instrs: Instr[]) (readIndex: int) : ReadFate =
  let loadsOf (local: int) =
    instrs
    |> Array.indexed
    |> Array.choose (fun (j, i) ->
      match localUse i with
      | LocalUse.Load n when n = local -> Some(j, LocalUse.Load n)
      | LocalUse.Address n when n = local -> Some(j, LocalUse.Address n)
      | _ -> None)
    |> Array.toList
  let rec follow (hops: int) (following: Set<int>) (from: int) : ReadFate =
    let rec walk (index: int) (above: int) =
      match index >= instrs.Length with
      | true -> ReadFate.Escaped(Escape.Untraced "the method ended with the value still on the stack")
      | false ->
        let i = instrs.[index]
        match i.Op = OpCodes.Ret with
        | true ->
          match above with
          | 0 -> ReadFate.Escaped Escape.Returned
          | _ -> ReadFate.Escaped(Escape.Untraced "returned with other values on the stack")
        | false ->
          match effectOf tokens i with
          | StackEffect.Unknown why -> ReadFate.Escaped(Escape.Untraced why)
          | StackEffect.Moves(pops, _) when pops > above -> consume hops following i
          | StackEffect.Moves _ when leavesTheLine i.Op ->
            ReadFate.Escaped(Escape.Untraced(sprintf "%s before the value was used" i.Op.Name))
          | StackEffect.Moves(pops, pushes) -> walk (index + 1) (above - pops + pushes)
    walk (from + 1) 0
  and consume (hops: int) (following: Set<int>) (i: Instr) : ReadFate =
    match localUse i with
    | LocalUse.Store local when following.Contains local ->
      // Already following this local's loads further up; this store adds none.
      ReadFate.Discarded
    | LocalUse.Store _ when hops >= maxLocalHops -> ReadFate.Escaped(Escape.Untraced "followed through too many locals")
    | LocalUse.Store local ->
      loadsOf local
      |> List.fold
           (fun fate (j, load) ->
             match fate, load with
             | ReadFate.Escaped _, _ -> fate
             | ReadFate.Discarded, LocalUse.Address _ -> ReadFate.Escaped Escape.AddressTaken
             | ReadFate.Discarded, _ -> follow (hops + 1) (following.Add local) j)
           ReadFate.Discarded
    | _ ->
      let token =
        match i.Operand with
        | Operand.Token t -> t
        | _ -> 0
      if i.Op = OpCodes.Pop then ReadFate.Discarded
      elif i.Op = OpCodes.Stfld || i.Op = OpCodes.Stsfld then ReadFate.Escaped(Escape.StoredInField(tokens.Member token))
      elif i.Op = OpCodes.Newobj then
        match tokens.Method token with
        | Ok callee -> ReadFate.Escaped(Escape.CapturedBy callee.DeclaringType)
        | Error why -> ReadFate.Escaped(Escape.Untraced why)
      elif isCall i.Op then
        match tokens.Method token with
        | Ok callee -> ReadFate.Escaped(Escape.PassedTo callee.Name)
        | Error why -> ReadFate.Escaped(Escape.Untraced why)
      else ReadFate.Escaped(Escape.Untraced i.Op.Name)
  follow 0 Set.empty readIndex

/// Which instructions are reads of the value being asked about.
[<RequireQualifiedAccess>]
type ReadKind =
  /// `call get_x`, the ordinary read.
  | GetterCall
  /// `ldsfld` of its backing field.
  | FieldLoad
  /// `ldsflda` of its backing field.
  | FieldAddress
  /// `ldftn get_x`: the getter itself escapes as a function pointer.
  | GetterPointer

[<RequireQualifiedAccess>]
type ReadMatch =
  | Reads of kind: ReadKind
  | NotARead

/// Every read of one value in one method body, with where it went.
let sitesIn (tokens: Tokens) (classify: Instr -> ReadMatch) (instrs: Instr[]) : (int * ReadFate) list =
  instrs
  |> Array.indexed
  |> Array.choose (fun (index, i) ->
    match classify i with
    | ReadMatch.NotARead -> None
    | ReadMatch.Reads ReadKind.GetterCall
    | ReadMatch.Reads ReadKind.FieldLoad -> Some(i.Offset, fateAt tokens instrs index)
    | ReadMatch.Reads ReadKind.FieldAddress -> Some(i.Offset, ReadFate.Escaped Escape.AddressTaken)
    | ReadMatch.Reads ReadKind.GetterPointer ->
      Some(i.Offset, ReadFate.Escaped(Escape.Untraced "its getter was turned into a delegate, which can run anywhere")))
  |> Array.toList

/// Token resolution for a real method body, through reflection.
let tokensOf (m: MethodBase) : Tokens =
  let typeArgs =
    match isNull m.DeclaringType with
    | true -> [||]
    | false when m.DeclaringType.IsGenericType -> m.DeclaringType.GetGenericArguments()
    | false -> [||]
  let methodArgs =
    match m.IsGenericMethod with
    | true -> m.GetGenericArguments()
    | false -> [||]
  let nameOf (t: Type) =
    match isNull t with
    | true -> "?"
    | false -> t.FullName |> Option.ofObj |> Option.defaultValue t.Name
  { Method =
      fun token ->
        try
          let resolved = m.Module.ResolveMethod(token, typeArgs, methodArgs)
          let returns =
            match resolved with
            | :? MethodInfo as mi when mi.ReturnType <> typeof<Void> -> Returns.AValue
            | _ -> Returns.Nothing
          Ok
            { Name = sprintf "%s.%s" (nameOf resolved.DeclaringType) resolved.Name
              DeclaringType = nameOf resolved.DeclaringType
              Parameters = resolved.GetParameters().Length
              Receiver =
                match resolved.IsStatic with
                | true -> Receiver.Static
                | false -> Receiver.Instance
              Returns = returns }
        with ex -> Error ex.Message
    Member =
      fun token ->
        try
          let resolved = m.Module.ResolveMember(token, typeArgs, methodArgs)
          sprintf "%s.%s" (nameOf resolved.DeclaringType) resolved.Name
        with _ -> sprintf "token 0x%08x" token }

// ── evidence ──────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type SiteLocation =
  | ILOffset of offset: int
  /// The reader's code has no read of the value at all: it got it through
  /// reflection, a delegate, or an inlined copy of the getter.
  | NotInItsCode

/// One read of a value, in one method.
type ReadSite =
  { Reader: string
    Where: SiteLocation
    Fate: ReadFate }

/// When SageFs saw the reader run.
[<RequireQualifiedAccess>]
type ReadSeen =
  /// While the app was starting (the startup window's getter watch saw it).
  | AtStartup
  /// After startup (its one-shot probe fired).
  | AfterStartup
  /// SageFs can't watch this reader run, so it counts as having run.
  | Unobservable of why: string

[<RequireQualifiedAccess>]
type ValueRead =
  | ThrownAway of site: ReadSite
  /// Lets the value escape, but hasn't run. When it does, it reads through the
  /// getter, so it gets whatever the getter returns then.
  | NotRunYet of site: ReadSite
  | Kept of site: ReadSite * seen: ReadSeen

/// Everything the running app knows about where one value's copies went.
[<RequireQualifiedAccess>]
type ValueEvidence =
  | Untracked of value: string * why: string
  | Tracked of value: string * reads: ValueRead list

[<RequireQualifiedAccess>]
type ValueVerdict =
  | SafeToPatch
  | HeldBy of site: ReadSite * seen: ReadSeen
  | CannotTell of why: string

/// SafeToPatch only when no read of the value was kept. The first kept read is
/// the one the restart reason names.
let verdictOf (evidence: ValueEvidence) : ValueVerdict =
  match evidence with
  | ValueEvidence.Untracked(_, why) -> ValueVerdict.CannotTell why
  | ValueEvidence.Tracked(_, reads) ->
    let kept =
      reads
      |> List.choose (function
        | ValueRead.Kept(site, seen) -> Some(site, seen)
        | ValueRead.ThrownAway _
        | ValueRead.NotRunYet _ -> None)
    match kept with
    | [] -> ValueVerdict.SafeToPatch
    | (site, seen) :: _ -> ValueVerdict.HeldBy(site, seen)

/// The value a piece of evidence is about.
let valueOf (evidence: ValueEvidence) : string =
  match evidence with
  | ValueEvidence.Untracked(value, _)
  | ValueEvidence.Tracked(value, _) -> value

/// Where a copy went, in one clause a restart reason can carry.
let describeHolder (site: ReadSite) (seen: ReadSeen) : string =
  let what =
    match site.Fate with
    | ReadFate.Discarded -> "read it and threw it away"
    | ReadFate.Escaped(Escape.StoredInField field) -> sprintf "stored it in %s" field
    | ReadFate.Escaped(Escape.CapturedBy typeName) -> sprintf "put it in a new %s" typeName
    | ReadFate.Escaped(Escape.PassedTo methodName) -> sprintf "passed it to %s" methodName
    | ReadFate.Escaped Escape.Returned -> "returned it to whatever called it"
    | ReadFate.Escaped Escape.AddressTaken -> "took its address"
    | ReadFate.Escaped(Escape.Untraced how) -> sprintf "used it in a way SageFs doesn't follow (%s)" how
  let where =
    match site.Where with
    | SiteLocation.ILOffset offset -> sprintf " (IL_%04x)" offset
    | SiteLocation.NotInItsCode -> " without a read of it in its own code (reflection, most likely)"
  let whenText =
    match seen with
    | ReadSeen.AtStartup -> "while the app started"
    | ReadSeen.AfterStartup -> "after the app started"
    | ReadSeen.Unobservable why -> sprintf "and SageFs can't tell whether that has run yet, so it assumes it has: %s" why
  sprintf "%s%s %s, %s" site.Reader where what whenText

// ── reflection reads ─────────────────────────────────────────────────────────

/// How a session watches module values read through reflection once the app
/// has started. A reflective read has no read of the value in the reader's own
/// IL, so the probes can't see it. During startup the getter watch sees it
/// either way; this is about everything after that. The costs are measured
/// (read-tracking-costs.md), on a loaded box, so treat them as orders of
/// magnitude.
[<RequireQualifiedAccess>]
type ReflectionReadMode =
  /// Every tracked getter keeps its watch for the app's life, and every read of
  /// it, reflective or not, walks the stack to find who's reading it. Exact
  /// about who read it and what they did with it. Costs 8 to 16 microseconds
  /// on EVERY read of a tracked value, plain reads in a hot loop included.
  | ExactEveryRead
  /// The first reflective read of a tracked value after startup marks it, and
  /// editing that value restarts the app. About 20 ns more on every reflective
  /// call in the process, nothing at all on plain reads. Can't say who read it.
  | MarkOnReflect
  /// The first reflective read from each calling method walks the stack once
  /// and rewires that method's reflection call, so its later reads name it for
  /// about 40 ns a read. Exact about who read it and what they did with it. A
  /// call it can't rewire (and a loop that never returns) walks every time,
  /// about 16 microseconds a read.
  | ProbeCallers

module ReflectionReadMode =
  let all = [ ReflectionReadMode.ExactEveryRead; ReflectionReadMode.MarkOnReflect; ReflectionReadMode.ProbeCallers ]

  /// The spike measured ProbeCallers' steady state at about 38 ns a read,
  /// against 9,000 for a walk per read, and it names the caller. So it's the
  /// default.
  let standard = ReflectionReadMode.ProbeCallers

  /// The one spelling used by config, the MCP tool and the dashboard.
  let name (mode: ReflectionReadMode) : string =
    match mode with
    | ReflectionReadMode.ExactEveryRead -> "exact-every-read"
    | ReflectionReadMode.MarkOnReflect -> "mark-on-reflect"
    | ReflectionReadMode.ProbeCallers -> "probe-callers"

  let parse (text: string) : Result<ReflectionReadMode, string> =
    let wanted = text.Trim().ToLowerInvariant()
    match all |> List.tryFind (fun mode -> name mode = wanted) with
    | Some mode -> Result.Ok mode
    | None -> Result.Error(sprintf "'%s' isn't a reflection read mode. Use one of: %s" text (all |> List.map name |> String.concat ", "))

  /// What the mode costs, in one line.
  let cost (mode: ReflectionReadMode) : string =
    match mode with
    | ReflectionReadMode.ExactEveryRead -> "every read of a tracked value walks the stack, about 8 to 16 us a read, plain reads included"
    | ReflectionReadMode.MarkOnReflect -> "reads stay fast, but a value read through reflection restarts the app when you edit it"
    | ReflectionReadMode.ProbeCallers -> "about 40 ns a reflective read once each caller is rewired, 16 us a read for a call it can't rewire"

  /// What picking the mode does, in one line.
  let consequence (mode: ReflectionReadMode) : string =
    match mode with
    | ReflectionReadMode.ExactEveryRead -> "exact about who read it, but slows every read of every tracked value"
    | ReflectionReadMode.MarkOnReflect -> "fastest, but any reflective read means an edit to that value restarts the app"
    | ReflectionReadMode.ProbeCallers -> "exact about who read it and nearly free after each caller's first read"

/// Who made a reflective read of a value, as far as SageFs knows.
[<RequireQualifiedAccess>]
type ReflectiveCaller =
  /// The method that made the reflection call, the IL offset of that call in
  /// its body, and what it did with what the call returned.
  | AtSite of reader: string * offset: int * fate: ReadFate
  /// SageFs didn't look for the caller (MarkOnReflect), or couldn't read the
  /// caller's code. Counts as a copy.
  | Unattributed of why: string

/// A reflective read, and whether it happened while the app was starting.
type ReflectiveSighting =
  { Caller: ReflectiveCaller
    Seen: ReadSeen }

// ── the ledger: which readers have run ───────────────────────────────────────

/// A reader method's identity, stable for the life of the process.
type ReaderId = string

/// A method that reads at least one tracked value, and what each read does.
type Reader =
  { Id: ReaderId
    Name: string
    /// (value, IL offset, fate) for every read in its body.
    Reads: (string * int * ReadFate) list }

[<RequireQualifiedAccess>]
type ReaderStatus =
  /// Watched, and hasn't run.
  | Armed
  | Ran of seen: ReadSeen
  | Unwatchable of why: string

/// The window in which every getter read is recorded with its caller.
[<RequireQualifiedAccess>]
type StartupWindow =
  | Open
  | Closed

[<RequireQualifiedAccess>]
type Caller =
  | Known of reader: ReaderId
  /// A caller whose code has no read of the value (reflection, say).
  | Unknown of name: string

[<RequireQualifiedAccess>]
type LedgerEvent =
  /// A value SageFs tracks.
  | ValueTracked of value: string
  /// A value SageFs can't track, and why (an optimized build, say).
  | ValueUntracked of value: string * why: string
  /// A reader was found and watched (`Armed`), or couldn't be (`Unwatchable`),
  /// or ran before anyone could watch it (`Ran`).
  | ReaderFound of reader: Reader * status: ReaderStatus
  /// Its one-shot probe fired: it has run.
  | ReaderRan of reader: ReaderId
  /// The startup window's getter watch saw `caller` read `value`.
  | GetterRead of value: string * caller: Caller
  /// The reflection watch saw `value` read through reflection (after startup,
  /// or during it when the getter watch couldn't go on).
  | ReflectiveRead of value: string * caller: ReflectiveCaller
  | StartupEnded

type Ledger =
  { Window: StartupWindow
    Values: Set<string>
    Untracked: Map<string, string>
    Readers: Map<ReaderId, Reader>
    Status: Map<ReaderId, ReaderStatus>
    /// Callers the getter watch saw read a value without a read in their code.
    UnknownReads: Map<string, string list>
    /// Reflective reads the reflection watch caught, per value. One sighting
    /// per caller: the first one sticks.
    ReflectiveReads: Map<string, ReflectiveSighting list> }

module Ledger =
  let empty : Ledger =
    { Window = StartupWindow.Open
      Values = Set.empty
      Untracked = Map.empty
      Readers = Map.empty
      Status = Map.empty
      UnknownReads = Map.empty
      ReflectiveReads = Map.empty }

  /// When a reader that just ran was seen: the window being open is what
  /// makes it a startup read.
  let private seenNow (ledger: Ledger) =
    match ledger.Window with
    | StartupWindow.Open -> ReadSeen.AtStartup
    | StartupWindow.Closed -> ReadSeen.AfterStartup

  /// A reader that ran. The first sighting sticks: once a reader has run it
  /// may hold a copy, and nothing later makes that untrue.
  let private ran (ledger: Ledger) (reader: ReaderId) : Ledger =
    match Map.tryFind reader ledger.Status with
    | Some(ReaderStatus.Ran _) -> ledger
    | Some ReaderStatus.Armed
    | Some(ReaderStatus.Unwatchable _)
    | None -> { ledger with Status = Map.add reader (ReaderStatus.Ran(seenNow ledger)) ledger.Status }

  let step (ledger: Ledger) (event: LedgerEvent) : Ledger =
    match event with
    | LedgerEvent.ValueTracked value -> { ledger with Values = Set.add value ledger.Values }
    | LedgerEvent.ValueUntracked(value, why) -> { ledger with Untracked = Map.add value why ledger.Untracked }
    | LedgerEvent.ReaderFound(reader, status) ->
      let status =
        // A probe can fire before the scan that found its reader is filed
        // (the runtime races them). A sighting is never downgraded.
        match Map.tryFind reader.Id ledger.Status, status with
        | Some(ReaderStatus.Ran seen), _ -> ReaderStatus.Ran seen
        | _, found -> found
      { ledger with
          Readers = Map.add reader.Id reader ledger.Readers
          Status = Map.add reader.Id status ledger.Status }
    | LedgerEvent.ReaderRan reader -> ran ledger reader
    | LedgerEvent.GetterRead(_, Caller.Known reader) ->
      // A getter read by a known reader is a sighting of that reader. It's
      // recorded even after the window closed: a watch removed while a read
      // was in flight still reports it, and more evidence never hurts.
      ran ledger reader
    | LedgerEvent.GetterRead(value, Caller.Unknown name) ->
      let callers = Map.tryFind value ledger.UnknownReads |> Option.defaultValue []
      match List.contains name callers with
      | true -> ledger
      | false -> { ledger with UnknownReads = Map.add value (callers @ [ name ]) ledger.UnknownReads }
    | LedgerEvent.ReflectiveRead(value, caller) ->
      let sightings = Map.tryFind value ledger.ReflectiveReads |> Option.defaultValue []
      match sightings |> List.exists (fun s -> s.Caller = caller) with
      | true -> ledger
      | false ->
        let sighting = { Caller = caller; Seen = seenNow ledger }
        { ledger with ReflectiveReads = Map.add value (sightings @ [ sighting ]) ledger.ReflectiveReads }
    | LedgerEvent.StartupEnded -> { ledger with Window = StartupWindow.Closed }

  /// Everything the ledger knows about one value, read by read.
  let evidence (ledger: Ledger) (value: string) : ValueEvidence =
    match Map.tryFind value ledger.Untracked, Set.contains value ledger.Values with
    | Some why, _ -> ValueEvidence.Untracked(value, why)
    | None, false ->
      ValueEvidence.Untracked(value, "it isn't a public module value of a compiled project assembly that SageFs is watching")
    | None, true ->
      let fromReaders =
        ledger.Readers
        |> Map.toList
        |> List.collect (fun (id, reader) ->
          let status = Map.tryFind id ledger.Status |> Option.defaultValue ReaderStatus.Armed
          reader.Reads
          |> List.filter (fun (v, _, _) -> v = value)
          |> List.map (fun (_, offset, fate) ->
            let site = { Reader = reader.Name; Where = SiteLocation.ILOffset offset; Fate = fate }
            match fate, status with
            | ReadFate.Discarded, _ -> ValueRead.ThrownAway site
            | ReadFate.Escaped _, ReaderStatus.Armed -> ValueRead.NotRunYet site
            | ReadFate.Escaped _, ReaderStatus.Ran seen -> ValueRead.Kept(site, seen)
            | ReadFate.Escaped _, ReaderStatus.Unwatchable why -> ValueRead.Kept(site, ReadSeen.Unobservable why)))
      let fromUnknownCallers =
        Map.tryFind value ledger.UnknownReads
        |> Option.defaultValue []
        |> List.map (fun caller ->
          ValueRead.Kept(
            { Reader = caller
              Where = SiteLocation.NotInItsCode
              Fate = ReadFate.Escaped(Escape.Untraced "it read the value without a read in its own code") },
            ReadSeen.AtStartup))
      let fromReflection =
        Map.tryFind value ledger.ReflectiveReads
        |> Option.defaultValue []
        |> List.map (fun sighting ->
          match sighting.Caller with
          | ReflectiveCaller.AtSite(reader, offset, fate) ->
            let site = { Reader = reader; Where = SiteLocation.ILOffset offset; Fate = fate }
            match fate with
            | ReadFate.Discarded -> ValueRead.ThrownAway site
            | ReadFate.Escaped _ -> ValueRead.Kept(site, sighting.Seen)
          | ReflectiveCaller.Unattributed why ->
            ValueRead.Kept(
              { Reader = "a reflective read"
                Where = SiteLocation.NotInItsCode
                Fate = ReadFate.Escaped(Escape.Untraced why) },
              sighting.Seen))
      ValueEvidence.Tracked(value, fromReaders @ fromUnknownCallers @ fromReflection)

// ── a save's decision ────────────────────────────────────────────────────────

/// What a save that redefines values may do, once it has the evidence.
[<RequireQualifiedAccess>]
type SaveCheck =
  | AllSafe
  | Refused of first: (string * ValueVerdict) * rest: (string * ValueVerdict) list

/// Every verdict has to be SafeToPatch. The evidence is taken twice around a
/// patch (before, to decide, and after, to catch a read that raced it), so this
/// is asked of both.
let checkSave (verdicts: (string * ValueVerdict) list) : SaveCheck =
  let refused =
    verdicts
    |> List.filter (fun (_, verdict) ->
      match verdict with
      | ValueVerdict.SafeToPatch -> false
      | ValueVerdict.HeldBy _
      | ValueVerdict.CannotTell _ -> true)
  match refused with
  | [] -> SaveCheck.AllSafe
  | first :: rest -> SaveCheck.Refused(first, rest)

// ── asking the user about a hot reflective loop ──────────────────────────────

/// When reflective reads of one value count as a hot loop: `Count` reads inside
/// `Within`, over a sliding window. Named config, not a literal.
type HotLoopThreshold =
  { Count: int
    Within: TimeSpan }

module HotLoopThreshold =
  /// A thousand reflective reads of one value inside a second is a loop, not
  /// a startup scan or a request handler.
  let standard = { Count = 1000; Within = TimeSpan.FromSeconds 1.0 }

/// What the rate of one value's reflective reads looks like right now.
[<RequireQualifiedAccess>]
type RateReading =
  | Quiet
  | HotLoop of readsPerSecond: int

/// The last `Count` read times of one value, as a ring. Hot when the oldest of
/// them is no further back than `Within`. Constant work and memory per read.
/// The clock is the caller's, in ticks, so a test drives it.
[<Sealed>]
type ReadRate(threshold: HotLoopThreshold) =
  let size = max 1 threshold.Count
  let stamps : int64[] = Array.zeroCreate size
  let mutable next = 0
  let mutable filled = 0
  member _.Observe(now: int64) : RateReading =
    stamps.[next] <- now
    next <- (next + 1) % size
    filled <- min size (filled + 1)
    match filled = size with
    | false -> RateReading.Quiet
    | true ->
      // After the write, `next` points at the oldest stamp in the ring.
      let span = now - stamps.[next]
      match span <= threshold.Within.Ticks with
      | false -> RateReading.Quiet
      | true ->
        let seconds = max (TimeSpan(span).TotalSeconds) (1.0 / float TimeSpan.TicksPerSecond)
        RateReading.HotLoop(int (min (float Int32.MaxValue) (float size / seconds)))

/// What SageFs tells the user when one value's reflective reads get hot.
type ReflectionNotice =
  { Value: string
    /// The method making the reads, or what SageFs could say about it.
    Caller: string
    ReadsPerSecond: int
    /// The mode the session was in when it got hot.
    Mode: ReflectionReadMode }

/// Where the question about one value stands.
[<RequireQualifiedAccess>]
type NoticeState =
  | Unasked
  | Asked of notice: ReflectionNotice
  | Chosen of notice: ReflectionNotice * mode: ReflectionReadMode

[<RequireQualifiedAccess>]
type NoticeEvent =
  | HotLoopSeen of notice: ReflectionNotice
  | ModeChosen of mode: ReflectionReadMode

module ReflectionNotices =
  /// One value's question, one event at a time. A value is asked about once.
  let step (state: NoticeState) (event: NoticeEvent) : NoticeState * ReflectionNotice list =
    match state, event with
    | NoticeState.Unasked, NoticeEvent.HotLoopSeen notice -> NoticeState.Asked notice, [ notice ]
    | NoticeState.Asked notice, NoticeEvent.ModeChosen mode -> NoticeState.Chosen(notice, mode), []
    | NoticeState.Unasked, NoticeEvent.ModeChosen _
    | NoticeState.Asked _, NoticeEvent.HotLoopSeen _
    | NoticeState.Chosen _, _ -> state, []

  /// The notice in words: the value, the caller, the rate, what the current
  /// mode is costing, and the choices with what each one does.
  let describe (notice: ReflectionNotice) : string =
    let choices =
      ReflectionReadMode.all
      |> List.map (fun mode ->
        let current =
          match mode = notice.Mode with
          | true -> " (current)"
          | false -> ""
        sprintf "  %s%s: %s" (ReflectionReadMode.name mode) current (ReflectionReadMode.consequence mode))
      |> String.concat "\n"
    sprintf
      "'%s' is being read through reflection about %d times a second, by %s.\nIn %s mode that means: %s.\nPick a mode:\n%s"
      notice.Value
      notice.ReadsPerSecond
      notice.Caller
      (ReflectionReadMode.name notice.Mode)
      (ReflectionReadMode.cost notice.Mode)
      choices

/// What a session starts its reflection watch with. Travels to the isolated
/// host inside the agent's init.
type ReflectionReadSettings =
  { Mode: ReflectionReadMode
    HotLoop: HotLoopThreshold }

module ReflectionReadSettings =
  let standard = { Mode = ReflectionReadMode.standard; HotLoop = HotLoopThreshold.standard }

/// Whether the reflection entry points carry SageFs's watch.
[<RequireQualifiedAccess>]
type ReflectionWatchStatus =
  | Watching
  /// Harmony couldn't put the watch on. The getters keep their own watch for
  /// the app's life instead (exact-every-read), whatever the mode says.
  | NotWatching of why: string
  /// The watch stopped seeing reads for a while (the runtime recompiled a
  /// method it was patched onto). A read in that gap can't be ruled out, so
  /// every value this session tracks restarts on its next edit, until the app
  /// restarts. The getters carry the watch from here on.
  | Lapsed of why: string

/// Where one value's question stands.
type ValueNotice =
  { Value: string
    State: NoticeState }

/// A session's reflection reads, for the dashboard and MCP.
type ReflectionReadsReport =
  { Mode: ReflectionReadMode
    Watch: ReflectionWatchStatus
    /// Only values that have been asked about, oldest first.
    Notices: ValueNotice list
    /// How many reflective reads of a tracked value walked the stack.
    Walks: int64
    /// How many were named by a rewired caller's slot instead.
    SiteHits: int64 }
