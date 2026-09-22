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

// ── the stack ────────────────────────────────────────────────────────────────

let private coreLib = typeof<obj>.Assembly
let private reflectionNamespace = typeof<PropertyInfo>.Namespace

/// CoreLib types a reflective call passes through on its way to the target,
/// besides everything in System.Reflection.
let private plumbingTypes =
  HashSet<Type>(
    [ typeof<Type>
      typeof<Type>.GetType() // RuntimeType
      typeof<Delegate>
      typeof<MulticastDelegate>
      typeof<Activator>
      typeof<RuntimeMethodHandle>
      typeof<RuntimeFieldHandle>
      typeof<RuntimeTypeHandle> ])

/// Is `m` part of the machinery a reflective call runs through?
let private isPlumbing (m: MethodBase) =
  match isNull m.DeclaringType with
  | true -> false
  | false ->
    let t = m.DeclaringType
    t.Assembly = coreLib
    && (plumbingTypes.Contains t
        || (match t.Namespace with
            | null -> false
            | ns -> ns.StartsWith(reflectionNamespace, StringComparison.Ordinal)))

/// How a caller reached a tracked value, as the stack tells it.
[<RequireQualifiedAccess>]
type private Walked =
  /// It called the getter itself, nothing in between.
  | Direct of caller: MethodBase
  /// It made a reflection call that got there. `offset` is the IL offset the
  /// runtime reports for that call in the caller's body (-1 when it can't).
  | ThroughReflection of caller: MethodBase * offset: int
  /// The first frame past SageFs and the reflection machinery isn't code
  /// SageFs can name (generated code, or no frame at all).
  | Opaque of why: string

[<RequireQualifiedAccess>]
type private FrameKind =
  | Ours
  | Target
  | Plumbing
  | Generated
  | Code of MethodBase

let private frameMethod (frame: StackFrame) : MethodBase =
  match frame.GetMethod() with
  | null -> null
  // A Harmony replacement is a DynamicMethod with no declaring type; Harmony
  // maps it back to the method it replaced.
  | raw when isNull raw.DeclaringType ->
    try
      match Harmony.GetMethodFromStackframe frame with
      | null -> raw
      | original -> original
    with _ -> raw
  | raw -> raw

/// This module, as a type: every hook, wrapper and closure here is nested in it.
let private ownModule = typeof<ValueReadWatch>.DeclaringType

let private isOurs (m: MethodBase) =
  let rec inside (t: Type) =
    match t with
    | null -> false
    | t when t = ownModule -> true
    | t -> inside t.DeclaringType
  inside m.DeclaringType

/// Walk out from the hook that's running to whoever made the read, past
/// SageFs's own frames and `target` (the thing being read).
/// One StackTrace: in the spike that cost about 13 us, where each
/// `StackFrame(n)` cost about 10 us on its own.
let private walk (target: MethodBase -> bool) : Walked =
  let frames =
    match StackTrace(1, false).GetFrames() with
    | null -> [||]
    | fs -> fs
  let kinds =
    frames
    |> Array.truncate 64
    |> Array.map (fun frame ->
      let m = frameMethod frame
      let kind =
        match m with
        | null -> FrameKind.Generated
        | m when isNull m.DeclaringType -> FrameKind.Generated
        | m when isOurs m -> FrameKind.Ours
        | m when target m -> FrameKind.Target
        | m when isPlumbing m -> FrameKind.Plumbing
        | m -> FrameKind.Code m
      kind, frame)
  // A generated frame INSIDE the machinery (an invoke stub) is skipped: the
  // next real frame out is machinery too. One right under real code is the
  // real caller, and it can't be named.
  let rec go (i: int) (throughReflection: bool) =
    match i >= kinds.Length with
    | true -> Walked.Opaque "no caller was on the stack"
    | false ->
      match fst kinds.[i] with
      | FrameKind.Ours
      | FrameKind.Target -> go (i + 1) throughReflection
      | FrameKind.Plumbing -> go (i + 1) true
      | FrameKind.Generated ->
        let rec nextReal j =
          match j >= kinds.Length with
          | true -> FrameKind.Generated
          | false ->
            match fst kinds.[j] with
            | FrameKind.Generated -> nextReal (j + 1)
            | other -> other
        match nextReal (i + 1) with
        | FrameKind.Plumbing
        | FrameKind.Ours
        | FrameKind.Target -> go (i + 1) throughReflection
        | FrameKind.Generated
        | FrameKind.Code _ -> Walked.Opaque "generated code SageFs can't read made the call"
      | FrameKind.Code m ->
        match throughReflection with
        | true -> Walked.ThroughReflection(m, (snd kinds.[i]).GetILOffset())
        | false -> Walked.Direct m
  go 0 false

// ── the patches ──────────────────────────────────────────────────────────────

/// What a getter watch does with a read. While the app starts it files the
/// caller, as it always has. After that (it only stays on in
/// exact-every-read) it walks past any reflection to the real caller.
type private GetterWatcher =
  { Window: unit -> StartupWindow
    AtStartup: MethodBase option -> unit
    AfterStartup: Walked -> unit }

/// Who a patch reports to.
type private Instrument =
  | ReaderProbe of file: (LedgerEvent -> unit) * reader: ReaderId
  | GetterWatch of watcher: GetterWatcher
  /// A caller whose reflection calls were rewired to say which call they are.
  | CallerProbe

let private harmony = lazy (Harmony "sagefs.valuereads")
let private instrumentsLock = obj ()
let private instruments = Dictionary<MethodBase, Instrument>()
/// Methods hot reload has re-pointed. Nothing may be patched onto them again:
/// Harmony would rebuild them from the original body and drop the detour.
let private detoured = HashSet<MethodBase>()

/// Take whatever this file put on `m` off it. Idempotent.
let private release (m: MethodBase) =
  lock instrumentsLock (fun () ->
    match instruments.Remove m with
    | false -> ()
    | true ->
      try harmony.Value.Unpatch(m, HarmonyPatchType.All, harmony.Value.Id)
      with ex -> Log.warn "[ValueReads] couldn't take the watch off %s: %s" (displayName m) ex.Message)

/// Hot reload is about to detour `m`: anything watching it has to come off
/// first, because a later Harmony unpatch would overwrite the detour. And
/// nothing goes back on it.
let releaseBeforeDetour (m: MethodBase) =
  lock instrumentsLock (fun () -> detoured.Add m |> ignore)
  release m

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
        | true, GetterWatch watcher -> Some watcher
        | _ -> None)
    match watch with
    | None -> ()
    | Some watcher when watcher.Window() = StartupWindow.Closed ->
      // Only exact-every-read keeps the watch on past startup. Never throws
      // into the app: a walk that fails is a read nobody can name.
      let walked =
        try
          walk (fun m -> m = __originalMethod)
        with ex -> Walked.Opaque(sprintf "SageFs couldn't walk the stack (%s)" ex.Message)
      try watcher.AfterStartup walked
      with ex -> Log.warn "[ValueReads] a getter watch failed to file a read: %s" ex.Message
    | Some watcher ->
      let onRead = watcher.AtStartup
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

// ── reflection reads after startup ───────────────────────────────────────────
//
// A reflective read has no read of the value in anyone's IL, so no probe sees
// it. Once the startup window's getter watch is off, the only place left to
// see it is the reflection machinery itself: a prefix on the entry points
// every reflective read of a getter or a backing field goes through.
//
// ProbeCallers makes the "who" cheap. The first reflective read from a caller
// walks the stack once (about 16 us), then rewires that caller's reflection
// calls: `callvirt PropertyInfo.GetValue` becomes `ldc.i4 <site>; call
// ReflectionCallSite.PropertyGetValue`, which puts the site in a
// [<ThreadStatic>] slot for exactly that one call. The first reflection entry
// the call reaches CONSUMES the slot, tracked value or not, so a reflective
// read further in (the target's own code) finds it empty and walks instead of
// being pinned on the outer caller. Empty means walk. Never guess.

/// Which reflection call a rewired caller is making on this thread right now
/// (a site id), or 0.
type ReflectionSlot =
  [<ThreadStatic; DefaultValue>]
  static val mutable private site: int
  /// The member the armed call is about to reach (a method or field handle).
  /// The slot names a read only when the entry is for this exact member.
  [<ThreadStatic; DefaultValue>]
  static val mutable private target: nativeint
  /// Non-zero while SageFs's own reflection hook runs on this thread, so the
  /// hook never watches itself (Harmony uses reflection too).
  [<ThreadStatic; DefaultValue>]
  static val mutable private busy: int
  /// Arm the slot for one call: `site` is about to reach `target`.
  static member Arm(site: int, target: nativeint) =
    ReflectionSlot.target <- target
    ReflectionSlot.site <- site
  static member Disarm() =
    ReflectionSlot.site <- 0
    ReflectionSlot.target <- 0n
  static member Site = ReflectionSlot.site
  static member Target = ReflectionSlot.target
  static member Busy
    with get () = ReflectionSlot.busy
    and set (v: int) = ReflectionSlot.busy <- v

/// What a rewired reflection call site calls instead. Each one arms the slot
/// with its site and the exact member it's about to reach, for the one call
/// it makes, and disarms it after however that call ends. Public: the
/// rewired code in the user's assembly calls these.
type ReflectionCallSite =
  static member private KeyOf(m: MethodBase) =
    match m with
    | null -> 0n
    | m -> try m.MethodHandle.Value with _ -> 0n

  static member PropertyGetValue(property: PropertyInfo, target: obj, site: int) : obj =
    ReflectionSlot.Arm(site, (match property with | null -> 0n | p -> ReflectionCallSite.KeyOf p.GetMethod))
    try property.GetValue target
    finally ReflectionSlot.Disarm()

  static member PropertyGetValueAt(property: PropertyInfo, target: obj, index: obj[], site: int) : obj =
    ReflectionSlot.Arm(site, (match property with | null -> 0n | p -> ReflectionCallSite.KeyOf p.GetMethod))
    try property.GetValue(target, index)
    finally ReflectionSlot.Disarm()

  static member MethodInvoke(m: MethodBase, target: obj, args: obj[], site: int) : obj =
    ReflectionSlot.Arm(site, ReflectionCallSite.KeyOf m)
    try m.Invoke(target, args)
    finally ReflectionSlot.Disarm()

  static member FieldGetValue(field: FieldInfo, target: obj, site: int) : obj =
    ReflectionSlot.Arm(site, (match field with | null -> 0n | f -> (try f.FieldHandle.Value with _ -> 0n)))
    try field.GetValue target
    finally ReflectionSlot.Disarm()

/// The method a quotation calls, so no method is ever found by its name.
let rec private methodOf (e: Quotations.Expr) : MethodInfo option =
  match e with
  | Quotations.Patterns.Call(_, m, _) -> Some m
  | Quotations.Patterns.PropertyGet(_, p, _) -> Option.ofObj (p.GetGetMethod true)
  | Quotations.Patterns.Lambda(_, body) -> methodOf body
  | Quotations.Patterns.Let(_, _, body) -> methodOf body
  | _ -> None

let private fieldOf (e: Quotations.Expr) : FieldInfo option =
  match e with
  | Quotations.Patterns.FieldGet(_, f) -> Some f
  | _ -> None

/// The reflection calls a caller can have rewired, and what replaces each.
let private rewirable : (MethodInfo * MethodInfo) list =
  [ methodOf <@ fun (p: PropertyInfo) -> p.GetValue(null) @>, methodOf <@ ReflectionCallSite.PropertyGetValue(null, null, 0) @>
    methodOf <@ fun (p: PropertyInfo) -> p.GetValue(null, null) @>, methodOf <@ ReflectionCallSite.PropertyGetValueAt(null, null, null, 0) @>
    methodOf <@ fun (m: MethodBase) -> m.Invoke(null, null) @>, methodOf <@ ReflectionCallSite.MethodInvoke(null, null, null, 0) @>
    methodOf <@ fun (f: FieldInfo) -> f.GetValue(null) @>, methodOf <@ ReflectionCallSite.FieldGetValue(null, null, 0) @> ]
  |> List.choose (fun (call, wrap) ->
    match call, wrap with
    | Some call, Some wrap -> Some(call, wrap)
    | _ -> None)

let private rewireFor (callee: MethodBase) : MethodInfo option =
  rewirable |> List.tryPick (fun (call, wrap) -> match (call :> MethodBase) = callee with | true -> Some wrap | false -> None)

/// One reflection call in a caller's body, and what the caller does with what
/// it returns.
[<Sealed>]
type private CallSite(id: int, reader: string, offset: int, fate: ReadFate) =
  let filed = HashSet<string>()
  /// The value this site filed last, so a loop reading one value files once
  /// without taking a lock.
  [<VolatileField>]
  let mutable last : string = null
  member _.Id = id
  member _.Caller = ReflectiveCaller.AtSite(reader, offset, fate)
  member _.Reader = reader
  member _.Offset = offset
  member _.Fate = fate
  /// True the first time this site reads `value`.
  member _.FirstReadOf(value: string) : bool =
    match obj.ReferenceEquals(last, value) with
    | true -> false
    | false ->
      let first = lock filed (fun () -> filed.Add value)
      last <- value
      first

/// A value that's replaced whole, never changed in place, so a hook reads it
/// without a lock.
[<Sealed>]
type private Published<'T>(initial: 'T) =
  [<VolatileField>]
  let mutable current = initial
  member _.Value
    with get () = current
    and set (next: 'T) = current <- next

let private sitesLock = obj ()
/// Site id -> site. Index 0 is never used (0 means "no site").
let private sites = Published<CallSite[]>([| CallSite(0, "", 0, ReadFate.Discarded) |])
/// Whether a caller calls anything without naming it: a delegate's `Invoke` or
/// a `calli`. A delegate bound straight to `PropertyInfo.GetValue` leaves no
/// frame of its own, so a walk would land on the caller at a call that isn't a
/// reflection call at all.
[<RequireQualifiedAccess>]
type private IndirectCalls =
  | NoIndirectCalls
  | CallsIndirectly

/// Every reflection call in a caller, and whether it calls indirectly. Found
/// once per caller.
type private CallerSites =
  { Sites: CallSite list
    Indirect: IndirectCalls }

let private callerSites = Dictionary<MethodBase, CallerSites>()
/// For a rewired caller: the site id of each rewirable call, in IL order.
let private rewiredSites = Dictionary<MethodBase, int list>()

/// The reflection calls in `caller`'s body, each with what the caller does
/// with the result: the same escape check a compiled read gets.
let private reflectionSitesOf (caller: MethodBase) : CallerSites =
  lock sitesLock (fun () ->
    match callerSites.TryGetValue caller with
    | true, known -> known
    | false, _ ->
      let found, indirect =
        try
          match caller.GetMethodBody() with
          | null -> [], IndirectCalls.CallsIndirectly
          | body ->
            match decode (body.GetILAsByteArray()) with
            | Error _ -> [], IndirectCalls.CallsIndirectly
            | Ok instrs ->
              let instrs = List.toArray instrs
              let tokens = tokensOf caller
              let indirect =
                let viaDelegate (i: Instr) =
                  match i.Operand with
                  | Operand.Token token when i.Op = OpCodes.Call || i.Op = OpCodes.Callvirt ->
                    match (try Some(caller.Module.ResolveMethod token) with _ -> None) with
                    | Some callee when not (isNull callee.DeclaringType) -> callee.DeclaringType.IsSubclassOf typeof<Delegate>
                    | Some _ -> false
                    | None -> true
                  | _ -> false
                match instrs |> Array.exists (fun i -> i.Op = OpCodes.Calli || viaDelegate i) with
                | true -> IndirectCalls.CallsIndirectly
                | false -> IndirectCalls.NoIndirectCalls
              let sites =
                instrs
                |> Array.indexed
                |> Array.choose (fun (index, i) ->
                  match i.Operand with
                  | Operand.Token token when i.Op = OpCodes.Call || i.Op = OpCodes.Callvirt ->
                    match (try Some(caller.Module.ResolveMethod token) with _ -> None) with
                    | Some callee when isPlumbing callee ->
                      let fate =
                        match callee with
                        | :? MethodInfo as mi when mi.ReturnType <> typeof<Void> -> fateAt tokens instrs index
                        | _ -> ReadFate.Escaped(Escape.Untraced(sprintf "%s read it, and that returns nothing SageFs can follow" (displayName callee)))
                      Some(i.Offset, fate)
                    | _ -> None
                  | _ -> None)
                |> Array.toList
              sites, indirect
        with _ -> [], IndirectCalls.CallsIndirectly
      let made =
        found
        |> List.map (fun (offset, fate) ->
          let site = CallSite(sites.Value.Length, displayName caller, offset, fate)
          sites.Value <- Array.append sites.Value [| site |]
          site)
      let result = { Sites = made; Indirect = indirect }
      callerSites.[caller] <- result
      result)

/// The rewirable calls in `caller`, in IL order, as site ids.
let private rewirableSiteIds (caller: MethodBase) (known: CallSite list) : int list =
  try
    match caller.GetMethodBody() with
    | null -> []
    | body ->
      match decode (body.GetILAsByteArray()) with
      | Error _ -> []
      | Ok instrs ->
        instrs
        |> List.choose (fun i ->
          match i.Operand with
          | Operand.Token token when i.Op = OpCodes.Call || i.Op = OpCodes.Callvirt ->
            match (try Some(caller.Module.ResolveMethod token) with _ -> None) with
            | Some callee when (rewireFor callee).IsSome ->
              known |> List.tryFind (fun s -> s.Offset = i.Offset) |> Option.map (fun s -> s.Id)
            | _ -> None
          | _ -> None)
  with _ -> []

/// The Harmony transpiler that rewires a caller's reflection calls. Harmony
/// runs it again whenever it rebuilds the method, so it reads the site ids
/// from `rewiredSites` and always gives the same answer.
type ReflectionRewire =
  static member Transpile(instructions: IEnumerable<CodeInstruction>, __originalMethod: MethodBase) : IEnumerable<CodeInstruction> =
    let ids =
      lock sitesLock (fun () ->
        match rewiredSites.TryGetValue __originalMethod with
        | true, ids -> ids
        | false, _ -> [])
    let out = List<CodeInstruction>()
    let mutable remaining = ids
    for ins in instructions do
      let wrap =
        match ins.operand, remaining with
        | (:? MethodBase as callee), _ :: _ when ins.opcode = OpCodes.Call || ins.opcode = OpCodes.Callvirt -> rewireFor callee
        | _ -> None
      match wrap, remaining with
      | Some wrap, site :: rest ->
        remaining <- rest
        let load = CodeInstruction(OpCodes.Ldc_I4, box site)
        // A `tail.` prefix has to stay right in front of the call, so the site
        // goes in before it, and so do its labels.
        let prefixAt = out.Count - 1
        match prefixAt >= 0 && out.[prefixAt].opcode = OpCodes.Tailcall with
        | true ->
          let tail = out.[prefixAt]
          load.labels.AddRange tail.labels
          load.blocks.AddRange tail.blocks
          tail.labels.Clear()
          tail.blocks.Clear()
          out.Insert(prefixAt, load)
        | false ->
          load.labels.AddRange ins.labels
          load.blocks.AddRange ins.blocks
          out.Add load
        out.Add(CodeInstruction(OpCodes.Call, box wrap))
      | _ -> out.Add ins
    out :> IEnumerable<CodeInstruction>

/// Put a patch on `m`, or say why it can't go on.
let private instrument (m: MethodBase) (what: Instrument) : Result<unit, string> =
  lock instrumentsLock (fun () ->
    match instruments.ContainsKey m, detoured.Contains m with
    | true, _ -> Error "it's already being watched"
    | _, true -> Error "hot reload has re-pointed it, and patching it again would undo that"
    | false, false ->
      try
        match what with
        | ReaderProbe _ -> harmony.Value.Patch(m, prefix = hook "ReaderEntered") |> ignore
        | GetterWatch _ -> harmony.Value.Patch(m, prefix = hook "GetterEntered") |> ignore
        | CallerProbe ->
          harmony.Value.Patch(m, transpiler = HarmonyMethod(typeof<ReflectionRewire>.GetMethod(nameof ReflectionRewire.Transpile)))
          |> ignore
        instruments.[m] <- what
        Ok()
      with ex -> Error(sprintf "Harmony couldn't patch it (%s)" ex.Message))

/// How the reflection machinery reached a tracked value.
[<RequireQualifiedAccess>]
type private ReflectionEntry =
  /// `MethodBase.Invoke` of its getter (every `PropertyInfo.GetValue` lands here).
  | Invoked
  /// `FieldInfo.GetValue` of its backing field.
  | FieldRead
  /// A delegate made from its getter, which can then run anywhere.
  | DelegateMade

/// What a tracked value's reflection watch reports to.
type private IReflectionSink =
  abstract Reflected: entry: ReflectionEntry * site: int -> unit

/// Getter (by method handle) and backing field (by field handle) -> sink.
/// Replaced, never changed, so the hook reads them without a lock.
let private methodSinks = Published(Dictionary<nativeint, IReflectionSink>())
let private fieldSinks = Published(Dictionary<nativeint, IReflectionSink>())
let private sinksLock = obj ()

let private addSink (target: MemberInfo) (sink: IReflectionSink) =
  lock sinksLock (fun () ->
    match target with
    | :? MethodBase as m ->
      let next = Dictionary<nativeint, IReflectionSink>(methodSinks.Value)
      next.[m.MethodHandle.Value] <- sink
      methodSinks.Value <- next
    | :? FieldInfo as f ->
      let next = Dictionary<nativeint, IReflectionSink>(fieldSinks.Value)
      next.[f.FieldHandle.Value] <- sink
      fieldSinks.Value <- next
    | _ -> ())

/// Which entry point a hook sits on. The canary counts hits per point, so a
/// point whose patch was lost shows up by itself.
[<RequireQualifiedAccess>]
type private EntryPoint =
  | Invoke
  | FieldGetValue
  | CreateDelegate
  | CreateDelegateFor
  | DelegateCreateDelegate
  | DelegateCreateDelegateFor

let private entryPointIndex (point: EntryPoint) =
  match point with
  | EntryPoint.Invoke -> 0
  | EntryPoint.FieldGetValue -> 1
  | EntryPoint.CreateDelegate -> 2
  | EntryPoint.CreateDelegateFor -> 3
  | EntryPoint.DelegateCreateDelegate -> 4
  | EntryPoint.DelegateCreateDelegateFor -> 5

let private allEntryPoints =
  [ EntryPoint.Invoke
    EntryPoint.FieldGetValue
    EntryPoint.CreateDelegate
    EntryPoint.CreateDelegateFor
    EntryPoint.DelegateCreateDelegate
    EntryPoint.DelegateCreateDelegateFor ]

/// Something SageFs reads through every entry point to prove each patch still
/// fires. On this runtime a Harmony patch on a CoreLib method that hasn't
/// been recompiled yet is lost when tiered compilation recompiles it, and it
/// doesn't come back by itself. So a canary that still fires means the patch
/// never lapsed: the only thing that revives one is SageFs patching it again.
type ReflectionCanary =
  [<DefaultValue>]
  static val mutable private field: int
  static member Value : int = ReflectionCanary.field

let private canaryGetter = methodOf <@ ReflectionCanary.Value @>
let private canaryField =
  typeof<ReflectionCanary>.GetFields(BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
  |> Array.tryHead
let private canaryMethodKey =
  match canaryGetter with
  | Some g -> g.MethodHandle.Value
  | None -> 0n
let private canaryFieldKey =
  match canaryField with
  | Some f -> f.FieldHandle.Value
  | None -> 0n
let private canaryHits : int64[] = Array.zeroCreate (List.length allEntryPoints)

/// The prefixes on the reflection entry points. Each consumes the slot first,
/// whatever it's looking at, and never throws into the app.
type ReflectionHooks =
  static member private Dispatch(sinks: Dictionary<nativeint, IReflectionSink>, key: nativeint, entry: ReflectionEntry, point: EntryPoint) : unit =
    // Every entry uses the slot up, whatever it's looking at. It names this
    // read only when it was armed for this exact member.
    let armed = ReflectionSlot.Site
    let site =
      match armed with
      | 0 -> 0
      | _ ->
        let forThis = ReflectionSlot.Target = key
        ReflectionSlot.Disarm()
        match forThis with
        | true -> armed
        | false -> 0
    match key = canaryMethodKey || key = canaryFieldKey with
    | true -> Threading.Interlocked.Increment(&canaryHits.[entryPointIndex point]) |> ignore
    | false ->
      match ReflectionSlot.Busy with
      | 0 ->
        match sinks.TryGetValue key with
        | true, sink ->
          ReflectionSlot.Busy <- 1
          try
            try sink.Reflected(entry, site)
            with ex -> Log.warn "[ValueReads] the reflection watch failed on a read: %s" ex.Message
          finally
            ReflectionSlot.Busy <- 0
        | false, _ -> ()
      | _ -> ()

  static member private MethodKey(m: MethodBase) = try m.MethodHandle.Value with _ -> 0n

  static member InvokeEntered(__instance: MethodBase) : unit =
    ReflectionHooks.Dispatch(methodSinks.Value, ReflectionHooks.MethodKey __instance, ReflectionEntry.Invoked, EntryPoint.Invoke)

  static member FieldReadEntered(__instance: FieldInfo) : unit =
    let key = try __instance.FieldHandle.Value with _ -> 0n
    ReflectionHooks.Dispatch(fieldSinks.Value, key, ReflectionEntry.FieldRead, EntryPoint.FieldGetValue)

  static member DelegateEntered(__instance: MethodInfo) : unit =
    ReflectionHooks.Dispatch(methodSinks.Value, ReflectionHooks.MethodKey __instance, ReflectionEntry.DelegateMade, EntryPoint.CreateDelegate)

  static member DelegateForEntered(__instance: MethodInfo) : unit =
    ReflectionHooks.Dispatch(methodSinks.Value, ReflectionHooks.MethodKey __instance, ReflectionEntry.DelegateMade, EntryPoint.CreateDelegateFor)

  static member StaticDelegateEntered(method: MethodInfo) : unit =
    ReflectionHooks.Dispatch(methodSinks.Value, ReflectionHooks.MethodKey method, ReflectionEntry.DelegateMade, EntryPoint.DelegateCreateDelegate)

  static member StaticDelegateForEntered(method: MethodInfo) : unit =
    ReflectionHooks.Dispatch(methodSinks.Value, ReflectionHooks.MethodKey method, ReflectionEntry.DelegateMade, EntryPoint.DelegateCreateDelegateFor)

/// The runtime's own override of `baseMethod` on `runtimeType`.
let private overrideOn (runtimeType: Type) (baseMethod: MethodInfo) : MethodInfo option =
  runtimeType.GetMethods(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
  |> Array.tryFind (fun m -> m.GetBaseDefinition() = baseMethod.GetBaseDefinition())

let private describePoint (point: EntryPoint) =
  match point with
  | EntryPoint.Invoke -> "MethodBase.Invoke"
  | EntryPoint.FieldGetValue -> "FieldInfo.GetValue"
  | EntryPoint.CreateDelegate -> "MethodInfo.CreateDelegate(Type)"
  | EntryPoint.CreateDelegateFor -> "MethodInfo.CreateDelegate(Type, object)"
  | EntryPoint.DelegateCreateDelegate -> "Delegate.CreateDelegate(Type, MethodInfo, bool)"
  | EntryPoint.DelegateCreateDelegateFor -> "Delegate.CreateDelegate(Type, object, MethodInfo, bool)"

/// The method each entry point patches, found by what the BCL's public API
/// calls (never by a name string), and the hook it gets.
let private entryPoints : Lazy<(EntryPoint * MethodInfo option * string) list> =
  lazy
    (let invoke = methodOf <@ fun (m: MethodBase) -> m.Invoke(null, BindingFlags.Default, null, null, null) @>
     let fieldGet = methodOf <@ fun (f: FieldInfo) -> f.GetValue(null) @>
     let delegate1 = methodOf <@ fun (m: MethodInfo) -> m.CreateDelegate(typeof<Action>) @>
     let delegate2 = methodOf <@ fun (m: MethodInfo) -> m.CreateDelegate(typeof<Action>, null) @>
     let runtimeMethod = invoke |> Option.map (fun m -> m.GetType())
     let runtimeField = fieldOf <@ DBNull.Value @> |> Option.map (fun f -> f.GetType())
     let on (runtime: Type option) (baseMethod: MethodInfo option) =
       match runtime, baseMethod with
       | Some t, Some b -> overrideOn t b
       | _ -> None
     [ EntryPoint.Invoke, on runtimeMethod invoke, nameof ReflectionHooks.InvokeEntered
       EntryPoint.FieldGetValue, on runtimeField fieldGet, nameof ReflectionHooks.FieldReadEntered
       EntryPoint.CreateDelegate, on runtimeMethod delegate1, nameof ReflectionHooks.DelegateEntered
       EntryPoint.CreateDelegateFor, on runtimeMethod delegate2, nameof ReflectionHooks.DelegateForEntered
       EntryPoint.DelegateCreateDelegate,
       methodOf <@ fun (m: MethodInfo) -> Delegate.CreateDelegate(typeof<Action>, m, false) @>,
       nameof ReflectionHooks.StaticDelegateEntered
       EntryPoint.DelegateCreateDelegateFor,
       methodOf <@ fun (m: MethodInfo) -> Delegate.CreateDelegate(typeof<Action>, null, m, false) @>,
       nameof ReflectionHooks.StaticDelegateForEntered ])

/// Read the canary through one entry point. Only the entry matters; a call
/// that throws after the prefix ran still counted.
let private exercise (point: EntryPoint) =
  let func = typeof<Func<int>>
  try
    match point, canaryGetter, canaryField with
    | EntryPoint.Invoke, Some g, _ -> g.Invoke(null, null) |> ignore
    | EntryPoint.FieldGetValue, _, Some f -> f.GetValue null |> ignore
    | EntryPoint.CreateDelegate, Some g, _ -> g.CreateDelegate func |> ignore
    | EntryPoint.CreateDelegateFor, Some g, _ -> g.CreateDelegate(func, null) |> ignore
    | EntryPoint.DelegateCreateDelegate, Some g, _ -> Delegate.CreateDelegate(func, g, false) |> ignore
    | EntryPoint.DelegateCreateDelegateFor, Some g, _ -> Delegate.CreateDelegate(func, null, g, false) |> ignore
    | _ -> ()
  with _ -> ()

/// The entry points whose patch isn't firing any more.
let private lapsedPoints () : EntryPoint list =
  allEntryPoints
  |> List.filter (fun point ->
    let i = entryPointIndex point
    let before = Threading.Interlocked.Read &canaryHits.[i]
    exercise point
    Threading.Interlocked.Read &canaryHits.[i] = before)

let private patchPoint (m: MethodInfo) (hookName: string) =
  harmony.Value.Patch(m, prefix = HarmonyMethod(typeof<ReflectionHooks>.GetMethod hookName)) |> ignore

/// How many lapses this process has seen. A tracker that started before the
/// latest one can't vouch for reflection reads any more.
let private lapses = Published<int>(0)
let private lapseLock = obj ()
let private lapseReasons = Published<string list>([])

/// Check every entry point's canary. Any that stopped firing is patched again
/// (it's been recompiled, and a patch on the recompiled code stays) and the
/// lapse is counted.
let private checkEntryWatch () =
  match lapsedPoints () with
  | [] -> ()
  | lapsed ->
    lock lapseLock (fun () ->
      // Look again under the lock: another thread may have repaired it.
      match lapsedPoints () with
      | [] -> ()
      | still ->
        for point in still do
          match entryPoints.Value |> List.tryFind (fun (p, _, _) -> p = point) with
          | Some(_, Some m, hookName) ->
            (try harmony.Value.Unpatch(m, HarmonyPatchType.Prefix, harmony.Value.Id) with _ -> ())
            (try patchPoint m hookName
             with ex -> Log.warn "[ValueReads] couldn't put the reflection watch back on %s: %s" (describePoint point) ex.Message)
          | _ -> ()
        let why =
          sprintf "the runtime recompiled %s and SageFs's watch on it stopped seeing reads for a while"
            (still |> List.map describePoint |> String.concat ", ")
        Log.warn "[ValueReads] %s" why
        lapseReasons.Value <- lapseReasons.Value @ [ why ]
        lapses.Value <- lapses.Value + 1)
    ignore lapsed

/// The reflection entry watch, put on once per process. Every entry point has
/// to take it, or none counts: a watch that sees `GetValue` but not
/// `CreateDelegate` would let a read through.
let private entryWatch : Lazy<ReflectionWatchStatus> =
  lazy
    (let found = entryPoints.Value
     match found |> List.filter (fun (_, m, _) -> m.IsNone) with
     | (missing, _, _) :: _ -> ReflectionWatchStatus.NotWatching(sprintf "this runtime has no %s SageFs can find" (describePoint missing))
     | [] ->
       match canaryGetter, canaryField with
       | None, _
       | _, None -> ReflectionWatchStatus.NotWatching "SageFs couldn't find its own canary to check the watch with"
       | Some _, Some _ ->
         try
           for (_, m, hookName) in found do
             match m with
             | Some m -> patchPoint m hookName
             | None -> ()
           ReflectionWatchStatus.Watching
         with ex ->
           // Half on is worse than off: take whatever went on back off.
           for (_, m, _) in found do
             match m with
             | Some m -> (try harmony.Value.Unpatch(m, HarmonyPatchType.Prefix, harmony.Value.Id) with _ -> ())
             | None -> ()
           ReflectionWatchStatus.NotWatching(sprintf "Harmony couldn't patch the reflection entry points (%s)" ex.Message))

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

/// The tracker's clock for the hot-loop rate: TimeSpan ticks from a
/// monotonic source. Tests pass their own.
module ReflectionClock =
  let system () : int64 =
    let now = Stopwatch.GetTimestamp()
    int64 (float now * (float TimeSpan.TicksPerSecond / float Stopwatch.Frequency))

/// Whether a getter carries its own watch right now.
[<RequireQualifiedAccess>]
type private GetterWatchState =
  | WatchOn
  | WatchOff

/// Whether a walk that finds a reflective caller rewires it (probe-callers).
[<RequireQualifiedAccess>]
type private Rewire =
  | RewireCaller
  | LeaveCaller

/// Whether mark-on-reflect has filed its mark for a value yet.
[<RequireQualifiedAccess>]
type private MarkState =
  | Unmarked
  | Marked

/// One tracked value's reflection state: its hot-loop rate and the question.
[<Sealed>]
type private ValueWatch(value: string, threshold: HotLoopThreshold) =
  let rate = ReadRate threshold
  member _.Value = value
  member _.Rate = rate
  member val Notice = NoticeState.Unasked with get, set
  member val Getter = GetterWatchState.WatchOff with get, set
  member val Mark = MarkState.Unmarked with get, set

/// One agent's view of the app's value reads.
[<Sealed>]
type Tracker(settings: ReflectionReadSettings, clock: unit -> int64) =
  let gate = obj ()
  let mutable ledger = Ledger.empty
  [<VolatileField>]
  let mutable mode = settings.Mode
  /// A copy of the ledger's window the hooks can read without the lock.
  [<VolatileField>]
  let mutable window = StartupWindow.Open
  let watches = Dictionary<string, ValueWatch>()
  /// Values asked about, in the order they were asked.
  let asked = List<string>()
  /// Assemblies whose code SageFs reads: a reflective caller anywhere else
  /// is named, never rewired.
  let trackedAssemblies = HashSet<Assembly>()
  let installed = lazy entryWatch.Value
  /// Lapses before this tracker started don't count against it.
  let lapsesAtStart = lapses.Value
  /// Whether the entry watch is on, and whether it has been on without a
  /// gap since this tracker started.
  let watchStatus () : ReflectionWatchStatus =
    match installed.Value with
    | ReflectionWatchStatus.NotWatching why -> ReflectionWatchStatus.NotWatching why
    | ReflectionWatchStatus.Lapsed why -> ReflectionWatchStatus.Lapsed why
    | ReflectionWatchStatus.Watching ->
      match lapses.Value > lapsesAtStart with
      | false -> ReflectionWatchStatus.Watching
      | true -> ReflectionWatchStatus.Lapsed(lapseReasons.Value |> List.skip (min lapsesAtStart lapseReasons.Value.Length) |> String.concat "; ")
  /// Prove the entry watch is still firing (see ReflectionCanary). Called
  /// wherever its evidence is about to be used.
  let checkpoint () =
    match installed.Value with
    | ReflectionWatchStatus.Watching -> checkEntryWatch ()
    | ReflectionWatchStatus.NotWatching _
    | ReflectionWatchStatus.Lapsed _ -> ()
  let mutable walks = 0L
  let mutable siteHits = 0L
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

  let isTracked (m: MethodBase) =
    match isNull m.DeclaringType with
    | true -> false
    | false -> lock gate (fun () -> trackedAssemblies.Contains m.DeclaringType.Assembly)

  /// Rewire a reflective caller's reflection calls, when it can be.
  let rewire (caller: MethodBase) (known: CallSite list) =
    match unprobeable caller with
    | Error _ -> ()
    | Ok() ->
      let fresh =
        lock sitesLock (fun () ->
          match rewiredSites.ContainsKey caller with
          | true -> []
          | false ->
            let ids = rewirableSiteIds caller known
            match ids with
            | [] -> ()
            | _ -> rewiredSites.[caller] <- ids
            ids)
      match fresh with
      | [] -> ()
      | _ ->
        match instrument caller CallerProbe with
        | Ok() -> ()
        | Error why -> Log.info "[ValueReads] %s reads a value through reflection and couldn't be rewired (%s); its reads will walk the stack" (displayName caller) why

  /// Who made a reflective read, from a walk. `rewireIt` rewires the caller
  /// (probe-callers) so its next reads don't have to walk.
  let attribute (walked: Walked) (rewireIt: Rewire) : ReflectiveCaller * string =
    match walked with
    | Walked.Opaque why -> ReflectiveCaller.Unattributed why, "generated code"
    | Walked.Direct caller ->
      ReflectiveCaller.Unattributed(sprintf "%s read it, and SageFs has no record of that code" (displayName caller)), displayName caller
    | Walked.ThroughReflection(caller, offset) ->
      let name = displayName caller
      match isTracked caller with
      | false ->
        let where = try caller.DeclaringType.Assembly.GetName().Name with _ -> "another assembly"
        ReflectiveCaller.Unattributed(sprintf "%s read it through reflection, and it's in %s, which SageFs doesn't read" name where), name
      | true ->
        let known = reflectionSitesOf caller
        match rewireIt with
        | Rewire.RewireCaller -> rewire caller known.Sites
        | Rewire.LeaveCaller -> ()
        // The runtime reports the offset of the statement the call is in (the
        // last point the stack was empty), not the call itself. So the read
        // came through one of the reflection calls at or after it, and the
        // worst of them stands for all: a copy if any of them keeps one.
        // Through a delegate or `calli` it could have been none of them.
        let candidates = known.Sites |> List.filter (fun s -> offset < 0 || s.Offset >= offset)
        match known.Indirect, candidates with
        | IndirectCalls.CallsIndirectly, _ ->
          ReflectiveCaller.Unattributed(sprintf "%s read it through reflection, and it also calls through delegates, so SageFs can't tell which call it was" name), name
        | IndirectCalls.NoIndirectCalls, [] ->
          ReflectiveCaller.Unattributed(sprintf "%s read it through reflection at a call SageFs couldn't pin down" name), name
        | IndirectCalls.NoIndirectCalls, first :: _ ->
          let worst =
            candidates
            |> List.tryFind (fun s ->
              match s.Fate with
              | ReadFate.Escaped _ -> true
              | ReadFate.Discarded -> false)
            |> Option.defaultValue first
          worst.Caller, name

  let walkToCaller () =
    Threading.Interlocked.Increment &walks |> ignore
    try walk (fun _ -> false)
    with ex -> Walked.Opaque(sprintf "SageFs couldn't walk the stack (%s)" ex.Message)

  /// Count a reflective read toward the hot loop, and ask once when it's hot.
  let countRead (w: ValueWatch) (caller: unit -> string) =
    match w.Notice with
    | NoticeState.Unasked ->
      match lock w (fun () -> w.Rate.Observe(clock ())) with
      | RateReading.Quiet -> ()
      | RateReading.HotLoop perSecond ->
        let notice = { Value = w.Value; Caller = caller (); ReadsPerSecond = perSecond; Mode = mode }
        let raised =
          lock gate (fun () ->
            let next, raised = ReflectionNotices.step w.Notice (NoticeEvent.HotLoopSeen notice)
            w.Notice <- next
            match raised with
            | [] -> ()
            | _ -> asked.Add w.Value
            raised)
        for n in raised do
          Log.info "[ValueReads] %s" (ReflectionNotices.describe n)
    | NoticeState.Asked _
    | NoticeState.Chosen _ -> ()

  /// The reflection entry watch saw one of `w`'s getters or its field.
  let onReflected (w: ValueWatch) (entry: ReflectionEntry) (site: int) =
    let fileRead caller = file (LedgerEvent.ReflectiveRead(w.Value, caller))
    match entry, w.Getter with
    | ReflectionEntry.DelegateMade, _ ->
      match w.Mark with
      | MarkState.Marked -> ()
      | MarkState.Unmarked ->
        w.Mark <- MarkState.Marked
        fileRead (ReflectiveCaller.Unattributed "its getter was turned into a delegate through reflection, and a delegate can run anywhere")
    // The getter's own watch is on and sees this read, caller and all.
    | ReflectionEntry.Invoked, GetterWatchState.WatchOn -> ()
    | ReflectionEntry.Invoked, GetterWatchState.WatchOff
    | ReflectionEntry.FieldRead, _ ->
      match mode with
      | ReflectionReadMode.MarkOnReflect ->
        match w.Mark with
        | MarkState.Marked -> ()
        | MarkState.Unmarked ->
          w.Mark <- MarkState.Marked
          fileRead (ReflectiveCaller.Unattributed "it was read through reflection, and this session marks those reads without looking for the caller (mark-on-reflect)")
        countRead w (fun () -> snd (attribute (walkToCaller ()) Rewire.LeaveCaller))
      | ReflectionReadMode.ExactEveryRead ->
        let caller, name = attribute (walkToCaller ()) Rewire.LeaveCaller
        fileRead caller
        countRead w (fun () -> name)
      | ReflectionReadMode.ProbeCallers ->
        let current = sites.Value
        match site > 0 && site < current.Length with
        | true ->
          let known = current.[site]
          Threading.Interlocked.Increment &siteHits |> ignore
          match known.FirstReadOf w.Value with
          | true -> fileRead known.Caller
          | false -> ()
          countRead w (fun () -> known.Reader)
        | false ->
          let caller, name = attribute (walkToCaller ()) Rewire.RewireCaller
          fileRead caller
          countRead w (fun () -> name)

  /// The getter watch after startup (exact-every-read): every read walks.
  let onExactRead (value: string) (walked: Walked) =
    match walked with
    | Walked.Direct caller when knownReader caller value -> file (LedgerEvent.GetterRead(value, Caller.Known(readerIdOf caller)))
    | Walked.Direct caller -> file (LedgerEvent.GetterRead(value, Caller.Unknown(displayName caller)))
    | Walked.ThroughReflection _
    | Walked.Opaque _ ->
      let caller, name = attribute walked Rewire.LeaveCaller
      file (LedgerEvent.ReflectiveRead(value, caller))
      match lock gate (fun () -> watches.TryGetValue value) with
      | true, w -> countRead w (fun () -> name)
      | false, _ -> ()

  let getterWatcher (value: string) : GetterWatcher =
    { Window = fun () -> window
      AtStartup = onGetterRead value
      AfterStartup = onExactRead value }

  let watchOf (value: string) : ValueWatch =
    lock gate (fun () ->
      match watches.TryGetValue value with
      | true, w -> w
      | false, _ ->
        let w = ValueWatch(value, settings.HotLoop)
        watches.[value] <- w
        w)

  let sinkOf (w: ValueWatch) =
    { new IReflectionSink with
        member _.Reflected(entry, site) = onReflected w entry site }

  /// Put the getter watch on `getter`, and say so on its value.
  let watchGetter (getter: MethodBase) (value: string) : Result<unit, string> =
    match instrument getter (GetterWatch(getterWatcher value)) with
    | Ok() ->
      lock gate (fun () -> watched.Add getter |> ignore)
      (watchOf value).Getter <- GetterWatchState.WatchOn
      Ok()
    | Error why -> Error why

  /// Take every getter watch off.
  let unwatchGetters () =
    let getterList = lock gate (fun () -> List.ofSeq watched)
    for g in getterList do
      release g
    lock gate (fun () ->
      watched.Clear()
      for w in watches.Values do
        w.Getter <- GetterWatchState.WatchOff)

  /// Getter watches stay on past startup in exact-every-read, and whenever the
  /// reflection entry watch couldn't go on (it's the only other thing that
  /// sees a reflective read).
  let gettersStayWatched () : GetterWatchState =
    match mode, watchStatus () with
    | ReflectionReadMode.ExactEveryRead, _
    | _, ReflectionWatchStatus.NotWatching _
    | _, ReflectionWatchStatus.Lapsed _ -> GetterWatchState.WatchOn
    | ReflectionReadMode.MarkOnReflect, ReflectionWatchStatus.Watching
    | ReflectionReadMode.ProbeCallers, ReflectionWatchStatus.Watching -> GetterWatchState.WatchOff

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
        // The reflection entry watch finds the value by its getter and its field.
        let sink = sinkOf (watchOf value)
        addSink getter sink
        match backingOf getter with
        | Backing.Field field -> addSink field sink
        | Backing.NoField -> ()

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
      window <- StartupWindow.Closed
      match gettersStayWatched () with
      | GetterWatchState.WatchOn -> ()
      | GetterWatchState.WatchOff -> unwatchGetters ()

  /// Start tracking the project's compiled assemblies. Called when the agent
  /// starts, before any of the user's code runs.
  member _.Track(assemblies: Assembly list) =
    let debugBuilt, optimizedBuilt = assemblies |> List.partition (optimized >> not)
    lock gate (fun () -> for asm in debugBuilt do trackedAssemblies.Add asm |> ignore)
    // The reflection entry watch goes on with everything else, before the
    // user's code runs. If it can't, the getters keep their watch for good.
    checkpoint ()
    match watchStatus () with
    | ReflectionWatchStatus.Watching -> ()
    | ReflectionWatchStatus.Lapsed why
    | ReflectionWatchStatus.NotWatching why ->
      Log.warn "[ValueReads] reflection reads after startup will be watched at each getter instead, which is slower: %s" why
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
      match watchGetter getter value with
      | Ok() -> ()
      | Error why ->
        // Without the watch, a read through reflection during startup would go
        // unseen, so the value can't be vouched for.
        file (LedgerEvent.ValueUntracked(value, sprintf "SageFs couldn't watch its getter (%s)" why))

  /// Scan an assembly an eval just produced (FSI's), before hot reload detours
  /// anything in it.
  member _.ScanEval(asm: Assembly) =
    lock gate (fun () -> trackedAssemblies.Add asm |> ignore)
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
  member this.EvalFinished(isFileSave: bool) =
    match isFileSave || Threading.Volatile.Read &windowReads > 0 with
    | true -> closeWindow ()
    | false -> ()
    this.Checkpoint()

  /// Everything known about each value's reads. Asking closes the window: a
  /// save is past startup by definition.
  member this.Evidence(values: string list) : ValueEvidence list =
    closeWindow ()
    this.Checkpoint()
    let evidence = lock gate (fun () -> values |> List.map (Ledger.evidence ledger))
    match watchStatus () with
    | ReflectionWatchStatus.Lapsed why ->
      // A reflective read in the gap can't be ruled out, for any value.
      evidence
      |> List.map (fun e ->
        match e with
        | ValueEvidence.Untracked _ -> e
        | ValueEvidence.Tracked(value, _) ->
          ValueEvidence.Untracked(value, sprintf "SageFs can't rule out a read through reflection: %s. Restart the app to get the watch back" why))
    | ReflectionWatchStatus.Watching
    | ReflectionWatchStatus.NotWatching _ -> evidence

  /// The ledger as it stands, for tests.
  member _.Ledger = lock gate (fun () -> ledger)

  /// The session's reflection read mode.
  member _.Mode = mode

  /// Switch the reflection read mode. It takes effect right away, with no
  /// restart: moving to exact-every-read puts the getter watches back on
  /// (except on a getter hot reload has already re-pointed, where the
  /// reflection entry watch keeps covering it), moving off it takes them off.
  /// Choosing a mode answers every question already asked.
  member this.SetMode(next: ReflectionReadMode) : ReflectionReadsReport =
    this.Checkpoint()
    lock gate (fun () ->
      mode <- next
      for w in watches.Values do
        w.Notice <- fst (ReflectionNotices.step w.Notice (NoticeEvent.ModeChosen next))
        // A value marked under mark-on-reflect stays marked: the read happened.
      )
    match window with
    | StartupWindow.Open -> ()
    | StartupWindow.Closed ->
      match gettersStayWatched () with
      | GetterWatchState.WatchOff -> unwatchGetters ()
      | GetterWatchState.WatchOn ->
        let unwatched =
          lock gate (fun () ->
            getters
            |> Seq.filter (fun (KeyValue(g, _)) -> not (watched.Contains g))
            |> Seq.map (fun (KeyValue(g, v)) -> g, v)
            |> List.ofSeq)
        for (getter, value) in unwatched do
          match watchGetter getter value with
          | Ok() -> ()
          | Error why -> Log.info "[ValueReads] %s keeps being watched at the reflection entry points instead of its getter: %s" value why
    this.ReflectionReads

  /// Where the session's reflection reads stand: the mode, whether the entry
  /// watch is on, and every value that's been asked about.
  member this.ReflectionReads : ReflectionReadsReport =
    this.Checkpoint()
    lock gate (fun () ->
      { Mode = mode
        Watch = watchStatus ()
        Notices = asked |> Seq.map (fun v -> { Value = v; State = watches.[v].Notice }) |> List.ofSeq
        Walks = Threading.Interlocked.Read &walks
        SiteHits = Threading.Interlocked.Read &siteHits })

  /// A tracker with the standard settings and the system clock.
  new() = Tracker(ReflectionReadSettings.standard, ReflectionClock.system)

  /// Prove the reflection entry watch is still on. If it lapsed, the getters
  /// take the watch back for the rest of the session.
  member _.Checkpoint() =
    checkpoint ()
    match watchStatus (), window with
    | ReflectionWatchStatus.Lapsed _, StartupWindow.Closed ->
      let unwatched =
        lock gate (fun () ->
          getters
          |> Seq.filter (fun (KeyValue(g, _)) -> not (watched.Contains g))
          |> Seq.map (fun (KeyValue(g, v)) -> g, v)
          |> List.ofSeq)
      for (getter, value) in unwatched do
        watchGetter getter value |> ignore
    | _ -> ()

/// What the environment of a process that runs the user's code needs so the
/// watch holds. Tiered compilation recompiles a hot CoreLib method, and on
/// this runtime a Harmony patch on it is lost when that happens (measured in
/// the REPL: 4,000 of 12,000 calls hit). With tiering off nothing is
/// recompiled, so the reflection entry watch can't lapse. The canary still
/// checks it (ReflectionCanary), and a lapse fails closed.
let processEnvironment (watch: ValueReadWatch) : (string * string) list =
  match watch with
  | ValueReadWatch.WatchValueReads -> [ "DOTNET_TieredCompilation", "0" ]
  | ValueReadWatch.IgnoreValueReads -> []
