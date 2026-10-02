/// Method bodies, read two ways: as raw instructions the writer can patch token by token, and as a
/// canonical form two compiles can be compared in.
///
/// The canonical form is what makes "did this body change" a question about the code and not about the
/// compiler's bookkeeping. A token operand becomes the symbolic text of what it names. A branch target
/// becomes an instruction index, so a short branch and a long one, or a body that is a few bytes longer
/// because something was inserted before it, compare equal. The macro forms (`ldarg.0`, `ldc.i4.5`,
/// `br.s`) fold into the one long form. Max stack is left out: it is derived, and a rewriter that
/// recomputes it must not make every method look edited.
///
/// A body from a Cecil-instrumented shadow copy carries a coverage probe in front of every sequence point
/// (`ldc.i4 slot; call __SageFsCoverage::Hit`). The baseline the runtime holds is that instrumented
/// module, so comparing it with a clean build has to see through the probes: `StripCoverageProbes` drops
/// each pair, and sends a branch that landed on a probe to the instruction the probe was in front of.
///
/// Anything the reader cannot read (`calli`, an opcode outside the table, a body that ends mid
/// instruction) is a refusal, never a guess.
namespace SageFs.Features.MetadataDelta

open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

/// What a token operand points into.
[<RequireQualifiedAccess>]
type TokenKind =
  /// A row of TypeRef, TypeDef, Field, MethodDef, MemberRef, TypeSpec or MethodSpec.
  | Entity
  /// An offset into the #US heap.
  | UserString

/// One instruction as it sits in the body.
type RawInstruction =
  { Offset: int
    /// Bytes from the opcode through the operand.
    Length: int
    Code: OpCode
    Operand: RawOperand }

and [<RequireQualifiedAccess>] RawOperand =
  | NoOperand
  /// An immediate, a variable index, or a branch offset relative to the next instruction.
  | Integer of int64
  /// The bit pattern of a float operand.
  | FloatBits of int64
  /// Relative offsets of a `switch`.
  | SwitchTable of int array
  /// A metadata token, and which heap or table it indexes.
  | Token of kind: TokenKind * token: int

/// An operand in the canonical form.
[<RequireQualifiedAccess>]
type Operand =
  | NoOperand
  | Integer of int64
  | FloatBits of int64
  | Variable of int
  /// The index of the target instruction.
  | Branch of int
  | Switch of int list
  /// What an entity token names, as text.
  | Symbol of string
  | UserString of string

type Instruction =
  { /// The long-form mnemonic: `ldarg` for `ldarg.0` and `ldarg.s`, `br` for `br.s`.
    Op: string
    Operand: Operand }

type CanonHandler =
  { Kind: ExceptionRegionKind
    TryStart: int
    TryEnd: int
    HandlerStart: int
    HandlerEnd: int
    /// The caught type, as text. Empty for a finally, fault or filter.
    CatchType: string
    /// The instruction a filter block starts at, -1 for any other kind.
    FilterStart: int }

[<RequireQualifiedAccess>]
type LocalsInit =
  | ZeroInitialised
  | Uninitialised

/// A body in the form two compiles can be compared in.
type CanonBody =
  { Instructions: Instruction array
    Locals: string list
    Handlers: CanonHandler list
    LocalsInit: LocalsInit }

/// Whether the coverage probes a Cecil rewrite put in front of sequence points are looked through.
[<RequireQualifiedAccess>]
type ProbeStripping =
  | KeepEveryInstruction
  /// Drop each `ldc.i4 slot; call <hitSymbol>` pair. `hitSymbol` is the symbolic text of the tracker's
  /// `Hit` method.
  | StripCoverageProbes of hitSymbol: string

[<RequireQualifiedAccess>]
module CoverageProbe =
  /// The tracker's Hit method as the differ writes it. CoverageInstrumenter names the class
  /// `__SageFsCoverage` and the method `Hit(int)`; a test instruments a real assembly and checks that
  /// this still finds every probe.
  let hitSymbol = "md:__SageFsCoverage::Hit#Void(Int32)"

  /// The tracker class itself, which a clean build does not have.
  let trackerTypeKey = "__SageFsCoverage"

[<RequireQualifiedAccess>]
module IlCanon =

  /// Every opcode, by the value the body spells it with. Built once from the framework's own table, so an
  /// opcode the framework knows is an opcode the reader knows.
  let private opcodes : Dictionary<int, OpCode> =
    let table = Dictionary<int, OpCode>()
    for field in typeof<OpCodes>.GetFields(BindingFlags.Public ||| BindingFlags.Static) do
      match field.GetValue null with
      | :? OpCode as code -> table[int (uint16 code.Value)] <- code
      | _ -> ()
    table

  let private int32At (il: byte array) (at: int) = BitConverter.ToInt32(il, at)

  /// The size of an operand, or the refusal when the opcode has none the reader can size.
  let private operandSize (il: byte array) (at: int) (code: OpCode) : Result<int, IlRefusal> =
    match code.OperandType with
    | OperandType.InlineNone -> Result.Ok 0
    | OperandType.ShortInlineBrTarget
    | OperandType.ShortInlineI
    | OperandType.ShortInlineVar -> Result.Ok 1
    | OperandType.InlineVar -> Result.Ok 2
    | OperandType.InlineBrTarget
    | OperandType.InlineI
    | OperandType.ShortInlineR
    | OperandType.InlineString
    | OperandType.InlineField
    | OperandType.InlineMethod
    | OperandType.InlineTok
    | OperandType.InlineType -> Result.Ok 4
    | OperandType.InlineI8
    | OperandType.InlineR -> Result.Ok 8
    | OperandType.InlineSwitch ->
      match at + 4 <= il.Length with
      | true -> Result.Ok (4 + 4 * int32At il at)
      | false -> Result.Error (IlRefusal.MalformedBody "a switch table runs past the end")
    | OperandType.InlineSig -> Result.Error IlRefusal.CalliNotSupported
    | _ -> Result.Error (IlRefusal.MalformedBody (sprintf "operand kind %A is not one the reader sizes" code.OperandType))

  let private readOperand (il: byte array) (at: int) (code: OpCode) : RawOperand =
    match code.OperandType with
    | OperandType.InlineNone -> RawOperand.NoOperand
    | OperandType.ShortInlineBrTarget
    | OperandType.ShortInlineI -> RawOperand.Integer (int64 (sbyte il[at]))
    | OperandType.ShortInlineVar -> RawOperand.Integer (int64 il[at])
    | OperandType.InlineVar -> RawOperand.Integer (int64 (BitConverter.ToUInt16(il, at)))
    | OperandType.InlineBrTarget
    | OperandType.InlineI -> RawOperand.Integer (int64 (int32At il at))
    | OperandType.InlineI8 -> RawOperand.Integer (BitConverter.ToInt64(il, at))
    | OperandType.ShortInlineR -> RawOperand.FloatBits (int64 (BitConverter.SingleToInt32Bits(BitConverter.ToSingle(il, at))))
    | OperandType.InlineR -> RawOperand.FloatBits (BitConverter.DoubleToInt64Bits(BitConverter.ToDouble(il, at)))
    | OperandType.InlineSwitch ->
      let count = int32At il at
      RawOperand.SwitchTable (Array.init count (fun n -> int32At il (at + 4 + 4 * n)))
    | OperandType.InlineString -> RawOperand.Token (TokenKind.UserString, int32At il at)
    | _ -> RawOperand.Token (TokenKind.Entity, int32At il at)

  /// Split a body into instructions. Fails closed on `calli` and on any opcode not in the framework's table.
  let scan (il: byte array) : Result<RawInstruction array, IlRefusal> =
    let found = ResizeArray<RawInstruction>()
    let rec go (at: int) : Result<RawInstruction array, IlRefusal> =
      match at >= il.Length with
      | true -> Result.Ok (found.ToArray())
      | false ->
        let first = int il[at]
        let value, opcodeLength =
          match first with
          | 0xFE when at + 1 < il.Length -> 0xFE00 ||| int il[at + 1], 2
          | _ -> first, 1
        match opcodes.TryGetValue value with
        | false, _ -> Result.Error (IlRefusal.UnknownOpcode value)
        | true, code ->
          let operandAt = at + opcodeLength
          match operandSize il operandAt code with
          | Result.Error refusal -> Result.Error refusal
          | Result.Ok size ->
            match operandAt + size <= il.Length with
            | false -> Result.Error (IlRefusal.MalformedBody "the last instruction runs past the end")
            | true ->
              found.Add { Offset = at; Length = opcodeLength + size; Code = code; Operand = readOperand il operandAt code }
              go (operandAt + size)
    go 0

  /// How an opcode folds to its long form.
  [<RequireQualifiedAccess>]
  type private Fold =
    | Plain of op: string
    | ImpliedVariable of op: string * index: int
    | ImpliedInteger of op: string * value: int

  let private fold (code: OpCode) : Fold =
    let name = code.Name
    let suffixed (prefix: string) = name.Length = prefix.Length + 2 && name.StartsWith(prefix + ".", StringComparison.Ordinal)
    match name with
    | _ when (suffixed "ldarg" || suffixed "ldloc" || suffixed "stloc") && Char.IsDigit name[name.Length - 1] ->
      Fold.ImpliedVariable (name.Substring(0, name.Length - 2), int name[name.Length - 1] - int '0')
    | "ldc.i4.m1" -> Fold.ImpliedInteger ("ldc.i4", -1)
    | _ when name.StartsWith("ldc.i4.", StringComparison.Ordinal) && name.Length = 8 && Char.IsDigit name[7] ->
      Fold.ImpliedInteger ("ldc.i4", int name[7] - int '0')
    | _ when name.EndsWith(".s", StringComparison.Ordinal) -> Fold.Plain (name.Substring(0, name.Length - 2))
    | _ -> Fold.Plain name

  let private entityTables = set [ 0x01; 0x02; 0x04; 0x06; 0x0A; 0x1B; 0x2B ]

  /// All of the results, or the first refusal.
  let private collect (results: Result<'a, IlRefusal> array) : Result<'a array, IlRefusal> =
    match results |> Array.tryPick (function Result.Error refusal -> Some refusal | Result.Ok _ -> None) with
    | Some refusal -> Result.Error refusal
    | None -> Result.Ok (results |> Array.choose (function Result.Ok value -> Some value | Result.Error _ -> None))

  /// The body of a method, in the form the differ compares.
  let canonicalize (image: PeImage) (body: MethodBodyBlock) (stripping: ProbeStripping) : Result<CanonBody, IlRefusal> =
    let reader = image.Reader
    match scan (body.GetILBytes()) with
    | Result.Error refusal -> Result.Error refusal
    | Result.Ok raw ->
      let symbolOf (token: int) : Result<string, IlRefusal> =
        match entityTables.Contains(token >>> 24) with
        | false -> Result.Error (IlRefusal.UnsupportedToken (token >>> 24))
        | true -> Result.Ok (image.Describe(MetadataTokens.EntityHandle token))
      // Which instructions are a probe: an `ldc.i4` directly followed by a call to the tracker's Hit.
      let isProbeStart (i: int) : bool =
        match stripping with
        | ProbeStripping.KeepEveryInstruction -> false
        | ProbeStripping.StripCoverageProbes hit ->
          i + 1 < raw.Length
          && (match fold raw[i].Code, raw[i + 1].Code.Name, raw[i + 1].Operand with
              | (Fold.Plain "ldc.i4" | Fold.ImpliedInteger _), "call", RawOperand.Token (TokenKind.Entity, token) ->
                (match symbolOf token with
                 | Result.Ok text -> text = hit
                 | Result.Error _ -> false)
              | _ -> false)
      let dropped = Array.zeroCreate<bool> raw.Length
      let mutable i = 0
      while i < raw.Length do
        match isProbeStart i with
        | true ->
          dropped[i] <- true
          dropped[i + 1] <- true
          i <- i + 2
        | false -> i <- i + 1
      // An offset's place in the kept instructions: the first kept instruction at or after it.
      let keptIndexAt = Dictionary<int, int>()
      let mutable kept = 0
      for n in 0 .. raw.Length - 1 do
        keptIndexAt[raw[n].Offset] <- kept
        match dropped[n] with
        | false -> kept <- kept + 1
        | true -> ()
      let endOffset = (match raw.Length with | 0 -> 0 | n -> raw[n - 1].Offset + raw[n - 1].Length)
      keptIndexAt[endOffset] <- kept
      let target (offset: int) : Result<int, IlRefusal> =
        match keptIndexAt.TryGetValue offset with
        | true, index -> Result.Ok index
        | false, _ -> Result.Error (IlRefusal.MalformedBody (sprintf "a branch lands at %d, which is not an instruction" offset))
      let convert (instruction: RawInstruction) : Result<Instruction, IlRefusal> =
        let next = instruction.Offset + instruction.Length
        match fold instruction.Code, instruction.Operand with
        | Fold.ImpliedVariable (op, index), _ -> Result.Ok { Op = op; Operand = Operand.Variable index }
        | Fold.ImpliedInteger (op, value), _ -> Result.Ok { Op = op; Operand = Operand.Integer (int64 value) }
        | Fold.Plain op, operand ->
          match instruction.Code.OperandType, operand with
          | (OperandType.ShortInlineBrTarget | OperandType.InlineBrTarget), RawOperand.Integer relative ->
            target (next + int relative) |> Result.map (fun index -> { Op = op; Operand = Operand.Branch index })
          | (OperandType.ShortInlineVar | OperandType.InlineVar), RawOperand.Integer index ->
            Result.Ok { Op = op; Operand = Operand.Variable (int index) }
          | _, RawOperand.NoOperand -> Result.Ok { Op = op; Operand = Operand.NoOperand }
          | _, RawOperand.Integer value -> Result.Ok { Op = op; Operand = Operand.Integer value }
          | _, RawOperand.FloatBits bits -> Result.Ok { Op = op; Operand = Operand.FloatBits bits }
          | _, RawOperand.SwitchTable table ->
            table
            |> Array.map (fun relative -> target (next + relative))
            |> collect
            |> Result.map (fun targets -> { Op = op; Operand = Operand.Switch (Array.toList targets) })
          | _, RawOperand.Token (TokenKind.UserString, token) ->
            Result.Ok { Op = op; Operand = Operand.UserString (reader.GetUserString(MetadataTokens.UserStringHandle(token &&& 0xFFFFFF))) }
          | _, RawOperand.Token (TokenKind.Entity, token) ->
            symbolOf token |> Result.map (fun text -> { Op = op; Operand = Operand.Symbol text })
      let converted =
        raw
        |> Array.indexed
        |> Array.filter (fun (n, _) -> not dropped[n])
        |> Array.map (fun (_, instruction) -> convert instruction)
        |> collect
      match converted with
      | Result.Error refusal -> Result.Error refusal
      | Result.Ok instructions ->
        let handlerOf (region: ExceptionRegion) : Result<CanonHandler, IlRefusal> =
          match target region.TryOffset, target (region.TryOffset + region.TryLength), target region.HandlerOffset, target (region.HandlerOffset + region.HandlerLength) with
          | Result.Ok tryStart, Result.Ok tryEnd, Result.Ok handlerStart, Result.Ok handlerEnd ->
            let filter =
              match region.Kind with
              | ExceptionRegionKind.Filter -> target region.FilterOffset
              | _ -> Result.Ok -1
            match filter with
            | Result.Error refusal -> Result.Error refusal
            | Result.Ok filterStart ->
              let catchType =
                match region.Kind, region.CatchType.IsNil with
                | ExceptionRegionKind.Catch, false -> image.Describe region.CatchType
                | _ -> ""
              Result.Ok
                { Kind = region.Kind
                  TryStart = tryStart
                  TryEnd = tryEnd
                  HandlerStart = handlerStart
                  HandlerEnd = handlerEnd
                  CatchType = catchType
                  FilterStart = filterStart }
          | Result.Error refusal, _, _, _
          | _, Result.Error refusal, _, _
          | _, _, Result.Error refusal, _
          | _, _, _, Result.Error refusal -> Result.Error refusal
        match body.ExceptionRegions |> Seq.map handlerOf |> Seq.toArray |> collect with
        | Result.Error refusal -> Result.Error refusal
        | Result.Ok handlers ->
          let locals =
            match body.LocalSignature.IsNil with
            | true -> []
            | false -> image.LocalSignature(reader.GetStandaloneSignature(body.LocalSignature).Signature)
          Result.Ok
            { Instructions = instructions
              Locals = locals
              Handlers = Array.toList handlers
              LocalsInit =
                (match body.LocalVariablesInitialized with
                 | true -> LocalsInit.ZeroInitialised
                 | false -> LocalsInit.Uninitialised) }

  /// True only when two bodies are certainly the same, and cheaply: the IL bytes are identical, every token in them
  /// names the same thing in each image, the locals are the same, and so are the exception regions. False means
  /// "look closer", never "different": a body a rewriter laid out differently is the same code, which
  /// `canonicalize` is for. Most bodies of two builds of one project are byte-identical, so this is what keeps a
  /// second save from reading every method of a large assembly into instructions.
  let sameWithoutLooking (previous: PeImage) (before: MethodBodyBlock) (next: PeImage) (after: MethodBodyBlock) : bool =
    let bytes = before.GetILBytes()
    let shape () =
      System.MemoryExtensions.SequenceEqual(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte>(after.GetILBytes()))
      && before.LocalVariablesInitialized = after.LocalVariablesInitialized
      && before.ExceptionRegions.Length = after.ExceptionRegions.Length
      && (before.LocalSignature.IsNil = after.LocalSignature.IsNil)
    let locals () =
      before.LocalSignature.IsNil
      || previous.LocalSignature(previous.Reader.GetStandaloneSignature(before.LocalSignature).Signature)
         = next.LocalSignature(next.Reader.GetStandaloneSignature(after.LocalSignature).Signature)
    let regions () =
      Seq.forall2
        (fun (a: ExceptionRegion) (b: ExceptionRegion) ->
          a.Kind = b.Kind
          && a.TryOffset = b.TryOffset
          && a.TryLength = b.TryLength
          && a.HandlerOffset = b.HandlerOffset
          && a.HandlerLength = b.HandlerLength
          && a.FilterOffset = b.FilterOffset
          && (a.CatchType.IsNil = b.CatchType.IsNil)
          && (a.CatchType.IsNil || previous.Describe a.CatchType = next.Describe b.CatchType))
        before.ExceptionRegions
        after.ExceptionRegions
    let tokens () =
      match scan bytes with
      | Result.Error _ -> false
      | Result.Ok instructions ->
        instructions
        |> Array.forall (fun instruction ->
          match instruction.Operand with
          | RawOperand.Token (TokenKind.Entity, token) ->
            entityTables.Contains(token >>> 24)
            && previous.Describe(MetadataTokens.EntityHandle token) = next.Describe(MetadataTokens.EntityHandle token)
          | RawOperand.Token (TokenKind.UserString, token) ->
            let handle = MetadataTokens.UserStringHandle(token &&& 0xFFFFFF)
            previous.Reader.GetUserString handle = next.Reader.GetUserString handle
          | _ -> true)
    shape () && locals () && regions () && tokens ()
