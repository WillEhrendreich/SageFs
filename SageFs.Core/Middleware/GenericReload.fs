/// What it takes to hot reload a generic function, in the runtime's own terms.
///
/// The runtime compiles a generic method once for every value-type instantiation that runs, and ONE body
/// for all the reference-type instantiations (the canonical `__Canon` body). A caller reaches that shared
/// body directly, passing the exact instantiation as a hidden first argument, and a delegate, a closure and
/// a reflection call all end up in the same body. So there are two kinds of body to patch:
///
///   * a value-type instantiation has its own code. A detour of that closed method reaches every caller of
///     it, and nothing else.
///   * the shared body is ONE piece of code. A detour of it reaches every reference-type instantiation, the
///     ones that ran and the ones that have not, but a detour that points at the new body's closed method
///     gives every one of them the type arguments of that one method (MonoMod's own comment on this: your
///     hook "will receive calls for all reference type-based implementations"). So the detour points at a
///     stub that is handed the hidden argument, finds the exact instantiation from it, and calls the new
///     body for that instantiation.
///
/// The bodies that have to be patched are the ones the program can reach. A generic method can only be
/// instantiated by a method reference in IL (call, callvirt, newobj, ldftn, ldvirtftn, ldtoken) or by
/// reflection, so `reach` reads the IL of the program, follows generic callers to the instantiations that
/// reach them, and says so when it cannot list them (reflection that makes generic methods or types).
///
/// BCL only, like `EntryProbes`: compiled into the isolated FSI host too.
module SageFs.Middleware.GenericReload

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit
open System.Runtime.CompilerServices
open System.Threading
open SageFs.Middleware.ValueReads
open SageFs.Middleware.EntryProbes

// ── why the instantiations cannot be listed ──────────────────────────────────

/// Why a generic function cannot be patched in every instantiation, with what to say about it.
[<RequireQualifiedAccess>]
type Unreachable =
  /// The program calls `MethodInfo.MakeGenericMethod`, so it can make an instantiation that no IL names.
  | MakesGenericMethods of caller: string
  /// The program calls `Type.MakeGenericType` and a generic type or method of it reaches the function, so
  /// the type arguments it runs with are not in the IL.
  | MakesGenericTypes of caller: string
  /// A method that could name the function could not be read (its IL did not decode).
  | CouldNotRead of methodName: string * detail: string
  /// The function is reached from more distinct instantiations than the scan follows.
  | TooManyInstantiations of limit: int
  /// A shape the detour does not carry: the way the shared body takes its hidden argument is not the way
  /// the stub takes it (a struct return buffer, a byref).
  | UnsupportedShape of detail: string

module Unreachable =
  let describe (why: Unreachable) : string =
    match why with
    | Unreachable.MakesGenericMethods caller ->
      sprintf "%s calls MakeGenericMethod, so the program can make an instantiation no code names" caller
    | Unreachable.MakesGenericTypes caller ->
      sprintf "%s calls MakeGenericType, and a generic type or method reaches the function, so the type arguments it runs with are not in the code" caller
    | Unreachable.CouldNotRead(methodName, detail) -> sprintf "%s could not be read (%s)" methodName detail
    | Unreachable.TooManyInstantiations limit -> sprintf "it is reached from more than %d instantiations" limit
    | Unreachable.UnsupportedShape detail -> detail

// ── a method's identity, and the shape two copies of it share ────────────────

/// A method definition, by where it lives. Two `MethodInfo`s of the same definition (the open one and the
/// ones over type arguments) share the module and the metadata token.
[<CustomEquality; NoComparison>]
type DefinitionKey =
  { Module: Module
    Token: int }
  override this.Equals(other: obj) =
    match other with
    | :? DefinitionKey as k -> obj.ReferenceEquals(k.Module, this.Module) && k.Token = this.Token
    | _ -> false
  override this.GetHashCode() = this.Token

let keyOf (m: MethodBase) : DefinitionKey = { Module = m.Module; Token = m.MetadataToken }

/// A type with its generic parameters named by position, so a type of one copy of a generic declaration and
/// the same type of another copy can be compared (the parameters of two definitions are never equal).
let rec typeShape (t: Type) : string =
  match t with
  | _ when t.IsGenericParameter ->
    sprintf "!%s%d" (match isNull t.DeclaringMethod with | true -> "" | false -> "!") t.GenericParameterPosition
  | _ when t.IsByRef -> typeShape (t.GetElementType()) + "&"
  | _ when t.IsArray -> sprintf "%s[%d]" (typeShape (t.GetElementType())) (t.GetArrayRank())
  | _ when t.IsGenericType ->
    sprintf "%s<%s>" (t.GetGenericTypeDefinition().FullName) (t.GetGenericArguments() |> Array.map typeShape |> String.concat ",")
  | _ -> t.FullName

/// How many type parameters the type a method is declared in has of its own.
let private classArity (m: MethodBase) : int =
  match isNull m.DeclaringType || not m.DeclaringType.IsGenericTypeDefinition with
  | true -> 0
  | false -> m.DeclaringType.GetGenericArguments().Length

/// The signature of a generic method (or a method of a generic type) with its generic parameters named by
/// position, so the copy the app holds and the copy a save compiled can be told apart from a different
/// method of the same name.
let shapeOf (m: MethodInfo) : string =
  sprintf "%b|%d|%d|%s|%s"
    m.IsStatic
    (classArity m)
    (m.GetGenericArguments().Length)
    (m.GetParameters() |> Array.map (fun p -> typeShape p.ParameterType) |> String.concat ",")
    (typeShape m.ReturnType)

/// A generic method definition, or a method of a generic type definition.
let isGenericDefinition (m: MethodInfo) : bool = m.IsGenericMethodDefinition || classArity m > 0

/// Both are generic definitions of the same shape.
let sameShape (older: MethodInfo) (newer: MethodInfo) : bool =
  isGenericDefinition older && isGenericDefinition newer && shapeOf older = shapeOf newer

/// `definition` over `arguments`: the arguments of its type, then those of the method. The method of a closed
/// type is found by its handle, since a closed type's methods are not the definition's.
let closeOver (definition: MethodInfo) (arguments: Type[]) : MethodInfo =
  let arity = classArity definition
  let closedType =
    match arity with
    | 0 -> definition.DeclaringType
    | _ -> definition.DeclaringType.MakeGenericType(Array.sub arguments 0 arity)
  let onType =
    match arity with
    | 0 -> definition
    | _ -> MethodBase.GetMethodFromHandle(definition.MethodHandle, closedType.TypeHandle) :?> MethodInfo
  match definition.IsGenericMethodDefinition with
  | true -> onType.MakeGenericMethod(Array.sub arguments arity (arguments.Length - arity))
  | false -> onType

// ── which instantiations the program can reach ───────────────────────────────

/// How the definitions the scan was asked about are reached.
[<RequireQualifiedAccess>]
type ReachedThrough =
  /// Only by method references that name every type argument.
  | ClosedReferences
  /// Also by a generic method or type of the program that refers to one with type arguments of its own, so the
  /// arguments it runs with are the ones that generic is reached with.
  | GenericMethodsOrTypes

/// What the scan found for the definitions it was asked about.
type Reach =
  { /// Every closed instantiation some method reference in the program names, by definition.
    Instantiations: Map<int, Type[] list>
    Through: ReachedThrough }

/// The most method contexts one scan visits. Past this a program is one this scan does not know the end of.
[<Literal>]
let private scanLimit = 200000

let private memberFlags =
  BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly

let private typesOf (a: Assembly) : Type[] =
  try
    a.GetTypes()
  with
  | :? ReflectionTypeLoadException as e -> e.Types |> Array.filter (fun t -> not (isNull t))
  | _ -> [||]

let private membersOf (t: Type) : MethodBase[] =
  try
    Array.append
      (t.GetMethods memberFlags |> Array.map (fun m -> m :> MethodBase))
      (t.GetConstructors memberFlags |> Array.map (fun c -> c :> MethodBase))
  with _ -> [||]

let private sameArguments (a: Type[]) (b: Type[]) : bool =
  a.Length = b.Length && Array.forall2 (fun (x: Type) (y: Type) -> obj.ReferenceEquals(x, y)) a b

let private isCallLike (op: System.Reflection.Emit.OpCode) : bool =
  op = OpCodes.Call
  || op = OpCodes.Callvirt
  || op = OpCodes.Newobj
  || op = OpCodes.Ldftn
  || op = OpCodes.Ldvirtftn
  || op = OpCodes.Ldtoken
  || op = OpCodes.Jmp

/// The two reflection calls that make a generic method or type out of values. Looked up on the types that own
/// them, so a name that does not exist fails when this module loads and not by silently matching nothing.
let private makeGenericMethod : MethodInfo =
  typeof<MethodInfo>.GetMethod("MakeGenericMethod", [| typeof<Type[]> |])

let private makeGenericType : MethodInfo =
  typeof<Type>.GetMethod("MakeGenericType", [| typeof<Type[]> |])

/// Reads the program's IL for method references to `targets`, following the generic methods and types that
/// reach them with the type arguments they are reached with. `scanned` are the assemblies whose code can
/// name the targets: the ones that declare them, the ones that refer to those, and FSI's own.
let reach (scanned: Assembly list) (targets: MethodInfo list) : Result<Reach, Unreachable> =
  let wanted = targets |> List.map (fun t -> keyOf t, t) |> List.distinctBy fst
  let wantedIndex (m: MethodBase) : int option =
    let key = keyOf m
    wanted |> List.tryFindIndex (fun (k, _) -> k = key)
  let scannedAssemblies = HashSet<Assembly>(scanned)
  let found = Dictionary<int, List<Type[]>>()
  let visited = Dictionary<DefinitionKey, List<Type[] * Type[]>>()
  let queue = Queue<MethodBase>()
  let mutable carrier = false
  let mutable stopped : Unreachable option = None
  let mutable madeGenericMethod : string option = None
  let mutable madeGenericType : string option = None
  let mutable contexts = 0

  let stop (why: Unreachable) =
    match stopped with
    | None -> stopped <- Some why
    | Some _ -> ()

  let enqueue (m: MethodBase) =
    let key = keyOf m
    let typeArgs = match m.DeclaringType.IsGenericType with | true -> m.DeclaringType.GetGenericArguments() | false -> [||]
    let methodArgs = match m.IsGenericMethod with | true -> m.GetGenericArguments() | false -> [||]
    let seen =
      match visited.TryGetValue key with
      | true, l -> l
      | false, _ ->
        let l = List<Type[] * Type[]>()
        visited.[key] <- l
        l
    match seen.Exists(fun (t, mm) -> sameArguments t typeArgs && sameArguments mm methodArgs) with
    | true -> ()
    | false ->
      seen.Add((typeArgs, methodArgs))
      contexts <- contexts + 1
      match contexts > scanLimit with
      | true -> stop (Unreachable.TooManyInstantiations scanLimit)
      | false -> queue.Enqueue m

  let record (index: int) (args: Type[]) =
    match found.TryGetValue index with
    | true, l -> if not (l.Exists(fun a -> sameArguments a args)) then l.Add args
    | false, _ -> found.[index] <- List<Type[]>([ args ])

  /// A closed generic type was built or called into: every member of it can run with its type arguments,
  /// including the ones no IL names (a virtual or interface call reaches a member through the object).
  let enqueueType (t: Type) =
    for m in membersOf t do
      match wantedIndex m with
      | Some index when not m.IsGenericMethodDefinition -> record index (t.GetGenericArguments())
      | _ -> ()
      match m.IsGenericMethodDefinition || m.IsAbstract with
      | true -> ()
      | false -> enqueue m

  let nameOf (m: MethodBase) =
    sprintf "%s.%s" (match isNull m.DeclaringType with | true -> "?" | false -> m.DeclaringType.FullName) m.Name

  let visit (m: MethodBase) =
    let body = try m.GetMethodBody() with _ -> null
    match isNull body with
    | true -> ()
    | false ->
      let typeArgs = match m.DeclaringType.IsGenericType with | true -> m.DeclaringType.GetGenericArguments() | false -> [||]
      let methodArgs = match m.IsGenericMethod with | true -> m.GetGenericArguments() | false -> [||]
      let inGenericContext = typeArgs.Length > 0 || methodArgs.Length > 0
      match decode (body.GetILAsByteArray()) with
      | Error e -> stop (Unreachable.CouldNotRead(nameOf m, sprintf "%A" e))
      | Ok instrs ->
        for i in instrs do
          match i.Operand with
          | Operand.Token token when isCallLike i.Op ->
            // A token that names a type that no longer loads (a stale FSI type) is a reference that cannot
            // run, so it cannot be one the program reaches the function through.
            let resolved = try Some(m.Module.ResolveMember(token, typeArgs, methodArgs)) with _ -> None
            match resolved with
            | Some(:? MethodBase as callee) ->
              match callee with
              | :? MethodInfo as mi when keyOf mi = keyOf makeGenericMethod ->
                madeGenericMethod <- madeGenericMethod |> Option.orElse (Some(nameOf m))
              | :? MethodInfo as mi when keyOf mi = keyOf makeGenericType ->
                madeGenericType <- madeGenericType |> Option.orElse (Some(nameOf m))
              | _ -> ()
              // Which definition does it name, and over what?
              (match callee with
               | :? MethodInfo as mi ->
                 match wantedIndex mi with
                 | Some index ->
                   // The type's arguments, then the method's: what the definition is closed over.
                   let classArgs = match mi.DeclaringType.IsGenericType with | true -> mi.DeclaringType.GetGenericArguments() | false -> [||]
                   let methodArgs = match mi.IsGenericMethod with | true -> mi.GetGenericArguments() | false -> [||]
                   let args = Array.append classArgs methodArgs
                   match args.Length > 0, args |> Array.exists (fun t -> t.ContainsGenericParameters) with
                   | false, _ -> ()
                   | true, true -> carrier <- true
                   | true, false ->
                     record index args
                     if inGenericContext then carrier <- true
                 | None -> ()
               | _ -> ())
              // Follow it into code of the program.
              match scannedAssemblies.Contains callee.Module.Assembly, callee.ContainsGenericParameters with
              | true, false ->
                (match callee.IsAbstract with
                 | true -> ()
                 | false -> enqueue callee)
                (match callee.DeclaringType.IsGenericType with
                 | true -> enqueueType callee.DeclaringType
                 | false -> ())
              | _ -> ()
            | Some(:? Type as t) ->
              // A closed generic type named by a token (ldtoken, newobj of its constructor is a method).
              match t.IsGenericType && not t.ContainsGenericParameters && scannedAssemblies.Contains t.Assembly with
              | true -> enqueueType t
              | false -> ()
            | _ -> ()
          | _ -> ()

  // Every method of a type that has no type parameters of its own can run, so it is where the scan starts.
  for assembly in scanned do
    for t in typesOf assembly do
      match t.ContainsGenericParameters with
      | true -> ()
      | false ->
        for m in membersOf t do
          match m.IsGenericMethodDefinition || m.IsAbstract with
          | true -> ()
          | false -> enqueue m

  while queue.Count > 0 && stopped.IsNone do
    visit (queue.Dequeue())

  // A member of a generic type is reached by whatever builds the type, and MakeGenericType builds it with
  // any arguments, so the type is a carrier of its own members.
  let membersOfGenericTypes = wanted |> List.exists (fun (_, t) -> classArity t > 0)
  match stopped with
  | Some why -> Error why
  | None ->
    match madeGenericMethod, madeGenericType, carrier || membersOfGenericTypes with
    | Some caller, _, _ -> Error(Unreachable.MakesGenericMethods caller)
    | None, Some caller, true -> Error(Unreachable.MakesGenericTypes caller)
    | None, _, _ ->
      Ok
        { Instantiations = found |> Seq.map (fun (KeyValue(k, v)) -> k, List.ofSeq v) |> Map.ofSeq
          Through =
            match carrier with
            | true -> ReachedThrough.GenericMethodsOrTypes
            | false -> ReachedThrough.ClosedReferences }

// ── value-type bodies and the shared body ────────────────────────────────────

/// The type arguments that make the runtime share one body: a reference type, or a value type whose own
/// type arguments include one.
let rec private needsCanon (t: Type) : bool =
  match t.IsValueType with
  | false -> true
  | true -> t.IsGenericType && t.GetGenericArguments() |> Array.exists needsCanon

/// The instantiation this one shares its compiled body with, spelled out: every reference type is `*`.
let rec private canonName (t: Type) : string =
  match t.IsValueType, t.IsGenericType with
  | false, _ -> "*"
  | true, true -> sprintf "%s<%s>" (t.GetGenericTypeDefinition().FullName) (t.GetGenericArguments() |> Array.map canonName |> String.concat ",")
  | true, false -> t.AssemblyQualifiedName

/// Which instantiations compile to one body (the canonical form) and which have their own.
[<RequireQualifiedAccess>]
type Body =
  /// An instantiation whose type arguments are all value types: its own code.
  | Own of arguments: Type[]
  /// Every instantiation that has the same value-type arguments in the same places: one compiled body,
  /// represented here by one of them.
  | Shared of representative: Type[]

/// The bodies behind the instantiations the program reaches, one per distinct compiled body.
let bodiesOf (instantiations: Type[] list) : Body list =
  let shared, own = instantiations |> List.partition (fun args -> args |> Array.exists needsCanon)
  let sharedBodies =
    shared
    |> List.groupBy (fun args -> args |> Array.map canonName |> String.concat ";")
    |> List.map (fun (_, group) -> Body.Shared(List.head group))
  (own |> List.map Body.Own) @ sharedBodies

/// `representative` with every reference type argument replaced by `object`. If this runs the same compiled
/// body as `representative`, the body is a shared one.
let canonicalInstance (representative: Type[]) : Type[] =
  let rec canon (t: Type) =
    match t.IsValueType, t.IsGenericType with
    | false, _ -> typeof<obj>
    | true, true -> t.GetGenericTypeDefinition().MakeGenericType(t.GetGenericArguments() |> Array.map canon)
    | true, false -> t
  representative |> Array.map canon

// ── the stub a shared body is detoured to ────────────────────────────────────

/// How a call into a shared body says which instantiation it is for.
[<RequireQualifiedAccess>]
type Context =
  /// The method has type parameters of its own: the hidden argument is the exact method.
  | ExactMethod
  /// An instance member of a generic class: the type arguments are the object's, and there is no hidden argument.
  | TheObject
  /// A static member of a generic class: the hidden argument is the exact class.
  | ExactClass
  /// A generic instance method of a generic class: the hidden argument is the exact method, which the runtime
  /// will only turn back into a method when it is given the exact class, and that is the object's.
  | ExactMethodOfTheObject
  /// A static generic method of a generic class: the hidden argument is the exact method, and nothing at the
  /// call says which class it belongs to.
  | NotCarried

/// How the shared body of `definition` is told which instantiation it runs for.
let contextOf (definition: MethodInfo) : Context =
  let inGenericClass = classArity definition > 0
  match definition.IsGenericMethodDefinition, inGenericClass, definition.IsStatic with
  | true, false, _ -> Context.ExactMethod
  | true, true, false -> Context.ExactMethodOfTheObject
  | true, true, true -> Context.NotCarried
  | false, _, false -> Context.TheObject
  | false, _, true -> Context.ExactClass

/// Where a stub finds the exact new instantiation. Static, because IL can only call a static from a dynamic
/// method.
[<Sealed>]
type SharedTargets private () =
  static let plans = ConcurrentDictionary<int64, MethodInfo * MethodInfo>()
  static let byHandle = ConcurrentDictionary<struct (int64 * nativeint), nativeint>()
  static let byType = ConcurrentDictionary<struct (int64 * Type), nativeint>()
  static let mutable lastPlan = 0L

  /// A plan: the copy the shared body belongs to and the new copy its calls are sent to. Returned to the stub's
  /// IL as a number.
  static member Register(older: MethodInfo, newer: MethodInfo) : int64 =
    let id = Interlocked.Increment(&lastPlan)
    plans.[id] <- (older, newer)
    id

  /// The new body's entry for an instantiation: the type's arguments, then the method's.
  static member private EntryFor(plan: int64, classArguments: Type[], methodArguments: Type[]) : nativeint =
    let _, newer = plans.[plan]
    let target = closeOver newer (Array.append classArguments methodArguments)
    RuntimeHelpers.PrepareMethod target.MethodHandle
    target.MethodHandle.GetFunctionPointer()

  /// The hidden argument of a call into a shared body is the exact method being called. Its type arguments
  /// are the ones to run the new body with, and the new body's entry for them is what the stub calls.
  static member ResolveMethod(plan: int64, instantiation: nativeint) : nativeint =
    byHandle.GetOrAdd(
      struct (plan, instantiation),
      fun (struct (plan, instantiation)) ->
        let exact = MethodBase.GetMethodFromHandle(RuntimeMethodHandle.FromIntPtr instantiation) :?> MethodInfo
        let classArguments = match exact.DeclaringType.IsGenericType with | true -> exact.DeclaringType.GetGenericArguments() | false -> [||]
        SharedTargets.EntryFor(plan, classArguments, exact.GetGenericArguments())
    )

  /// The hidden argument of a static member of a generic class is the exact class.
  static member ResolveClass(plan: int64, instantiation: nativeint) : nativeint =
    byHandle.GetOrAdd(
      struct (plan, instantiation),
      fun (struct (plan, instantiation)) ->
        let exact = Type.GetTypeFromHandle(RuntimeTypeHandle.FromIntPtr instantiation)
        SharedTargets.EntryFor(plan, exact.GetGenericArguments(), [||])
    )

  /// The class an object is an instance of that the member is declared in (the object's own, or one of its bases
  /// when the member is inherited).
  static member private ClassOf(plan: int64, receiver: obj) : Type =
    let older, _ = plans.[plan]
    let rec constructed (t: Type) : Type =
      match isNull t with
      | true -> failwithf "%s is not an instance of %s" (receiver.GetType().FullName) older.DeclaringType.FullName
      | false when t.IsGenericType && t.GetGenericTypeDefinition() = older.DeclaringType -> t
      | false -> constructed t.BaseType
    constructed (receiver.GetType())

  /// An instance member of a generic class runs against an object, and the object's class says what the type
  /// arguments are.
  static member ResolveObject(plan: int64, receiver: obj) : nativeint =
    let exact = SharedTargets.ClassOf(plan, receiver)
    byType.GetOrAdd(struct (plan, exact), fun (struct (plan, exact)) -> SharedTargets.EntryFor(plan, exact.GetGenericArguments(), [||]))

  /// A generic instance method of a generic class: the class from the object, the method from the hidden argument.
  static member ResolveMethodOfObject(plan: int64, receiver: obj, instantiation: nativeint) : nativeint =
    byHandle.GetOrAdd(
      struct (plan, instantiation),
      fun (struct (plan, instantiation)) ->
        let exact = SharedTargets.ClassOf(plan, receiver)
        let method' = MethodBase.GetMethodFromHandle(RuntimeMethodHandle.FromIntPtr instantiation, exact.TypeHandle) :?> MethodInfo
        SharedTargets.EntryFor(plan, exact.GetGenericArguments(), method'.GetGenericArguments())
    )

let private stubs = List<DynamicMethod>()

/// How a type crosses a call into the shared body: a reference is a reference whatever it is a reference to.
let private abiType (t: Type) : Result<Type, Unreachable> =
  match t with
  | _ when t = typeof<Void> -> Ok t
  | _ when t.IsByRef || t.IsPointer || t.IsByRefLike ->
    Error(Unreachable.UnsupportedShape(sprintf "a parameter of type %s is passed by address, and the stub cannot hand an address through" t.Name))
  | _ when t.IsValueType -> Ok t
  | _ -> Ok typeof<obj>

/// A struct that is not a primitive or an enum comes back through a buffer the caller passes, before the
/// hidden argument, so a stub that takes the hidden argument first would read the wrong register.
let private returnsThroughBuffer (t: Type) : bool =
  t.IsValueType && t <> typeof<Void> && not t.IsPrimitive && not t.IsEnum

/// The stub for the shared body of `older` over `representative`: it records an entry under `probe`, finds
/// the exact instantiation of `newer` from the hidden argument, and calls it. Its signature is the shared
/// body's, hidden argument included (after `this` for an instance method).
let sharedStub (probe: EntryProbe) (older: MethodInfo) (newer: MethodInfo) (representative: Type[]) : Result<MethodInfo, Unreachable> =
  try
    let exact = closeOver older (canonicalInstance representative)
    let declared = exact.GetParameters() |> Array.map (fun p -> p.ParameterType)
    let abis = declared |> Array.map abiType
    match abis |> Array.tryPick (function | Error e -> Some e | Ok _ -> None), returnsThroughBuffer exact.ReturnType with
    | Some e, _ -> Error e
    | None, true ->
      Error(Unreachable.UnsupportedShape(sprintf "%s returns a struct, which comes back through a buffer the stub cannot place" exact.Name))
    | None, false ->
      let visible = abis |> Array.map (function | Ok t -> t | Error _ -> typeof<obj>)
      let ret = match abiType exact.ReturnType with | Ok t -> t | Error _ -> typeof<obj>
      // The object an instance member is called on. For a generic class its type has open parameters, and a
      // reference is a reference whatever it refers to, so the stub takes it as an object.
      let self =
        match older.IsStatic, older.DeclaringType.IsGenericTypeDefinition with
        | true, _ -> [||]
        | false, true -> [| typeof<obj> |]
        | false, false -> [| older.DeclaringType |]
      let context = contextOf older
      match context with
      | Context.NotCarried ->
        Error(
          Unreachable.UnsupportedShape(
            sprintf "%s is a static generic method of a generic class, and nothing at the call says which class it belongs to" older.Name
          )
        )
      | _ ->
      // Where the call says which instantiation it is for, and what comes before the member's own arguments.
      let hidden : Type[] =
        match context with
        | Context.ExactMethod
        | Context.ExactClass
        | Context.ExactMethodOfTheObject -> [| typeof<nativeint> |]
        | Context.TheObject
        | Context.NotCarried -> [||]
      let parameters = Array.concat [ self; hidden; visible ]
      let stub =
        DynamicMethod(sprintf "sagefs-shared-generic:%s" probe.Declaration, ret, parameters, typeof<EntryHooks>.Module, true)
      let il = stub.GetILGenerator()
      let entry = il.DeclareLocal(typeof<nativeint>)
      let plan = SharedTargets.Register(older, newer)
      il.Emit(OpCodes.Ldc_I8, probe.Id)
      il.Emit(OpCodes.Call, typeof<EntryHooks>.GetMethod "Enter")
      il.Emit(OpCodes.Ldc_I8, plan)
      match context with
      | Context.ExactMethod ->
        il.Emit(OpCodes.Ldarg, int16 self.Length)
        il.Emit(OpCodes.Call, typeof<SharedTargets>.GetMethod(nameof SharedTargets.ResolveMethod))
      | Context.ExactClass ->
        il.Emit(OpCodes.Ldarg, int16 self.Length)
        il.Emit(OpCodes.Call, typeof<SharedTargets>.GetMethod(nameof SharedTargets.ResolveClass))
      | Context.TheObject ->
        il.Emit OpCodes.Ldarg_0
        il.Emit(OpCodes.Call, typeof<SharedTargets>.GetMethod(nameof SharedTargets.ResolveObject))
      | Context.ExactMethodOfTheObject ->
        il.Emit OpCodes.Ldarg_0
        il.Emit(OpCodes.Ldarg, 1s)
        il.Emit(OpCodes.Call, typeof<SharedTargets>.GetMethod(nameof SharedTargets.ResolveMethodOfObject))
      | Context.NotCarried -> ()
      il.Emit(OpCodes.Stloc, entry)
      for k in 0 .. self.Length - 1 do
        il.Emit(OpCodes.Ldarg, int16 k)
      for k in 0 .. visible.Length - 1 do
        il.Emit(OpCodes.Ldarg, int16 (self.Length + hidden.Length + k))
      il.Emit(OpCodes.Ldloc, entry)
      let convention = match older.IsStatic with | true -> CallingConventions.Standard | false -> CallingConventions.HasThis
      il.EmitCalli(OpCodes.Calli, convention, ret, visible, null)
      il.Emit OpCodes.Ret
      lock stubs (fun () -> stubs.Add stub)
      Ok(stub :> MethodInfo)
  with ex ->
    Error(Unreachable.UnsupportedShape(sprintf "the stub for %s could not be built (%s: %s)" probe.Declaration (ex.GetType().Name) ex.Message))
