namespace SageFs.Features

// BCL and FSharp.Core only (reflection), on purpose: compiled into SageFs.Core and embedded into the isolated FSI host
// (see the GuardReachability entries in SageFs.Core.fsproj, FsiHostBuild.fs and FsiHost.fsproj). How a method's IL is
// read is a function the caller hands in (the host uses Harmony's reader), so the walk itself is plain and testable.

open System
open System.Collections.Generic
open System.Reflection
open System.Runtime.CompilerServices

/// How far the walk goes from the clicked getter. Past either bound a method is not guarded and the row says so.
type WalkBudget =
  { /// How many calls deep from the getter.
    MaxDepth: int
    /// How many methods may get guards. Each one costs a patch, so this is also how long a click waits for them.
    MaxMethods: int }

module WalkBudget =
  /// Deep enough for a getter that calls a few layers of helpers, small enough that a click does not patch a library.
  let [<Literal>] ProductMaxDepth = 12
  let [<Literal>] ProductMaxMethods = 64

  let product : WalkBudget = { MaxDepth = ProductMaxDepth; MaxMethods = ProductMaxMethods }

/// The answer to "what does this method call".
[<RequireQualifiedAccess>]
type CalleeRead =
  | Callees of MethodBase list
  | Unreadable of detail: string

/// Whether the walk may guard a method.
[<RequireQualifiedAccess>]
type Eligibility =
  | Eligible
  | Ineligible of SkipReason

/// What the walk needs from the world. Everything here is a function so a test can hand in a small world of its own.
type WalkWorld =
  { /// The methods this one calls, loads a pointer to, or constructs, read from its IL.
    Callees: MethodBase -> CalleeRead
    /// The bodies a call to an abstract, interface or virtual method can land on, in code we own.
    Implementations: MethodBase -> MethodBase list
    /// What comes with making an object of this constructor's type: a closure's `Invoke`, a state machine's `MoveNext`.
    Created: MethodBase -> MethodBase list
    /// Whether we compiled or wove this assembly. Only code we own is guarded.
    Owns: Assembly -> bool
    /// What hot reload did to a method. A re-pointed method is never patched; the code its entry jumps to is.
    Detour: MethodBase -> Detour }

/// What the walk found.
type Walk =
  { /// Methods that get guards, the clicked getter first, then breadth first.
    Eligible: MethodBase list
    Skipped: (MethodBase * SkipReason) list }

module GuardReachability =

  let private bindingsAll =
    BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly

  /// A readable name for a method, for the row and for tests.
  let nameOf (m: MethodBase) : string =
    match m.DeclaringType with
    | null -> m.Name
    | declaring -> sprintf "%s.%s" declaring.FullName m.Name

  let private isDynamicMethod (m: MethodBase) : bool =
    match m.DeclaringType with
    | null -> true
    | _ -> m :? System.Reflection.Emit.DynamicMethod

  /// Whether this method is the type's implementation of `IAsyncStateMachine.MoveNext`, however the compiler named it
  /// (C# calls it `MoveNext`, an F# explicit interface implementation carries the interface's name).
  let private isAsyncStateMachineMove (m: MethodBase) : bool =
    match m.DeclaringType with
    | null -> false
    | declaring ->
      match typeof<IAsyncStateMachine>.IsAssignableFrom declaring && not declaring.IsInterface with
      | false -> false
      | true ->
        try
          let map = declaring.GetInterfaceMap typeof<IAsyncStateMachine>
          let moveNext = typeof<IAsyncStateMachine>.GetMethod "MoveNext"
          let index = Array.IndexOf(map.InterfaceMethods, moveNext)
          index >= 0 && map.TargetMethods.[index].MethodHandle.Value = m.MethodHandle.Value
        with _ -> m.Name = "MoveNext"

  let private hasBody (m: MethodBase) : bool =
    match m.IsAbstract with
    | true -> false
    | false ->
      try not (isNull (m.GetMethodBody()))
      with _ -> false

  /// May the walk guard this method? The order matters: what a person would call the real reason comes first, so a
  /// framework `MoveNext` is "not our code", not "an async machine".
  let eligibility (owns: Assembly -> bool) (m: MethodBase) : Eligibility =
    match isDynamicMethod m with
    | true -> Eligibility.Ineligible SkipReason.DynamicMethod
    | false ->
    let declaring = m.DeclaringType
    match owns declaring.Assembly with
    | false -> Eligibility.Ineligible (SkipReason.NotOurCode (declaring.Assembly.GetName().Name))
    | true ->
    match m.ContainsGenericParameters || m.IsGenericMethod || declaring.IsGenericType with
    | true -> Eligibility.Ineligible SkipReason.Generic
    | false ->
    match isAsyncStateMachineMove m with
    | true -> Eligibility.Ineligible SkipReason.AsyncStateMachine
    | false ->
    match hasBody m with
    | false -> Eligibility.Ineligible SkipReason.NoBody
    | true -> Eligibility.Eligible

  /// A method's identity for "have I seen it": the same method reached through a base type or a derived one is one method.
  let private identity (m: MethodBase) : nativeint =
    try m.MethodHandle.Value
    with _ -> 0n

  let private isVirtualCall (m: MethodBase) : bool =
    match m.DeclaringType with
    | null -> false
    | declaring -> m.IsAbstract || m.IsVirtual || declaring.IsInterface

  /// A type the compiler made to carry code: an F# closure (`clo@12`, `Foo@23-1`), a C# lambda or display class, an
  /// async or iterator machine. Making one of these is how its code runs, so the code comes with the `newobj`.
  let isCodeCarrier (t: Type) : bool =
    t.Name.Contains '@'
    || t.IsDefined(typeof<CompilerGeneratedAttribute>, false)
    || (not (isNull t.BaseType) && t.BaseType.FullName <> null && t.BaseType.FullName.StartsWith "Microsoft.FSharp.Core.FSharpFunc")

  /// What comes with making a code carrier: everything it declares that is not a constructor.
  let createdBy (m: MethodBase) : MethodBase list =
    match m.IsConstructor, m.DeclaringType with
    | true, declaring when not (isNull declaring) && isCodeCarrier declaring ->
      declaring.GetMethods bindingsAll |> Seq.map (fun x -> x :> MethodBase) |> Seq.toList
    | _ -> []

  /// The bodies a virtual call can land on among the types of the given assemblies. Calls on the root of everything
  /// (`Object.ToString`) are not followed: every type has one, and none of them is what the getter means.
  let implementationsIn (assemblies: Assembly list) (callee: MethodBase) : MethodBase list =
    match callee.DeclaringType with
    | null -> []
    | declaring when declaring = typeof<obj> || declaring = typeof<ValueType> || declaring = typeof<Enum> -> []
    | declaring ->
      let types =
        assemblies
        |> List.collect (fun a -> try a.GetTypes() |> Array.toList with _ -> [])
        |> List.filter (fun t -> not t.IsAbstract && not t.IsInterface && not t.ContainsGenericParameters && declaring.IsAssignableFrom t)
      match callee with
      | :? MethodInfo as target ->
        match declaring.IsInterface with
        | true ->
          types
          |> List.choose (fun t ->
            try
              let map = t.GetInterfaceMap declaring
              Array.IndexOf(map.InterfaceMethods, target)
              |> function
                | -1 -> None
                | index -> Some (map.TargetMethods.[index] :> MethodBase)
            with _ -> None)
        | false ->
          let baseDefinition = target.GetBaseDefinition()
          types
          |> List.collect (fun t -> t.GetMethods bindingsAll |> Array.toList)
          |> List.filter (fun candidate -> candidate.IsVirtual && not candidate.IsAbstract && candidate.GetBaseDefinition() = baseDefinition)
          |> List.map (fun x -> x :> MethodBase)
      | _ -> []

  /// The reflection half of a world: ownership by assembly, closures by name, implementations by scanning. The IL
  /// reader is the caller's.
  let worldFor (owned: Assembly list) (callees: MethodBase -> CalleeRead) : WalkWorld =
    { Callees = callees
      Implementations = implementationsIn owned
      Created = createdBy
      Owns = fun assembly -> owned |> List.exists (fun o -> obj.ReferenceEquals(o, assembly))
      Detour = DetourLedger.resolve }

  /// Breadth first from the getter. A method is looked at once. What the getter calls in code we do not own is recorded
  /// and not followed; what is eligible is followed while the budget lasts.
  let walk (world: WalkWorld) (budget: WalkBudget) (root: MethodBase) : Walk =
    let seen = HashSet<nativeint>()
    let eligible = List<MethodBase>()
    let skipped = List<MethodBase * SkipReason>()
    let queue = Queue<MethodBase * int>()
    let enqueue (depth: int) (m: MethodBase) = queue.Enqueue((m, depth))
    // The same virtual callee is asked about from every caller, and each answer scans types: ask once.
    let implementations = Dictionary<nativeint, MethodBase list>()
    let implementationsOf (callee: MethodBase) : MethodBase list =
      match identity callee with
      | 0n -> world.Implementations callee
      | key ->
        match implementations.TryGetValue key with
        | true, found -> found
        | false, _ ->
          let found = world.Implementations callee
          implementations.[key] <- found
          found
    let firstTime (m: MethodBase) =
      match identity m with
      | 0n -> true
      | key -> seen.Add key
    enqueue 0 root
    while queue.Count > 0 do
      let m, depth = queue.Dequeue()
      match firstTime m with
      | false -> ()
      | true ->
      match world.Detour m with
      | Detour.DetouredTo body -> enqueue depth body
      | Detour.DetouredElsewhere -> skipped.Add((m, SkipReason.DetouredByHotReload))
      | Detour.NotDetoured ->
        match eligibility world.Owns m with
        | Eligibility.Ineligible reason -> skipped.Add((m, reason))
        | Eligibility.Eligible ->
          match eligible.Count >= budget.MaxMethods || depth > budget.MaxDepth with
          | true -> skipped.Add((m, SkipReason.BudgetReached))
          | false ->
            match world.Callees m with
            | CalleeRead.Unreadable detail ->
              // What cannot be read cannot be patched, and what it calls is unknown.
              skipped.Add((m, SkipReason.UnreadableBody detail))
            | CalleeRead.Callees callees ->
              eligible.Add m
              for callee in callees do
                enqueue (depth + 1) callee
                for created in world.Created callee do enqueue (depth + 1) created
                match isVirtualCall callee with
                | true -> for implementation in implementationsOf callee do enqueue (depth + 1) implementation
                | false -> ()
    { Eligible = List.ofSeq eligible; Skipped = List.ofSeq skipped }
