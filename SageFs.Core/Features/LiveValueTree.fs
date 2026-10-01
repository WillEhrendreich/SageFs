namespace SageFs.Features

open System
open System.Collections
open System.Reflection
open Microsoft.FSharp.Reflection

/// Live value tree — a reflection-walked, bounded tree of the actual bound
/// values in the FSI session, for the dashboard's debugger-style watch window.
/// Unlike BindingExplorer (which parses FSI's printed `val x : T = v` text),
/// this walks the real runtime objects via FSharpValue/FSharpType, so nested
/// records, lists, maps, unions, tuples and class properties are expanded.
module LiveValueTree =

  /// What the walk is allowed to run. Reading a record, a union, a tuple, a list or a map runs none of the
  /// user's code, so every mode shows those the same way. The modes differ on a class instance and on a
  /// lazy sequence, where looking at the value can mean running something.
  [<RequireQualifiedAccess>]
  type WalkMode =
    /// Fields are read. A getter runs only when its compiled body can do nothing: a field read, a
    /// constant, or straight-line arithmetic. Every other getter shows as "not evaluated", with why.
    | Safe
    /// Every readable public property runs, as the walk always did. The caller bounds it with a deadline.
    | Everything
    /// A class instance is collapsed and nothing of it is read.
    | Off

  /// Why a value was listed but not read. The row says so, never leaving it blank.
  [<RequireQualifiedAccess>]
  type NotEvaluatedReason =
    /// The getter calls other code, which could take any time or change something.
    | GetterRunsCode
    /// The getter's body loops or calls itself, so reading it may never return.
    | GetterLoops
    /// A lazy sequence: enumerating it runs the code that produces it.
    | SequenceNotEnumerated
    /// The walk is in `Off` mode, which does not open class instances.
    | ClassesCollapsed
    /// A click ran the getter and it did not return within its deadline.
    | EvaluationTimedOut
    /// A click ran the getter and it threw.
    | EvaluationThrew of message: string
    /// A click was refused because the getter could not be run under containment here.
    | EvaluationNotContained of why: string

  /// How a node's value should be rendered / expanded.
  [<RequireQualifiedAccess>]
  type NodeKind =
    | Leaf
    /// Listed, not read. `Preview` says what the value is, the reason says why it was left alone.
    | NotEvaluated of reason: NotEvaluatedReason
    | Record
    | List
    | Map
    | Option
    | Union
    | Tuple
    | Array
    | Class
    | Closure
    | Cycle
    | Truncated

  /// One node in the expanded value tree.
  type LiveValueNode = {
    Label: string
    TypeName: string
    Preview: string
    Kind: NodeKind
    Children: LiveValueNode list
    /// True when the children are a best-effort guess (e.g. closure captures).
    BestEffort: bool
    Depth: int
  }

  /// A single top-level binding with its expanded value tree.
  type LiveBindingValue = {
    Name: string
    TypeSignature: string
    Root: LiveValueNode
  }

  /// Point-in-time snapshot of all active bindings in one session.
  type LiveValueSnapshot = {
    SessionId: string
    Generation: int64
    Bindings: LiveBindingValue list
    Truncated: bool
    CapturedAt: DateTimeOffset
  }

  // ── Limits ────────────────────────────────────────────────────────

  let [<Literal>] MaxDepth = 6
  let [<Literal>] MaxChildren = 50
  let [<Literal>] MaxStringLen = 500
  /// Top-level bindings walked per snapshot — caps the per-eval reflection
  /// cost even when a session binds thousands of values (roast queue 3).
  let [<Literal>] MaxBindings = 200
  /// Total node budget for one snapshot build. Depth × children bounds alone
  /// allow an exponential 50^depth expansion on hostile object graphs; this
  /// ref-based budget cuts the walk at a hard node count regardless of shape.
  let [<Literal>] MaxNodes = 10000

  // ── Preview builders ──────────────────────────────────────────────

  let private truncateString (s: string) =
    match s.Length > MaxStringLen with
    | false -> s
    | true -> s.Substring(0, MaxStringLen) + "…"

  let private truncateList (items: string list) =
    match items.Length > MaxChildren with
    | false -> String.concat "; " items
    | true -> String.concat "; " (items |> List.truncate MaxChildren) + "; …"

  /// Compact one-line preview for a scalar/leaf value.
  let private scalarPreview (value: obj) =
    match value with
    | null -> "null"
    | :? string as s -> sprintf "\"%s\"" (truncateString s)
    | :? char as c -> sprintf "'%c'" c
    | :? bool as b -> if b then "true" else "false"
    | :? float as f -> sprintf "%g" f
    | :? DateTime as dt -> dt.ToString("o")
    | _ -> truncateString (string value)

  /// Label for a collection key — unquoted so map keys read like `a` not `"a"`.
  let private keyLabel (value: obj) =
    match value with
    | null -> "null"
    | :? string as s -> s
    | _ -> truncateString (string value)

  // ── What a getter's compiled body does ────────────────────────────
  //
  // A property getter is the user's code. Whether running it can hang, loop, overflow the stack or
  // change something is a question about its compiled body, and the body is right there as IL. So the
  // walk reads the IL once per getter and sorts it into one of the shapes below. The sort is
  // conservative: an opcode it does not know is "calls other code", and so is a body it cannot read.

  /// What a property getter's compiled body does.
  [<RequireQualifiedAccess>]
  type GetterShape =
    /// `ldarg.0; ldfld f; ret`: returns a field. Reading the field shows the same value.
    | ReturnsField of fieldName: string
    /// Loads a literal and returns it.
    | ReturnsConstant
    /// Arithmetic and field reads with no call and no way back to an earlier instruction.
    | PureStraightLine
    /// Contains a call, an allocation or an opcode this reader does not recognise.
    | CallsOtherCode
    /// Branches back to an earlier instruction, or calls itself.
    | ContainsLoop
    /// The author marked it `DebuggerBrowsable(Never)`.
    | HiddenByAuthor

  type private Instr = {
    Offset: int
    Op: System.Reflection.Emit.OpCode
    Operand: int64
    Targets: int list
  }

  /// Why a method body could not be split into instructions. The classifier treats every one as "calls other code".
  type private DecodeFailure =
    | UnknownOpcode of code: int16 * at: int
    | OperandPastEnd of at: int

  type private Decoding =
    | At of int
    | Broken of DecodeFailure

  let private opCodeTable =
    let table = Collections.Generic.Dictionary<int16, System.Reflection.Emit.OpCode>()
    for f in typeof<System.Reflection.Emit.OpCodes>.GetFields(BindingFlags.Public ||| BindingFlags.Static) do
      match f.GetValue null with
      | :? System.Reflection.Emit.OpCode as oc -> table.[oc.Value] <- oc
      | _ -> ()
    table

  /// Opcodes a getter may contain and still be provably harmless: stack and local shuffling, constants,
  /// field reads, arithmetic, comparisons, conversions and branches. A branch is allowed here and
  /// checked separately for going backwards.
  let private harmlessOpCodes =
    let o = typeof<System.Reflection.Emit.OpCodes>
    [ "Nop"; "Ldarg_0"; "Ldarg_1"; "Ldarg_2"; "Ldarg_3"; "Ldarg_S"; "Ldarg"
      "Ldloc_0"; "Ldloc_1"; "Ldloc_2"; "Ldloc_3"; "Ldloc_S"; "Ldloc"
      "Stloc_0"; "Stloc_1"; "Stloc_2"; "Stloc_3"; "Stloc_S"; "Stloc"; "Starg_S"; "Starg"
      "Ldnull"; "Ldc_I4_M1"; "Ldc_I4_0"; "Ldc_I4_1"; "Ldc_I4_2"; "Ldc_I4_3"; "Ldc_I4_4"; "Ldc_I4_5"
      "Ldc_I4_6"; "Ldc_I4_7"; "Ldc_I4_8"; "Ldc_I4_S"; "Ldc_I4"; "Ldc_I8"; "Ldc_R4"; "Ldc_R8"; "Ldstr"
      "Dup"; "Pop"; "Ldfld"; "Ldsfld"; "Ldlen"
      "Add"; "Sub"; "Mul"; "Div"; "Div_Un"; "Rem"; "Rem_Un"; "And"; "Or"; "Xor"; "Shl"; "Shr"; "Shr_Un"
      "Neg"; "Not"; "Ceq"; "Cgt"; "Cgt_Un"; "Clt"; "Clt_Un"
      "Conv_I1"; "Conv_I2"; "Conv_I4"; "Conv_I8"; "Conv_R4"; "Conv_R8"; "Conv_U1"; "Conv_U2"; "Conv_U4"
      "Conv_U8"; "Conv_R_Un"; "Conv_I"; "Conv_U"
      "Ret"; "Br"; "Br_S"; "Brfalse"; "Brfalse_S"; "Brtrue"; "Brtrue_S"; "Beq"; "Beq_S"; "Bge"; "Bge_S"
      "Bgt"; "Bgt_S"; "Ble"; "Ble_S"; "Blt"; "Blt_S"; "Bne_Un"; "Bne_Un_S"; "Bge_Un"; "Bge_Un_S"
      "Bgt_Un"; "Bgt_Un_S"; "Ble_Un"; "Ble_Un_S"; "Blt_Un"; "Blt_Un_S" ]
    |> List.map (fun name ->
      match o.GetField name with
      | null -> failwithf "System.Reflection.Emit.OpCodes has no field %s" name
      | f -> (f.GetValue null :?> System.Reflection.Emit.OpCode).Value)
    |> Collections.Generic.HashSet<int16>

  let private fixedOperandSize (kind: System.Reflection.Emit.OperandType) =
    match kind with
    | System.Reflection.Emit.OperandType.InlineNone -> 0
    | System.Reflection.Emit.OperandType.ShortInlineBrTarget
    | System.Reflection.Emit.OperandType.ShortInlineI
    | System.Reflection.Emit.OperandType.ShortInlineVar -> 1
    | System.Reflection.Emit.OperandType.InlineVar -> 2
    | System.Reflection.Emit.OperandType.InlineI8
    | System.Reflection.Emit.OperandType.InlineR -> 8
    | _ -> 4

  /// Split a method body into instructions, or say why it cannot be split.
  let private decode (il: byte[]) : Result<Instr[], DecodeFailure> =
    let instrs = ResizeArray<Instr>()
    let step (i: int) : Decoding =
      let width, code =
        match il.[i] = 0xFEuy && i + 1 < il.Length with
        | true -> 2, int16 (0xFE00 ||| int il.[i + 1])
        | false -> 1, int16 il.[i]
      match opCodeTable.TryGetValue code with
      | false, _ -> Broken (UnknownOpcode (code, i))
      | true, op ->
        let at = i + width
        let size =
          match op.OperandType with
          | System.Reflection.Emit.OperandType.InlineSwitch ->
            (match at + 4 <= il.Length with
             | true -> 4 + 4 * BitConverter.ToInt32(il, at)
             | false -> il.Length)
          | kind -> fixedOperandSize kind
        let next = at + size
        match next > il.Length || size < 0 with
        | true -> Broken (OperandPastEnd i)
        | false ->
          let operand, targets =
            match op.OperandType with
            | System.Reflection.Emit.OperandType.ShortInlineBrTarget ->
              int64 (sbyte il.[at]), [ next + int (sbyte il.[at]) ]
            | System.Reflection.Emit.OperandType.InlineBrTarget ->
              let rel = BitConverter.ToInt32(il, at)
              int64 rel, [ next + rel ]
            | System.Reflection.Emit.OperandType.InlineSwitch ->
              let count = BitConverter.ToInt32(il, at)
              0L, [ for k in 0 .. count - 1 -> next + BitConverter.ToInt32(il, at + 4 + 4 * k) ]
            | System.Reflection.Emit.OperandType.InlineVar -> int64 (BitConverter.ToUInt16(il, at)), []
            | System.Reflection.Emit.OperandType.ShortInlineVar
            | System.Reflection.Emit.OperandType.ShortInlineI -> int64 il.[at], []
            | System.Reflection.Emit.OperandType.InlineNone
            | System.Reflection.Emit.OperandType.InlineI8
            | System.Reflection.Emit.OperandType.InlineR -> 0L, []
            | _ -> int64 (BitConverter.ToInt32(il, at)), []
          instrs.Add { Offset = i; Op = op; Operand = operand; Targets = targets }
          At next
    let mutable state = At 0
    let mutable running = il.Length > 0
    while running do
      match state with
      | At i when i < il.Length -> state <- step i
      | _ -> running <- false
    match state with
    | Broken why -> Result.Error why
    | At _ -> Result.Ok (instrs.ToArray())

  let private isLdarg0 (i: Instr) =
    let v = i.Op.Value
    v = System.Reflection.Emit.OpCodes.Ldarg_0.Value
    || ((v = System.Reflection.Emit.OpCodes.Ldarg.Value || v = System.Reflection.Emit.OpCodes.Ldarg_S.Value) && i.Operand = 0L)

  let private isConstantLoad (i: Instr) =
    match i.Op.Value with
    | v when v = System.Reflection.Emit.OpCodes.Ldnull.Value || v = System.Reflection.Emit.OpCodes.Ldstr.Value -> true
    | v when v = System.Reflection.Emit.OpCodes.Ldc_I4_S.Value || v = System.Reflection.Emit.OpCodes.Ldc_I4.Value
             || v = System.Reflection.Emit.OpCodes.Ldc_I8.Value || v = System.Reflection.Emit.OpCodes.Ldc_R4.Value
             || v = System.Reflection.Emit.OpCodes.Ldc_R8.Value -> true
    | v -> v >= System.Reflection.Emit.OpCodes.Ldc_I4_M1.Value && v <= System.Reflection.Emit.OpCodes.Ldc_I4_8.Value
           && i.Op.OperandType = System.Reflection.Emit.OperandType.InlineNone

  let private isCall (i: Instr) =
    i.Op.Value = System.Reflection.Emit.OpCodes.Call.Value || i.Op.Value = System.Reflection.Emit.OpCodes.Callvirt.Value

  let private isReturn (i: Instr) = i.Op.Value = System.Reflection.Emit.OpCodes.Ret.Value

  let private isHiddenFromDebugger (attributes: Collections.Generic.IList<CustomAttributeData>) =
    attributes
    |> Seq.exists (fun a ->
      a.AttributeType = typeof<System.Diagnostics.DebuggerBrowsableAttribute>
      && a.ConstructorArguments.Count = 1
      && (match a.ConstructorArguments.[0].Value with
          | :? int as state -> state = int System.Diagnostics.DebuggerBrowsableState.Never
          | _ -> false))

  /// Does this call instruction call the getter that contains it?
  let private callsItself (getter: MethodInfo) (i: Instr) =
    isCall i
    && (try
          match getter.Module.ResolveMethod(int i.Operand) with
          | null -> false
          | target -> target.MetadataToken = getter.MetadataToken && target.Module = getter.Module
        with _ -> false)

  /// Sort a property's getter by what its compiled body can do. Anything unreadable is `CallsOtherCode`.
  let classifyGetter (property: PropertyInfo) : GetterShape =
    match isHiddenFromDebugger (property.GetCustomAttributesData()) with
    | true -> GetterShape.HiddenByAuthor
    | false ->
    match property.GetGetMethod true with
    | null -> GetterShape.CallsOtherCode
    | getter ->
    match (try getter.GetMethodBody() with _ -> null) with
    | null -> GetterShape.CallsOtherCode
    | body ->
    match decode (body.GetILAsByteArray()) with
    | Result.Error _ -> GetterShape.CallsOtherCode
    | Result.Ok all ->
      let real = all |> Array.filter (fun i -> i.Op.Value <> System.Reflection.Emit.OpCodes.Nop.Value)
      let fieldName (token: int64) =
        try
          match getter.Module.ResolveField(int token) with
          | null -> GetterShape.CallsOtherCode
          | f -> GetterShape.ReturnsField f.Name
        with _ -> GetterShape.CallsOtherCode
      match real with
      | [| a; b; r |] when isLdarg0 a && b.Op.Value = System.Reflection.Emit.OpCodes.Ldfld.Value && isReturn r ->
        fieldName b.Operand
      | [| c; r |] when isConstantLoad c && isReturn r -> GetterShape.ReturnsConstant
      | _ ->
        let goesBack = all |> Array.exists (fun i -> i.Targets |> List.exists (fun t -> t <= i.Offset))
        match goesBack || (all |> Array.exists (callsItself getter)) with
        | true -> GetterShape.ContainsLoop
        | false ->
          match all |> Array.forall (fun i -> harmlessOpCodes.Contains i.Op.Value) with
          | true -> GetterShape.PureStraightLine
          | false -> GetterShape.CallsOtherCode

  /// The label the field of a class is shown under: an auto-property's `Name@` field reads as `Name`, a
  /// C# `<Name>k__BackingField` the same.
  let private fieldLabel (rawName: string) =
    match rawName.StartsWith("<", StringComparison.Ordinal), rawName.EndsWith("@", StringComparison.Ordinal) with
    | true, _ ->
      let endIdx = rawName.IndexOf('>')
      (match endIdx > 1 with | true -> rawName.Substring(1, endIdx - 1) | false -> rawName)
    | false, true -> rawName.TrimEnd '@'
    | false, false -> rawName

  /// Whether enumerating a sequence runs only a container's own code, or whatever produced it.
  [<RequireQualifiedAccess>]
  type private SequenceSource =
    /// An array, or a framework or F# core collection that already holds its items.
    | Materialized
    /// Anything else: a `seq { }`, a LINQ query, a user type. Enumerating it runs code.
    | MayRunCode

  let private trustedCollectionAssemblies =
    Collections.Generic.HashSet<string>(
      [ "System.Private.CoreLib"; "System.Collections"; "System.Collections.Concurrent"
        "System.Collections.Immutable"; "FSharp.Core" ])

  let private sourceOf (t: Type) : SequenceSource =
    let holdsItems =
      t.IsArray
      || (trustedCollectionAssemblies.Contains(t.Assembly.GetName().Name)
          && (typeof<ICollection>.IsAssignableFrom t
              || t.GetInterfaces()
                 |> Array.exists (fun i ->
                   i.IsGenericType
                   && (let d = i.GetGenericTypeDefinition()
                       d = typedefof<Collections.Generic.ICollection<_>>
                       || d = typedefof<Collections.Generic.IReadOnlyCollection<_>>))))
    match holdsItems with
    | true -> SequenceSource.Materialized
    | false -> SequenceSource.MayRunCode

  /// Why running one getter on a click did not give a value. The host's containment pipeline produces these.
  [<RequireQualifiedAccess>]
  type MemberFailure =
    | MemberTimedOut
    | MemberThrew of message: string
    | MemberNotContained of why: string

  /// Runs one getter on one object and says what came back. The host supplies it, under containment; the
  /// walk never runs a held getter itself.
  type MemberRunner = PropertyInfo -> obj -> Result<obj, MemberFailure>

  /// The one member a click asked for, named by the labels from the binding down to it.
  [<RequireQualifiedAccess>]
  type ForcedMember =
    | Nothing
    | At of path: string list * run: MemberRunner

  /// What a walk does: its mode and, for a click, the one getter it may run through the given runner.
  type Walk = { Mode: WalkMode; Force: ForcedMember }

  let private reasonOf (failure: MemberFailure) : NotEvaluatedReason =
    match failure with
    | MemberFailure.MemberTimedOut -> NotEvaluatedReason.EvaluationTimedOut
    | MemberFailure.MemberThrew message -> NotEvaluatedReason.EvaluationThrew message
    | MemberFailure.MemberNotContained why -> NotEvaluatedReason.EvaluationNotContained why

  /// One line of a class's `Safe` view: a value that was read (or failed to be), or a member left alone.
  [<RequireQualifiedAccess>]
  type private Row =
    | Read of label: string * outcome: Result<obj, exn>
    | Held of label: string * typeName: string * reason: NotEvaluatedReason

  let private describe (reason: NotEvaluatedReason) : string =
    match reason with
    | NotEvaluatedReason.GetterRunsCode -> "not evaluated: the getter calls other code"
    | NotEvaluatedReason.GetterLoops -> "not evaluated: the getter loops or calls itself"
    | NotEvaluatedReason.SequenceNotEnumerated -> "not evaluated: enumerating a sequence runs the code behind it"
    | NotEvaluatedReason.ClassesCollapsed -> "not evaluated: this mode does not open class instances"
    | NotEvaluatedReason.EvaluationTimedOut -> "not evaluated: the getter did not return in time"
    | NotEvaluatedReason.EvaluationThrew message -> sprintf "not evaluated: the getter threw: %s" message
    | NotEvaluatedReason.EvaluationNotContained why -> sprintf "not evaluated: %s" why

  // ── Per-type shapes ───────────────────────────────────────────────
  //
  // How a value is walked depends only on its runtime type: whether it is a
  // record/union/tuple/function, its record fields, union cases, readable
  // properties and closure captures. Working that out is the expensive
  // reflection, so it runs once per type and every later value of the type
  // reuses the readers FSharp.Core precomputes. The cache is keyed weakly:
  // FSI-defined types live in collectible load contexts a reset unloads, and a
  // strong cache would pin every session's assemblies for the process lifetime.

  type private UnionCaseShape = {
    CaseName: string
    FieldNames: string[]
    ReadFields: obj -> obj[]
  }

  [<RequireQualifiedAccess>]
  type private TypeShape =
    /// string, char, bool, float, DateTime, primitives and enums.
    | Scalar
    /// An F# function value: its closure class's fields are the captures.
    | Closure of captures: (string * FieldInfo)[]
    | Dictionary of source: SequenceSource
    /// F# Map, enumerated as KeyValuePair entries.
    | FSharpMap of key: PropertyInfo * value: PropertyInfo
    | Sequence of kind: NodeKind * source: SequenceSource
    | Record of fieldNames: string[] * readFields: (obj -> obj[])
    | Union of readTag: (obj -> int) * cases: UnionCaseShape[]
    | Tuple of readFields: (obj -> obj[])
    /// A Task: shown by its status. `Result` and `Exception` are only read once it has finished,
    /// because reading `Result` of a pending Task waits for it on the thread doing the walk.
    | Task
    /// A ValueTask or ValueTask<T>, handled like a Task for the same reason.
    | ValueTask
    /// A Lazy<T>: shown as created or not. `Value` is only read once it has been created, because
    /// reading it otherwise runs the user's factory.
    | Lazy
    /// Any other type: its instance fields (what `Safe` shows first) and its readable, non-indexed public
    /// properties, each with the shape of its getter (read once per type, from the compiled body).
    | Class of fields: (string * FieldInfo)[] * members: (PropertyInfo * GetterShape)[]

  /// Compiler-decorated capture fields (`<captured>v__`) are labelled by the
  /// captured name.
  let private captureLabel (rawName: string) =
    if rawName.StartsWith("<", StringComparison.Ordinal) then
      let endIdx = rawName.IndexOf('>')
      if endIdx > 1 then rawName.Substring(1, endIdx - 1) else rawName
    else rawName

  /// The checks run in the walker's precedence order: collections before the
  /// F# union check, because an F# list is a union AND IEnumerable.
  let private classify (t: Type) : TypeShape =
    if t = typeof<string> || t = typeof<char> || t = typeof<bool> || t = typeof<float>
       || t = typeof<DateTime> || t.IsPrimitive || t.IsEnum then
      TypeShape.Scalar
    elif FSharpType.IsFunction t then
      t.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
      |> Array.truncate MaxChildren
      |> Array.map (fun fi -> captureLabel fi.Name, fi)
      |> TypeShape.Closure
    elif typeof<IDictionary>.IsAssignableFrom t then
      TypeShape.Dictionary (sourceOf t)
    elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<string, obj>> then
      let entryType =
        typedefof<Collections.Generic.KeyValuePair<obj, obj>>.MakeGenericType(t.GetGenericArguments())
      TypeShape.FSharpMap (entryType.GetProperty "Key", entryType.GetProperty "Value")
    elif typeof<IEnumerable>.IsAssignableFrom t then
      TypeShape.Sequence ((if t.IsArray then NodeKind.Array else NodeKind.List), sourceOf t)
    elif FSharpType.IsRecord t then
      TypeShape.Record (
        FSharpType.GetRecordFields t |> Array.map (fun p -> p.Name),
        FSharpValue.PreComputeRecordReader t)
    elif FSharpType.IsUnion t then
      let cases =
        FSharpType.GetUnionCases t
        |> Array.map (fun case ->
          { CaseName = case.Name
            FieldNames = case.GetFields() |> Array.map (fun fi -> fi.Name)
            ReadFields = FSharpValue.PreComputeUnionReader case })
      TypeShape.Union (FSharpValue.PreComputeUnionTagReader t, cases)
    elif FSharpType.IsTuple t then
      TypeShape.Tuple (FSharpValue.PreComputeTupleReader t)
    elif typeof<System.Threading.Tasks.Task>.IsAssignableFrom t then
      TypeShape.Task
    elif t = typeof<System.Threading.Tasks.ValueTask>
         || (t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<System.Threading.Tasks.ValueTask<_>>) then
      TypeShape.ValueTask
    elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Lazy<_>> then
      TypeShape.Lazy
    else
      let members =
        t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
        |> Array.filter (fun p -> p.GetIndexParameters().Length = 0 && p.CanRead)
        |> Array.truncate MaxChildren
        |> Array.map (fun p -> p, classifyGetter p)
      // Fields from the most derived type up to, not including, object: a class's real state.
      let rec fieldsUp (start: Type | null) =
        match start with
        | null -> []
        | current when current = typeof<obj> || current = typeof<ValueType> -> []
        | current ->
          let declared =
            current.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
            |> Array.filter (fun fi ->
              not (fi.Name.StartsWith("init@", StringComparison.Ordinal))
              && not (isHiddenFromDebugger (fi.GetCustomAttributesData())))
            |> Array.map (fun fi -> fieldLabel fi.Name, fi)
            |> Array.toList
          declared @ fieldsUp current.BaseType
      TypeShape.Class (fieldsUp t |> List.toArray |> Array.truncate MaxChildren, members)

  let private shapes = System.Runtime.CompilerServices.ConditionalWeakTable<Type, TypeShape>()
  let private classifyCallback =
    System.Runtime.CompilerServices.ConditionalWeakTable<Type, TypeShape>.CreateValueCallback classify

  let private shapeOf (t: Type) = shapes.GetValue(t, classifyCallback)

  // ── Reflection walker ─────────────────────────────────────────────

  let private isCycle (visited: System.Collections.Generic.HashSet<obj>) (value: obj) =
    not (isNull value) && not (value.GetType().IsValueType) && not (visited.Add value)

  let rec private buildNode
    (walk: Walk)
    (trail: string list)
    (visited: System.Collections.Generic.HashSet<obj>)
    (budget: int ref)
    (label: string)
    (depth: int)
    (value: obj)
    : LiveValueNode =
    let t = if isNull value then typeof<obj> else value.GetType()

    // Cycle protection: reference types seen before are not re-expanded.
    if isCycle visited value then
      { Label = label; TypeName = t.Name; Preview = "↩ (cycle)"; Kind = NodeKind.Cycle
        Children = []; BestEffort = false; Depth = depth }
    elif depth >= MaxDepth then
      { Label = label; TypeName = t.Name; Preview = scalarPreview value; Kind = NodeKind.Truncated
        Children = []; BestEffort = false; Depth = depth }
    elif budget.Value <= 0 then
      // Total node budget exhausted — stop the walk for this snapshot. The
      // depth/children limits alone allow a 50^depth blow-up on hostile object
      // graphs; the budget forces a hard stop regardless of graph shape.
      { Label = label; TypeName = t.Name; Preview = "… (node budget)"; Kind = NodeKind.Truncated
        Children = []; BestEffort = false; Depth = depth }
    else
      budget.Value <- budget.Value - 1
      try
        let typeName = t.Name
        let leaf preview kind = {
          Label = label; TypeName = typeName; Preview = preview; Kind = kind
          Children = []; BestEffort = false; Depth = depth }

        match value with
        | null -> leaf "null" NodeKind.Leaf
        | _ ->
        match shapeOf t with
        | TypeShape.Scalar -> leaf (scalarPreview value) NodeKind.Leaf
        // F# function values are FSharpFunc subclasses; the compiler-generated
        // closure class carries captured variables as instance fields. In Debug
        // builds the fields are decorated (`<captured>v__`); the label strips the
        // decoration but the field is kept — they ARE the captures.
        | TypeShape.Closure captures ->
          let children =
            try
              captures
              |> Array.map (fun (name, fi) ->
                let child = buildNode walk (label :: trail) visited budget name (depth + 1) (fi.GetValue value)
                { child with BestEffort = true })
              |> Array.toList
            with _ -> []
          { Label = label; TypeName = typeName; Preview = "<fun>"; Kind = NodeKind.Closure
            Children = children; BestEffort = true; Depth = depth }
        // Enumerating a lazy sequence runs the code that produces it, so `Safe` and `Off` leave it alone.
        | TypeShape.Dictionary SequenceSource.MayRunCode
        | TypeShape.Sequence (_, SequenceSource.MayRunCode) when walk.Mode <> WalkMode.Everything ->
          { Label = label; TypeName = typeName; Preview = describe NotEvaluatedReason.SequenceNotEnumerated
            Kind = NodeKind.NotEvaluated NotEvaluatedReason.SequenceNotEnumerated
            Children = []; BestEffort = false; Depth = depth }
        | TypeShape.Dictionary _ ->
          let d = value :?> IDictionary
          let entries = d |> Seq.cast<DictionaryEntry> |> Seq.truncate (MaxChildren + 1) |> Seq.toList
          let preview = entries |> List.truncate MaxChildren
                         |> List.map (fun e -> sprintf "(%s, %s)" (scalarPreview e.Key) (scalarPreview e.Value))
                         |> truncateList |> fun s -> "map [" + s + "]"
          let children =
            entries |> List.truncate MaxChildren
            |> List.mapi (fun i e -> buildNode walk (label :: trail) visited budget (keyLabel e.Key) (depth + 1) e.Value)
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Map
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.FSharpMap (keyProp, valueProp) ->
          let entries =
            (value :?> IEnumerable)
            |> Seq.cast<obj>
            |> Seq.truncate (MaxChildren + 1)
            |> Seq.toList
            |> List.truncate MaxChildren
            |> List.map (fun kv -> keyProp.GetValue kv, valueProp.GetValue kv)
          let preview =
            entries
            |> List.map (fun (k, v) -> sprintf "(%s, %s)" (scalarPreview k) (scalarPreview v))
            |> truncateList |> fun s -> "map [" + s + "]"
          let children =
            entries
            |> List.map (fun (k, v) -> buildNode walk (label :: trail) visited budget (keyLabel k) (depth + 1) v)
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Map
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Sequence (kind, _) ->
          let items = (value :?> IEnumerable) |> Seq.cast<obj> |> Seq.truncate (MaxChildren + 1) |> Seq.toList
          let shown = items |> List.truncate MaxChildren
          let preview = shown |> List.map scalarPreview |> truncateList |> fun s -> "[" + s + "]"
          let children =
            shown
            |> List.mapi (fun i item -> buildNode walk (label :: trail) visited budget (sprintf "[%d]" i) (depth + 1) item)
          { Label = label; TypeName = typeName; Preview = preview; Kind = kind
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Record (fieldNames, readFields) ->
          let fields = readFields value
          let preview =
            fields
            |> Array.mapi (fun i f -> sprintf "%s = %s" fieldNames.[i] (scalarPreview f))
            |> Array.toList
            |> truncateList
            |> fun s -> "{ " + s + " }"
          let children =
            fields
            |> Array.mapi (fun i f -> buildNode walk (label :: trail) visited budget fieldNames.[i] (depth + 1) f)
            |> Array.truncate MaxChildren
            |> Array.toList
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Record
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Union (readTag, cases) ->
          let case = cases.[readTag value]
          let caseFields = case.ReadFields value
          let preview =
            match caseFields.Length with
            | 0 -> case.CaseName
            | _ ->
              let args =
                caseFields |> Array.map scalarPreview |> Array.toList |> truncateList
                |> fun s -> "(" + s + ")"
              case.CaseName + " " + args
          let children =
            case.FieldNames
            |> Array.mapi (fun i name -> buildNode walk (label :: trail) visited budget name (depth + 1) caseFields.[i])
            |> Array.truncate MaxChildren
            |> Array.toList
          let kind = if case.CaseName = "Some" || case.CaseName = "None" then NodeKind.Option else NodeKind.Union
          { Label = label; TypeName = typeName; Preview = preview; Kind = kind
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Tuple readFields ->
          let fields = readFields value
          let preview = fields |> Array.map scalarPreview |> Array.toList |> truncateList |> fun s -> "(" + s + ")"
          let children =
            fields
            |> Array.mapi (fun i f -> buildNode walk (label :: trail) visited budget (sprintf "item%d" (i + 1)) (depth + 1) f)
            |> Array.truncate MaxChildren
            |> Array.toList
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Tuple
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Task ->
          let task = value :?> System.Threading.Tasks.Task
          let status = task.Status
          let children =
            match status with
            | System.Threading.Tasks.TaskStatus.RanToCompletion ->
              match t.GetProperty "Result" with
              | null -> []
              | p -> [ buildNode walk (label :: trail) visited budget "Result" (depth + 1) (p.GetValue value) ]
            | System.Threading.Tasks.TaskStatus.Faulted ->
              let why =
                match task.Exception with
                | null -> "faulted"
                | ex -> ex.GetBaseException().Message
              [ { Label = "Exception"; TypeName = "exn"; Preview = scalarPreview why; Kind = NodeKind.Leaf
                  Children = []; BestEffort = false; Depth = depth + 1 } ]
            | _ -> []
          { Label = label; TypeName = typeName; Preview = sprintf "Task %s" (string status); Kind = NodeKind.Class
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.ValueTask ->
          let read (name: string) =
            match t.GetProperty name with
            | null -> false
            | p -> (p.GetValue value :?> bool)
          let state =
            match read "IsCompletedSuccessfully", read "IsFaulted", read "IsCanceled" with
            | true, _, _ -> "RanToCompletion"
            | _, true, _ -> "Faulted"
            | _, _, true -> "Canceled"
            | _ -> "WaitingForActivation"
          let children =
            match state, t.GetProperty "Result" with
            | "RanToCompletion", p when not (isNull p) -> [ buildNode walk (label :: trail) visited budget "Result" (depth + 1) (p.GetValue value) ]
            | _ -> []
          { Label = label; TypeName = typeName; Preview = sprintf "ValueTask %s" state; Kind = NodeKind.Class
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Lazy ->
          let created = (t.GetProperty "IsValueCreated").GetValue value :?> bool
          let children =
            match created with
            | true -> [ buildNode walk (label :: trail) visited budget "Value" (depth + 1) ((t.GetProperty "Value").GetValue value) ]
            | false -> []
          { Label = label; TypeName = typeName; Kind = NodeKind.Class
            Preview = (match created with | true -> "Lazy (created)" | false -> "Lazy (not created)")
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Class _ when walk.Mode = WalkMode.Off ->
          { Label = label; TypeName = typeName; Preview = describe NotEvaluatedReason.ClassesCollapsed
            Kind = NodeKind.NotEvaluated NotEvaluatedReason.ClassesCollapsed
            Children = []; BestEffort = false; Depth = depth }
        | TypeShape.Class (fields, members) when walk.Mode = WalkMode.Safe ->
          // Fields are the object's real state and reading one runs nothing. A getter runs only when its
          // body is provably harmless. One that merely returns a field already shown is not shown twice.
          let shown = Collections.Generic.HashSet<string>(fields |> Array.map (fun (_, fi) -> fi.Name))
          let fieldRows =
            fields
            |> Array.map (fun (name, fi) ->
              Row.Read (name, (try Ok (fi.GetValue value) with ex -> Error ex)))
          let here = List.rev (label :: trail)
          // A held getter runs only when a click named exactly this one, and then only through the runner.
          let heldOrForced (p: PropertyInfo) (reason: NotEvaluatedReason) =
            match walk.Force with
            | ForcedMember.At (target, run) when target = here @ [ p.Name ] ->
              let outcome =
                try run p value
                with ex -> Result.Error (MemberFailure.MemberThrew ex.Message)
              (match outcome with
               | Result.Ok v -> Row.Read (p.Name, Ok v)
               | Result.Error failure -> Row.Held (p.Name, p.PropertyType.Name, reasonOf failure))
            | _ -> Row.Held (p.Name, p.PropertyType.Name, reason)
          let memberRows =
            members
            |> Array.collect (fun (p, shape) ->
              match shape with
              | GetterShape.HiddenByAuthor -> [||]
              | GetterShape.ReturnsField f when shown.Contains f -> [||]
              | GetterShape.CallsOtherCode -> [| heldOrForced p NotEvaluatedReason.GetterRunsCode |]
              | GetterShape.ContainsLoop -> [| heldOrForced p NotEvaluatedReason.GetterLoops |]
              | GetterShape.ReturnsField _ | GetterShape.ReturnsConstant | GetterShape.PureStraightLine ->
                [| Row.Read (p.Name, (try Ok (p.GetValue value) with ex -> Error ex)) |])
          let rows = Array.append fieldRows memberRows |> Array.truncate MaxChildren
          let errorNode (rowLabel: string) =
            { Label = rowLabel; TypeName = "error"; Preview = "<error>"; Kind = NodeKind.Leaf
              Children = []; BestEffort = false; Depth = depth + 1 }
          let preview =
            rows
            |> Array.map (fun row ->
              match row with
              | Row.Read (l, Ok v) ->
                (try sprintf "%s = %s" l (scalarPreview v) with _ -> sprintf "%s = <error>" l)
              | Row.Read (l, Error _) -> sprintf "%s = <error>" l
              | Row.Held (l, _, _) -> sprintf "%s = …" l)
            |> Array.toList
            |> truncateList
            |> fun s -> "{ " + s + " }"
          let children =
            rows
            |> Array.map (fun row ->
              match row with
              | Row.Read (l, Ok v) ->
                (try buildNode walk (label :: trail) visited budget l (depth + 1) v with _ -> errorNode l)
              | Row.Read (l, Error _) -> errorNode l
              | Row.Held (l, heldTypeName, reason) ->
                { Label = l; TypeName = heldTypeName; Preview = describe reason; Kind = NodeKind.NotEvaluated reason
                  Children = []; BestEffort = false; Depth = depth + 1 })
            |> Array.toList
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Class
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Class (_, members) ->
          // Class instance — public instance properties (best-effort for .NET types).
          // Each getter is read ONCE: the preview and the child come from the same read, because
          // a getter is the user's code and may have an effect.
          let props = members |> Array.map fst
          let reads =
            props
            |> Array.map (fun p ->
              try Ok (p.GetValue value)
              with ex -> Error ex)
          let preview =
            Array.zip props reads
            |> Array.map (fun (p, read) ->
              match read with
              | Ok v ->
                try sprintf "%s = %s" p.Name (scalarPreview v)
                with _ -> sprintf "%s = <error>" p.Name
              | Error _ -> sprintf "%s = <error>" p.Name)
            |> Array.toList
            |> truncateList
            |> fun s -> "{ " + s + " }"
          let children =
            Array.zip props reads
            |> Array.map (fun (p, read) ->
              let errorNode =
                { Label = p.Name; TypeName = "error"; Preview = "<error>"; Kind = NodeKind.Leaf
                  Children = []; BestEffort = false; Depth = depth + 1 }
              match read with
              | Ok v ->
                try buildNode walk (label :: trail) visited budget p.Name (depth + 1) v
                with _ -> errorNode
              | Error _ -> errorNode)
            |> Array.toList
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Class
            Children = children; BestEffort = false; Depth = depth }
      with ex ->
        { Label = label; TypeName = t.Name; Preview = sprintf "<error: %s>" ex.Message
          Kind = NodeKind.Leaf; Children = []; BestEffort = false; Depth = depth }

  /// Build the root node for a binding's value under `walk`.
  let buildValueNodeWith (walk: Walk) (label: string) (value: obj) : LiveValueNode =
    let visited = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)
    buildNode walk [] visited (ref MaxNodes) label 0 value

  /// Build the root node for a binding's value under `mode`, with nothing forced.
  let buildValueNodeIn (mode: WalkMode) (label: string) (value: obj) : LiveValueNode =
    buildValueNodeWith { Mode = mode; Force = ForcedMember.Nothing } label value

  /// The walk as it always was: every readable property of a class runs. For values the caller already
  /// trusts. The host uses `Safe`.
  let buildValueNode (label: string) (value: obj) : LiveValueNode =
    buildValueNodeIn WalkMode.Everything label value

  /// How many walkers may be abandoned at once. A getter that never returns keeps its thread for
  /// the life of the process, so past this many a pass stops starting replacements and says why.
  let [<Literal>] MaxAbandonedWalks = 16

  /// Values a pass gave up on. Weak, so a value the session lets go of is not kept alive.
  let private unresponsive = System.Runtime.CompilerServices.ConditionalWeakTable<obj, obj>()
  let private abandonedWalks = ref 0

  let private unreadableNode (label: string) (value: obj) (why: string) : LiveValueNode =
    let t = if isNull value then typeof<obj> else value.GetType()
    { Label = label; TypeName = t.Name; Preview = why; Kind = NodeKind.Leaf
      Children = []; BestEffort = false; Depth = 0 }

  /// Walk `bindings` in order with a deadline for each one.
  ///
  /// Walking a value runs the user's property getters on whatever thread asks, and the host asks
  /// from the thread every eval of the session runs on, so a getter that never returns would stall
  /// every later eval. A walker thread takes the bindings in order and the caller waits for each
  /// with `budget`. One thread serves the whole pass, so the cost of the guard is one thread start
  /// per pass, not one per binding. When a binding misses its deadline the caller shows it as
  /// unreadable, remembers the value so it is not walked again, retires that walker (which writes
  /// nothing if it ever returns) and starts a fresh one at the next binding.
  let walkWithin (mode: WalkMode) (budget: TimeSpan) (bindings: (string * obj)[]) : LiveValueNode[] =
    let count = bindings.Length
    let nodes : LiveValueNode[] = Array.zeroCreate count
    let finished = Array.init count (fun _ -> new System.Threading.ManualResetEventSlim(false))
    let gate = obj ()
    let owner = ref 0
    let walkFrom (first: int) =
      let id = System.Threading.Interlocked.Increment(&owner.contents)
      let run () =
        let mutable index = first
        while index < count && System.Threading.Volatile.Read(&owner.contents) = id do
          let at = index
          let label, value = bindings.[at]
          let node =
            match not (isNull value) && fst (unresponsive.TryGetValue value) with
            | true -> unreadableNode label value "a property getter on this value did not return on an earlier look, so it is not read again"
            | false ->
              try buildValueNodeIn mode label value
              with _ -> unreadableNode label value "reading this value threw"
          lock gate (fun () ->
            match owner.Value = id && not finished.[at].IsSet with
            | true ->
              nodes.[at] <- node
              finished.[at].Set()
            | false -> ())
          index <- index + 1
        // A walker that was retired while stuck, and has now returned: it no longer counts.
        match System.Threading.Volatile.Read(&owner.contents) = id with
        | true -> ()
        | false -> System.Threading.Interlocked.Decrement(&abandonedWalks.contents) |> ignore
      let thread = System.Threading.Thread(run, IsBackground = true, Name = "sagefs-live-values")
      thread.Start()
    match count with
    | 0 -> nodes
    | _ ->
      walkFrom 0
      for i in 0 .. count - 1 do
        match finished.[i].Wait budget with
        | true -> ()
        | false ->
          let gaveUp =
            lock gate (fun () ->
              match finished.[i].IsSet with
              | true -> false
              | false ->
                let label, value = bindings.[i]
                nodes.[i] <- unreadableNode label value (sprintf "a property getter did not return within %gs, so this value is not shown" budget.TotalSeconds)
                finished.[i].Set()
                (match isNull value with
                 | true -> ()
                 | false -> unresponsive.TryAdd(value, obj()) |> ignore)
                System.Threading.Interlocked.Increment(&abandonedWalks.contents) |> ignore
                // Retire the stuck walker so it cannot write anything if it ever returns.
                System.Threading.Interlocked.Increment(&owner.contents) |> ignore
                true)
          match gaveUp, i + 1 < count with
          | true, true ->
            match abandonedWalks.Value >= MaxAbandonedWalks with
            | false -> walkFrom (i + 1)
            | true ->
              lock gate (fun () ->
                for j in i + 1 .. count - 1 do
                  let label, value = bindings.[j]
                  nodes.[j] <- unreadableNode label value "too many property getters did not return, so values are not read until the session restarts"
                  finished.[j].Set())
          | _ -> ()
      nodes

  /// Detect whether any node hit a truncation/cycle limit.
  let rec private hasTruncation (node: LiveValueNode) =
    node.Kind = NodeKind.Truncated
    || node.Kind = NodeKind.Cycle
    || (node.Children |> List.exists hasTruncation)

  /// Build a full snapshot from the session's bound values.
  /// `getBoundValues` returns (name, typeSignature, value) triples — the worker
  /// adapts FsiBoundValue into this shape so this module stays pure.
  ///
  /// `walk` turns the capped (name, value) pairs into their nodes, one per pair in the same order.
  /// The host passes the deadline-bounded `walkWithin` so a getter that never returns cannot stall
  /// the eval thread; everything else uses `buildSnapshot`.
  let buildSnapshotWith
    (walk: (string * obj)[] -> LiveValueNode[])
    (sessionId: string)
    (generation: int64)
    (boundValues: (string * string * obj) list)
    : LiveValueSnapshot =
    // boundValues arrives oldest-first (FsiEvaluationSession.GetBoundValues's
    // own declaration order — verified empirically: the first-declared name is
    // first in the list, and rebinding a name does not move its position).
    // Truncating that directly would silently keep the OLDEST MaxBindings
    // names and drop everything the user has defined since — backwards for a
    // live value WATCH, which exists to show what was just defined. Reverse
    // to newest-first, truncate to the most recent MaxBindings, then reverse
    // back so the kept subset still displays oldest-of-the-kept first.
    let capped = boundValues |> List.rev |> List.truncate MaxBindings |> List.rev
    let roots = walk (capped |> List.map (fun (name, _, value) -> name, value) |> Array.ofList)
    let bindings =
      capped
      |> List.mapi (fun i (name, typeSig, _) -> { Name = name; TypeSignature = typeSig; Root = roots.[i] })
    let truncated =
      (boundValues.Length > MaxBindings)
      || bindings |> List.exists (fun b -> hasTruncation b.Root)
    { SessionId = sessionId; Generation = generation; Bindings = bindings
      Truncated = truncated; CapturedAt = DateTimeOffset.UtcNow }

  /// A full snapshot walked with no deadline: for callers that read values they already trust.
  let buildSnapshot
    (sessionId: string)
    (generation: int64)
    (boundValues: (string * string * obj) list)
    : LiveValueSnapshot =
    buildSnapshotWith (Array.map (fun (name, value) -> buildValueNode name value)) sessionId generation boundValues

  /// A full snapshot where each binding gets `budget` to walk, so one value whose getter never
  /// returns shows as unreadable instead of stalling the thread that asked. The host uses this.
  let buildSnapshotWithin
    (mode: WalkMode)
    (budget: TimeSpan)
    (sessionId: string)
    (generation: int64)
    (boundValues: (string * string * obj) list)
    : LiveValueSnapshot =
    buildSnapshotWith (walkWithin mode budget) sessionId generation boundValues
