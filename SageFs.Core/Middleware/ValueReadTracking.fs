/// The runtime half of rule 2: find every read of the app's module values, and
/// learn which of the code doing those reads has actually run.
///
/// ValueReads.fs decides what the evidence means. This file gathers it, in the
/// process where the user's code lives (the isolated FSI host, or the worker
/// for an in-process session), with two kinds of Harmony patch:
///
///   * A ONE-SHOT PROBE on every method whose read of a value lets it escape.
///     It's installed when the agent starts, before any of the user's code has
///     run, so the first time that method runs it's filed as having run. Then
///     the probe comes off, so there's no cost after that. A method whose reads
///     all throw the value away gets no probe: running it can't keep anything.
///
///   * A STARTUP WINDOW WATCH on every module value's getter. While the app is
///     starting, it files the caller of every read, which catches the one thing
///     the IL can't show: a read by code with no read in its own IL (reflection).
///     It's a PREFIX, so the read is filed before the value exists. As a postfix
///     a save could check, patch and recheck between the read and the filing,
///     and the DST (ValueReadSim, `postfixWatchTwin`) found exactly that.
///
/// The window opens when the agent starts and closes at the end of the first
/// eval after the app's code has read one of its values, or at the first
/// save, whichever comes first. That's the eval that started the app (the
/// startup profile's eval, `App.run`, or whatever first touched the app's
/// modules), and it's deterministic: it doesn't depend on timing, requests, or
/// whether the app serves HTTP at all. Closing early never loses a read by
/// code SageFs can see, because those are probed from the start. It only
/// narrows the reflection check to startup, which docs/hot-reload.md says.
///
/// Every patch is taken off BEFORE hot reload detours the method it's on
/// (`releaseBeforeDetour`), because Harmony re-patching a method later would
/// overwrite the detour and silently put the old code back.
///
/// BCL + FSharp.Core + Harmony only: compiled into the isolated FSI host too.
module SageFs.Middleware.ValueReadTracking

open System
open System.Collections.Generic
open System.Diagnostics
open System.Reflection
open System.Reflection.Emit
open HarmonyLib
open SageFs.Utils
open SageFs.Middleware.ValueReads

/// Whether a session watches value reads. Only hot reload needs it.
[<RequireQualifiedAccess>]
type ValueReadWatch =
  | WatchValueReads
  | IgnoreValueReads

// ── naming ───────────────────────────────────────────────────────────────────

let private hasModuleSuffix (t: Type) =
  t.GetCustomAttributes(typeof<CompilationRepresentationAttribute>, false)
  |> Array.exists (fun a ->
    (a :?> CompilationRepresentationAttribute).Flags.HasFlag CompilationRepresentationFlags.ModuleSuffix)

let private sourceName (t: Type) =
  match hasModuleSuffix t && t.Name.EndsWith("Module", StringComparison.Ordinal) with
  | true -> t.Name.Substring(0, t.Name.Length - "Module".Length)
  | false -> t.Name

/// The path the source spells for a type: its namespace and every enclosing
/// module, with FSI's `FSI_NNNN` wrappers left out, so a compiled copy and an
/// FSI copy of the same module name the same thing.
let private pathOf (t: Type) : string list =
  let rec enclosing (t: Type) (acc: Type list) =
    match isNull t.DeclaringType with
    | true -> t :: acc
    | false -> enclosing t.DeclaringType (t :: acc)
  let chain = enclosing t []
  let outer = List.head chain
  let ns =
    match outer.Namespace with
    | null
    | "" -> []
    | ns -> ns.Split('.') |> Array.toList
  ns @ (chain |> List.map sourceName)
  |> List.filter (fun segment -> not (SageFs.FsiNaming.isDynamicModuleSegment segment))

/// The value a getter belongs to, spelled the way the source spells it
/// (`StateFixture.State.greeting`).
let valueKeyOf (getter: MethodInfo) : string =
  String.concat "." (pathOf getter.DeclaringType @ [ getter.Name.Substring "get_".Length ])

let private displayName (m: MethodBase) =
  match isNull m.DeclaringType with
  | true -> m.Name
  | false -> sprintf "%s.%s" (m.DeclaringType.FullName |> Option.ofObj |> Option.defaultValue m.DeclaringType.Name) m.Name

let private readerIdOf (m: MethodBase) : ReaderId =
  sprintf "%O:%08x" m.Module.ModuleVersionId m.MetadataToken

// ── what's a module value ────────────────────────────────────────────────────

let private sourceConstruct (m: MemberInfo) =
  m.GetCustomAttributes(typeof<CompilationMappingAttribute>, false)
  |> Array.map (fun a -> (a :?> CompilationMappingAttribute).SourceConstructFlags)

let private isModule (t: Type) = sourceConstruct t |> Array.exists (fun f -> f = SourceConstructFlags.Module)

/// A module-level `let` value that isn't mutable: a public static property of
/// an F# module, with a getter and no setter. (F# only puts a
/// `CompilationMapping(Value)` on the ones with a backing field, so a constant
/// like `let greeting = "hello"` has to be found by shape.)
let private moduleValuesOf (t: Type) : MethodInfo list =
  match isModule t with
  | false -> []
  | true ->
    t.GetProperties(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
    |> Array.filter (fun p ->
      isNull (p.GetSetMethod true)
      && not (isNull (p.GetGetMethod())))
    |> Array.map (fun p -> p.GetGetMethod())
    |> Array.filter (fun g -> g.GetParameters().Length = 0 && not g.IsGenericMethodDefinition)
    |> Array.toList

let private typesOf (asm: Assembly) : Type list =
  try asm.GetTypes() |> Array.toList
  with
  | :? ReflectionTypeLoadException as ex -> ex.Types |> Array.filter (fun t -> not (isNull t)) |> Array.toList

let private allFlags =
  BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly

let private bodiesOf (t: Type) : MethodBase list =
  try
    [ yield! t.GetMethods allFlags |> Array.map (fun m -> m :> MethodBase)
      yield! t.GetConstructors allFlags |> Array.map (fun c -> c :> MethodBase) ]
  with _ -> []

/// An assembly built with optimizations may have inlined a getter into its
/// callers (or had the compiler copy a constant value in), so there'd be no
/// read left to see. SageFs builds with -p:Optimize=false; an assembly that
/// wasn't is untracked rather than trusted.
let private optimized (asm: Assembly) =
  match asm.GetCustomAttribute<DebuggableAttribute>() with
  | null -> true
  | d -> not d.IsJITOptimizerDisabled

/// The static field a getter returns, when its body is `ldsfld F; ret`.
[<RequireQualifiedAccess>]
type private Backing =
  | Field of FieldInfo
  | NoField

let private backingOf (getter: MethodInfo) : Backing =
  try
    match getter.GetMethodBody() with
    | null -> Backing.NoField
    | body ->
      match decode (body.GetILAsByteArray()) with
      | Ok [ { Op = load; Operand = Operand.Token token }; { Op = ret } ] when load = OpCodes.Ldsfld && ret = OpCodes.Ret ->
        Backing.Field(getter.Module.ResolveField token)
      | _ -> Backing.NoField
  with _ -> Backing.NoField

// ── the patches ──────────────────────────────────────────────────────────────

/// Who a patch reports to.
type private Instrument =
  | ReaderProbe of file: (LedgerEvent -> unit) * reader: ReaderId
  | GetterWatch of onRead: (MethodBase option -> unit)

let private harmony = lazy (Harmony "sagefs.valuereads")
let private instrumentsLock = obj ()
let private instruments = Dictionary<MethodBase, Instrument>()

/// Take whatever this file put on `m` off it. Idempotent.
let private release (m: MethodBase) =
  lock instrumentsLock (fun () ->
    match instruments.Remove m with
    | false -> ()
    | true ->
      try harmony.Value.Unpatch(m, HarmonyPatchType.All, harmony.Value.Id)
      with ex -> Log.warn "[ValueReads] couldn't take the watch off %s: %s" (displayName m) ex.Message)

/// Hot reload is about to detour `m`: anything watching it has to come off
/// first, because a later Harmony unpatch would overwrite the detour.
let releaseBeforeDetour (m: MethodBase) = release m

/// The Harmony patch methods. Static, and named the way Harmony wants them.
type ValueReadHooks =
  /// A probed reader just ran: file it and take the probe off.
  static member ReaderEntered(__originalMethod: MethodBase) : unit =
    let hit =
      lock instrumentsLock (fun () ->
        match instruments.TryGetValue __originalMethod with
        | true, ReaderProbe(file, reader) -> Some(file, reader)
        | _ -> None)
    match hit with
    | Some(file, reader) ->
      file (LedgerEvent.ReaderRan reader)
      Threading.ThreadPool.QueueUserWorkItem(fun _ -> release __originalMethod) |> ignore
    | None -> ()

  /// The startup window's watch saw a getter called. Filed before the getter
  /// body runs.
  static member GetterEntered(__originalMethod: MethodBase) : unit =
    let watch =
      lock instrumentsLock (fun () ->
        match instruments.TryGetValue __originalMethod with
        | true, GetterWatch onRead -> Some onRead
        | _ -> None)
    match watch with
    | None -> ()
    | Some onRead ->
      // Frame 0 is this hook, 1 the getter (its Harmony replacement); the
      // first frame after that which isn't the getter is the caller. A caller
      // that's itself patched shows up as its replacement, which Harmony maps
      // back to the original. A plain loop, so the frame numbers stay this
      // method's own.
      let mutable depth = 1
      let mutable caller : MethodBase option = None
      while caller.IsNone && depth <= 8 do
        let frame = StackFrame(depth, false)
        // Only a Harmony replacement (a DynamicMethod, no declaring type)
        // needs mapping back to its original. Harmony's mapping answers null
        // for an ordinary static initializer frame, so it isn't asked for one.
        let m =
          match frame.GetMethod() with
          | null -> null
          | raw when isNull raw.DeclaringType ->
            try Harmony.GetMethodFromStackframe frame
            with _ -> raw
          | raw -> raw
        match m with
        | null -> ()
        | m when m = __originalMethod -> ()
        | m -> caller <- Some m
        depth <- depth + 1
      onRead caller

let private hook (name: string) = HarmonyMethod(typeof<ValueReadHooks>.GetMethod name)

/// Put a patch on `m`, or say why it can't go on.
let private instrument (m: MethodBase) (what: Instrument) : Result<unit, string> =
  lock instrumentsLock (fun () ->
    match instruments.ContainsKey m with
    | true -> Error "it's already being watched"
    | false ->
      try
        match what with
        | ReaderProbe _ -> harmony.Value.Patch(m, prefix = hook "ReaderEntered") |> ignore
        | GetterWatch _ -> harmony.Value.Patch(m, prefix = hook "GetterEntered") |> ignore
        instruments.[m] <- what
        Ok()
      with ex -> Error(sprintf "Harmony couldn't patch it (%s)" ex.Message))

/// Why a reader can't carry a probe, before trying.
let private unprobeable (m: MethodBase) : Result<unit, string> =
  // `IsConstructor` is false for a `.cctor`, so ask the type. A Harmony copy
  // of a static initializer would have to write its `initonly` fields from
  // outside it, and a failure there breaks the app, so it isn't tried.
  if (m :? ConstructorInfo) && m.IsStatic then Error "it's a static initializer, which runs once and can't be watched"
  elif m.IsGenericMethodDefinition || m.ContainsGenericParameters then Error "it's generic"
  elif m.IsAbstract then Error "it has no body"
  else Ok()

// ── the tracker ──────────────────────────────────────────────────────────────

/// Did the eval that produced an assembly already run code in it before
/// SageFs could look? A submission's startup code runs during the eval.
[<RequireQualifiedAccess>]
type EvalCode =
  /// Nothing but definitions: no reader in it has run yet.
  | DefinitionsOnly
  /// It ran code, which may have called any reader it defined.
  | RanCode

/// One agent's view of the app's value reads.
[<Sealed>]
type Tracker() =
  let gate = obj ()
  let mutable ledger = Ledger.empty
  /// Getter (compiled or an FSI copy) -> value.
  let getters = Dictionary<MethodBase, string>()
  /// Backing field -> value.
  let fields = Dictionary<FieldInfo, string>()
  /// Getters carrying the startup window's watch.
  let watched = HashSet<MethodBase>()
  let scannedTypes = HashSet<Type>()
  /// Per module, the definition tokens of tracked getters and backing fields,
  /// so a call to anything else is skipped without resolving it.
  let definedHere = Dictionary<Module, HashSet<int>>()
  /// Per module, what a member reference (a call into another assembly)
  /// resolves to. Resolved once per module, not once per call site.
  let referenced = Dictionary<Module, Dictionary<int, MemberInfo option>>()
  let noteDefinition (m: Module) (token: int) =
    match definedHere.TryGetValue m with
    | true, set -> set.Add token |> ignore
    | false, _ -> definedHere.[m] <- HashSet [ token ]
  let isDefinedHere (m: Module) (token: int) =
    match definedHere.TryGetValue m with
    | true, set -> set.Contains token
    | false, _ -> false
  let mutable windowReads = 0

  let file (event: LedgerEvent) = lock gate (fun () -> ledger <- Ledger.step ledger event)

  let knownReader (caller: MethodBase) (value: string) =
    lock gate (fun () ->
      match Map.tryFind (readerIdOf caller) ledger.Readers with
      | Some reader when reader.Reads |> List.exists (fun (v, _, _) -> v = value) -> true
      | _ -> false)

  let onGetterRead (value: string) (caller: MethodBase option) =
    Threading.Interlocked.Increment &windowReads |> ignore
    match caller with
    | None -> file (LedgerEvent.GetterRead(value, Caller.Unknown "an unknown caller"))
    | Some caller when knownReader caller value -> file (LedgerEvent.GetterRead(value, Caller.Known(readerIdOf caller)))
    | Some caller -> file (LedgerEvent.GetterRead(value, Caller.Unknown(displayName caller)))

  /// Which instruction reads which tracked value, for one method body.
  let readsIn (m: MethodBase) (instrs: Instr[]) : (string * int * ReadFate) list =
    let typeArgs =
      match isNull m.DeclaringType with
      | true -> [||]
      | false when m.DeclaringType.IsGenericType -> m.DeclaringType.GetGenericArguments()
      | false -> [||]
    let methodArgs =
      match m.IsGenericMethod with
      | true -> m.GetGenericArguments()
      | false -> [||]
    // Token tables: 0x06 MethodDef, 0x04 FieldDef, 0x0A MemberRef. A getter
    // or backing field defined in this module is a definition token, so any
    // other definition token is skipped outright. A member reference is
    // resolved once per module; a getter is never generic, so neither is its
    // reference, and a MethodSpec (0x2B) is never a read.
    let resolveMember (token: int) : MemberInfo option =
      match token >>> 24 with
      | 0x06
      | 0x04 when not (isDefinedHere m.Module token) -> None
      | 0x06 -> (try Some(m.Module.ResolveMethod(token, typeArgs, methodArgs) :> MemberInfo) with _ -> None)
      | 0x04 -> (try Some(m.Module.ResolveField(token, typeArgs, methodArgs) :> MemberInfo) with _ -> None)
      | 0x0A ->
        let cache =
          match referenced.TryGetValue m.Module with
          | true, c -> c
          | false, _ ->
            let c = Dictionary<int, MemberInfo option>()
            referenced.[m.Module] <- c
            c
        match cache.TryGetValue token with
        | true, r -> r
        | false, _ ->
          let r =
            try Some(m.Module.ResolveMember token)
            with _ -> (try Some(m.Module.ResolveMember(token, typeArgs, methodArgs)) with _ -> None)
          cache.[token] <- r
          r
      | _ -> None
    let resolveMethod token =
      match resolveMember token with
      | Some(:? MethodBase as mb) -> Some mb
      | _ -> None
    let resolveField token =
      match resolveMember token with
      | Some(:? FieldInfo as f) -> Some f
      | _ -> None
    let valueRead (i: Instr) : (string * ReadKind) option =
      match i.Operand with
      | Operand.Token token ->
        let op = i.Op
        if op = OpCodes.Call || op = OpCodes.Callvirt || op = OpCodes.Ldftn || op = OpCodes.Ldvirtftn then
          match resolveMethod token with
          | Some callee when callee <> m ->
            match getters.TryGetValue callee with
            | true, value ->
              let kind =
                match op = OpCodes.Ldftn || op = OpCodes.Ldvirtftn with
                | true -> ReadKind.GetterPointer
                | false -> ReadKind.GetterCall
              Some(value, kind)
            | false, _ -> None
          | _ -> None
        elif op = OpCodes.Ldsfld || op = OpCodes.Ldsflda then
          match resolveField token with
          | Some field ->
            match fields.TryGetValue field with
            | true, value ->
              // The getter reading its own field isn't a read by the app.
              match getters.TryGetValue m with
              | true, own when own = value -> None
              | _ ->
                let kind =
                  match op = OpCodes.Ldsflda with
                  | true -> ReadKind.FieldAddress
                  | false -> ReadKind.FieldLoad
                Some(value, kind)
            | false, _ -> None
          | None -> None
        else None
      | _ -> None
    let hits = instrs |> Array.choose valueRead |> Array.map fst |> Array.distinct |> Array.toList
    let tokens = tokensOf m
    hits
    |> List.collect (fun value ->
      let classify (i: Instr) =
        match valueRead i with
        | Some(v, kind) when v = value -> ReadMatch.Reads kind
        | _ -> ReadMatch.NotARead
      sitesIn tokens classify instrs |> List.map (fun (offset, fate) -> value, offset, fate))

  /// Register the module values `types` define, so reads of them are found.
  let registerValues (types: Type list) =
    for t in types do
      for getter in moduleValuesOf t do
        let value = valueKeyOf getter
        getters.[getter] <- value
        noteDefinition getter.Module getter.MetadataToken
        match backingOf getter with
        | Backing.Field field ->
          fields.[field] <- value
          noteDefinition field.Module field.MetadataToken
        | Backing.NoField -> ()
        file (LedgerEvent.ValueTracked value)

  /// Scan every method in `types` for reads of registered values.
  /// `statusFor` decides how an escaping reader starts out.
  let scanBodies (types: Type list) (statusFor: MethodBase -> ReaderStatus) =
    for t in types do
      for m in bodiesOf t do
        let instrs =
          try
            match m.GetMethodBody() with
            | null -> Ok [||]
            | body ->
              match decode (body.GetILAsByteArray()) with
              | Ok instrs -> Ok(List.toArray instrs)
              | Error e -> Error(sprintf "%A" e)
          with ex -> Error ex.Message
        match instrs with
        | Error why -> Log.warn "[ValueReads] couldn't read the code of %s (%s); a value it reads won't be seen" (displayName m) why
        | Ok instrs ->
          match readsIn m instrs with
          | [] -> ()
          | reads ->
            let reader = { Id = readerIdOf m; Name = displayName m; Reads = reads }
            let escapes = reads |> List.exists (fun (_, _, fate) -> fate <> ReadFate.Discarded)
            let status =
              match escapes with
              // Running it can't keep anything, so there's nothing to watch.
              | false -> ReaderStatus.Armed
              | true -> statusFor m
            file (LedgerEvent.ReaderFound(reader, status))

  /// Probe an escaping reader, or say why it can't be.
  let probe (m: MethodBase) : ReaderStatus =
    match unprobeable m |> Result.bind (fun () -> instrument m (ReaderProbe(file, readerIdOf m))) with
    | Ok() -> ReaderStatus.Armed
    | Error why -> ReaderStatus.Unwatchable why

  /// Take the startup window's watch off every getter, and say so to the ledger.
  let closeWindow () =
    let wasOpen =
      lock gate (fun () ->
        match ledger.Window with
        | StartupWindow.Open ->
          ledger <- Ledger.step ledger LedgerEvent.StartupEnded
          true
        | StartupWindow.Closed -> false)
    match wasOpen with
    | false -> ()
    | true ->
      let getterList = lock gate (fun () -> List.ofSeq watched)
      for g in getterList do
        release g
      lock gate (fun () -> watched.Clear())

  /// Start tracking the project's compiled assemblies. Called when the agent
  /// starts, before any of the user's code runs.
  member _.Track(assemblies: Assembly list) =
    let debugBuilt, optimizedBuilt = assemblies |> List.partition (optimized >> not)
    for asm in optimizedBuilt do
      for t in typesOf asm do
        for getter in moduleValuesOf t do
          file (LedgerEvent.ValueUntracked(valueKeyOf getter, sprintf "%s was built with optimizations, so the code that reads it may hold its own copy of it" (asm.GetName().Name)))
    // Every value first, so a read in one project of a value in another is found.
    let types = debugBuilt |> List.collect typesOf |> List.filter (fun t -> scannedTypes.Add t)
    registerValues types
    scanBodies types probe
    // The window opens now: watch every getter.
    let all = lock gate (fun () -> List.ofSeq getters)
    for KeyValue(getter, value) in all do
      match instrument getter (GetterWatch(onGetterRead value)) with
      | Ok() -> lock gate (fun () -> watched.Add getter |> ignore)
      | Error why ->
        // Without the watch, a read through reflection during startup would go
        // unseen, so the value can't be vouched for.
        file (LedgerEvent.ValueUntracked(value, sprintf "SageFs couldn't watch its getter (%s)" why))

  /// Scan an assembly an eval just produced (FSI's), before hot reload detours
  /// anything in it.
  member _.ScanEval(asm: Assembly) =
    let types = typesOf asm |> List.filter (fun t -> scannedTypes.Add t)
    let isStartupCode (t: Type) =
      match t.FullName with
      | null -> false
      | name -> name.StartsWith("<StartupCode$", StringComparison.Ordinal)
    let ranCode =
      types
      |> List.filter isStartupCode
      |> List.collect bodiesOf
      |> List.exists (fun m ->
        try
          match m.GetMethodBody() with
          | null -> false
          | body ->
            match decode (body.GetILAsByteArray()) with
            | Ok instrs -> instrs |> List.exists (fun i -> i.Op <> OpCodes.Ret && i.Op <> OpCodes.Nop)
            | Error _ -> true
        with _ -> true)
    let statusFor (m: MethodBase) =
      match ranCode with
      | true -> ReaderStatus.Unwatchable "it was defined by an eval that ran code before SageFs could watch it"
      | false -> probe m
    registerValues types
    scanBodies types statusFor

  /// The eval that just finished. The window closes at the end of the first
  /// one after the app's code has read one of its values, and at a save.
  member _.EvalFinished(isFileSave: bool) =
    match isFileSave || Threading.Volatile.Read &windowReads > 0 with
    | true -> closeWindow ()
    | false -> ()

  /// Everything known about each value's reads. Asking closes the window: a
  /// save is past startup by definition.
  member _.Evidence(values: string list) : ValueEvidence list =
    closeWindow ()
    lock gate (fun () -> values |> List.map (Ledger.evidence ledger))

  /// The ledger as it stands, for tests.
  member _.Ledger = lock gate (fun () -> ledger)
