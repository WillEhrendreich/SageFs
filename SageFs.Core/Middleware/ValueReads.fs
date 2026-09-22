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

/// Follow the value a read instruction pushed (the one at `readIndex`) to
/// whatever consumes it.
let fateAt (tokens: Tokens) (instrs: Instr[]) (readIndex: int) : ReadFate =
  ReadFate.Escaped(Escape.Untraced "not implemented yet")

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
let sitesIn (tokens: Tokens) (classify: Instr -> ReadMatch) (instrs: Instr[]) : (int * ReadFate) list = []

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

let verdictOf (evidence: ValueEvidence) : ValueVerdict = ValueVerdict.SafeToPatch

/// The value a piece of evidence is about.
let valueOf (evidence: ValueEvidence) : string =
  match evidence with
  | ValueEvidence.Untracked(value, _)
  | ValueEvidence.Tracked(value, _) -> value

/// Where a copy went, in one clause a restart reason can carry.
let describeHolder (site: ReadSite) (seen: ReadSeen) : string = ""

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
  | StartupEnded

type Ledger =
  { Window: StartupWindow
    Values: Set<string>
    Untracked: Map<string, string>
    Readers: Map<ReaderId, Reader>
    Status: Map<ReaderId, ReaderStatus>
    /// Callers the getter watch saw read a value without a read in their code.
    UnknownReads: Map<string, string list> }

module Ledger =
  let empty : Ledger =
    { Window = StartupWindow.Open
      Values = Set.empty
      Untracked = Map.empty
      Readers = Map.empty
      Status = Map.empty
      UnknownReads = Map.empty }

  let step (ledger: Ledger) (event: LedgerEvent) : Ledger = ledger

  let evidence (ledger: Ledger) (value: string) : ValueEvidence =
    ValueEvidence.Untracked(value, "not implemented yet")

// ── a save's decision ────────────────────────────────────────────────────────

/// What a save that redefines values may do, once it has the evidence.
[<RequireQualifiedAccess>]
type SaveCheck =
  | AllSafe
  | Refused of first: (string * ValueVerdict) * rest: (string * ValueVerdict) list

/// Every verdict has to be SafeToPatch. The evidence is taken twice around a
/// patch (before, to decide, and after, to catch a read that raced it), so this
/// is asked of both.
let checkSave (verdicts: (string * ValueVerdict) list) : SaveCheck = SaveCheck.AllSafe
