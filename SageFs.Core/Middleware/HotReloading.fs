module SageFs.Middleware.HotReloading

open System
open System.IO
open System.Reflection

open SageFs.ProjectLoading
open SageFs.Utils
open SageFs.AppState
open SageFs.DevReload

// Assembly resolver to find dependencies in project output directories
let assemblySearchPaths = ResizeArray<string>()

let resolveAssembly (args: ResolveEventArgs) =
  let assemblyName = AssemblyName(args.Name)
  let dllName = assemblyName.Name + ".dll"

  // Search in all registered paths
  assemblySearchPaths
  |> Seq.tryPick (fun searchPath ->
    let fullPath = Path.Combine(searchPath, dllName)

    if File.Exists(fullPath) then
      try
        Some(Assembly.LoadFrom(fullPath))
      with _ ->
        None
    else
      None)
  |> Option.defaultValue null

// Register the assembly resolver once
let resolverRegistered = ref false

let setupAssemblyResolver () =
  if not !resolverRegistered then
    resolverRegistered := true
    AppDomain.CurrentDomain.add_AssemblyResolve (ResolveEventHandler(fun _ args -> resolveAssembly args))

let registerSearchPath (path: string) =
  let dir = Path.GetDirectoryName(path)

  if not (assemblySearchPaths.Contains(dir)) then
    assemblySearchPaths.Add(dir)

type Method = {
  MethodInfo: MethodInfo
  FullName: string
} with

  static member make modulePath (m: MethodInfo) = {
    MethodInfo = m
    FullName = m.Name :: modulePath |> Seq.rev |> String.concat "."
  }

type State = {
  Methods: Map<string, Method list>
  LastOpenModules: string list
  LastAssembly: Assembly Option
}

type Event =
  | NewReplAssemblies of Assembly array
  | ModuleOpened of string

let getAllMethods (asm: Assembly) =
  let rec getMethods currentPath (t: Type) =
    try
      let newPath =
        if t.Name.Contains "FSI_" then
          currentPath
        else
          t.Name :: currentPath

      let methods =
        t.GetMethods()
        |> Array.filter (fun m -> m.IsStatic && not <| m.IsGenericMethod)
        |> Array.map (Method.make newPath)
        |> Array.toList

      let nestedTypes =
        try
          t.GetNestedTypes() |> Array.toList
        with _ -> []

      let nestedMethods = nestedTypes |> List.collect (getMethods (t.Name :: currentPath))
      methods @ nestedMethods
    with ex ->
      // Skip this specific type but continue with others
      []

  // Try to get types, handling partial failures
  let types =
    try
      asm.GetTypes() |> Array.toList
    with
    | :? ReflectionTypeLoadException as ex ->
      // Some types failed to load, but we can use the ones that succeeded
      let loadedTypes = ex.Types |> Array.filter (fun t -> t <> null) |> Array.toList

      if loadedTypes.Length > 0 then
        printfn
          $"Warning: Assembly {asm.GetName().Name} has types with missing dependencies - loaded {loadedTypes.Length} types, skipped {ex.Types.Length - loadedTypes.Length}"
      else
        printfn $"Warning: Could not load any types from assembly {asm.GetName().Name} - all types have missing dependencies"

      loadedTypes
    | :? System.IO.FileNotFoundException as ex ->
      printfn $"Warning: Assembly {asm.GetName().Name} is missing a dependency: {ex.Message}"
      []
    | :? System.IO.FileLoadException as ex ->
      printfn $"Warning: Assembly {asm.GetName().Name} has a load error: {ex.Message}"
      []
    | :? System.BadImageFormatException as ex ->
      printfn $"Warning: Assembly {asm.GetName().Name} has a bad format: {ex.Message}"
      []
    | ex ->
      printfn $"Warning: Failed to get types from assembly {asm.GetName().Name}: {ex.Message}"
      []

  // Only process exported/public types
  let publicTypes = types |> List.filter (fun t -> t.IsPublic || t.IsNestedPublic)

  publicTypes |> List.collect (getMethods [])

let mkReloadingState (sln: SageFs.ProjectLoading.Solution) =
  // Setup assembly resolver once
  setupAssemblyResolver ()

  // Register all project output directories for dependency resolution
  sln.Projects |> List.iter (fun p -> registerSearchPath p.TargetPath)

  let assemblies =
    sln.Projects
    |> List.choose (fun p ->
      try
        let asm = Assembly.LoadFrom(p.TargetPath)
        Some asm
      with
      | :? System.IO.FileNotFoundException as ex ->
        printfn $"Warning: Could not load assembly {p.TargetPath}: {ex.Message}"
        None
      | :? System.IO.FileLoadException as ex ->
        printfn $"Warning: Could not load assembly {p.TargetPath}: {ex.Message}"
        None
      | :? System.BadImageFormatException as ex ->
        printfn $"Warning: Could not load assembly {p.TargetPath}: {ex.Message}"
        None)

  // getAllMethods now handles all reflection errors internally
  let allMethods = assemblies |> List.collect getAllMethods

  let methods =
    allMethods
    |> List.groupBy (fun m -> m.MethodInfo.Name)
    |> List.map (fun (methodName, methods) -> methodName, methods)
    |> Map.ofList

  {
    Methods = methods
    LastOpenModules = []
    LastAssembly = None
  }

let hotReloadingInitFunction sln =
  try
    "hotReload", box <| mkReloadingState sln
  with ex ->
    printfn $"Warning: HotReloading initialization failed: {ex.Message}"

    "hotReload",
    box
    <| {
         Methods = Map.empty
         LastOpenModules = []
         LastAssembly = None
       }

let getReloadingState (st: AppState) =
  st.Custom
  |> Map.tryFind "hotReload"
  |> Option.map (fun reloadStObj -> reloadStObj :?> State)
  |> Option.defaultWith (fun () -> mkReloadingState st.Solution)

open HarmonyLib

let detourMethod (method: MethodBase) (replacement: MethodBase) =
  typeof<Harmony>.Assembly
  |> _.GetTypes()
  |> Seq.find (fun t -> t.Name = "PatchTools")
  |> fun x -> x.GetDeclaredMethods()
  |> Seq.find (fun n -> n.Name = "DetourMethod")
  |> fun x -> x.Invoke(null, [| method; replacement |])
  |> ignore

open FuzzySharp

let handleNewAsmFromRepl (logger: ILogger) (asm: Assembly) (st: State) =
  match st.LastAssembly with
  | Some prev when prev = asm -> st, []
  | _ ->
    let replacementPairs =
      getAllMethods asm
      |> Seq.choose (fun newMethod ->
        Map.tryFind newMethod.MethodInfo.Name st.Methods
        |> Option.bind (
          Seq.filter (fun existingMethod ->
               let getParams m =
                 m.MethodInfo.GetParameters() |> Array.map _.ParameterType

               getParams existingMethod = getParams newMethod
               && existingMethod.MethodInfo.ReturnType = newMethod.MethodInfo.ReturnType
               && existingMethod.FullName.Contains newMethod.FullName)
            >> Seq.sortByDescending (fun existingMethod ->
               let moduleCandidate =
                 st.LastOpenModules
                 |> Seq.map (fun o -> Fuzz.Ratio(o + newMethod.FullName, existingMethod.FullName))
                 |> Seq.tryHead
                 |> Option.defaultValue 0

               let noModuleCandidate = Fuzz.Ratio(newMethod.FullName, existingMethod.FullName)
               max moduleCandidate noModuleCandidate)
            >> Seq.tryHead)
        |> Option.map (fun oldMethod -> oldMethod, newMethod))
      |> Seq.toList

    for methodToReplace, newMethod in replacementPairs do
      logger.LogDebug <| "Updating method " + methodToReplace.FullName
      detourMethod methodToReplace.MethodInfo newMethod.MethodInfo

    // Merge new assembly's methods into Methods so future evals can patch functions
    // defined in FSI (not in project DLLs). Without this, only project-DLL methods
    // are ever patchable; FSI-first-defined functions are invisible to Harmony.
    let newMethodsByName =
      getAllMethods asm
      |> List.groupBy (fun m -> m.MethodInfo.Name)
      |> List.map (fun (name, methods) -> name, methods)
      |> Map.ofList

    let mergedMethods =
      newMethodsByName |> Map.fold (fun acc k v -> Map.add k v acc) st.Methods

    { st with LastAssembly = Some asm; Methods = mergedMethods },
    List.map (fst >> _.FullName) replacementPairs

let getOpenModules (replCode: string) st =
  let modules =
    replCode.Split([| " "; "\n" |], System.StringSplitOptions.None)
    |> Seq.filter ((<>) "")
    |> Seq.chunkBySize 2
    |> Seq.filter (fun arr -> arr.Length >= 2)
    |> Seq.filter (Array.tryHead >> Option.map ((=) "open") >> Option.defaultValue false)
    |> Seq.map (fun arr -> arr[1])
    |> Seq.toList

  {
    st with
        LastOpenModules = (modules @ st.LastOpenModules) |> List.distinct
  }

/// Detect top-level function bindings (not value bindings).
/// Function bindings have parameters between the name and '=':
///   let f () = ...      → function (unit param)
///   let f x y = ...     → function (named params)
///   let f (x: int) = .. → function (typed params)
///   let x = 42          → value (no params)
///   let h : Type = ...  → value (type annotation, no params)
let private isTopLevelFunctionBinding (line: string) =
  let trimmed = line.TrimStart()
  if not (trimmed.StartsWith("let ")) || trimmed.StartsWith("let!") || line <> line.TrimStart() then
    false
  else
    let mutable s = trimmed.Substring(4).TrimStart()
    for m in ["private "; "internal "; "public "; "inline "; "rec "; "mutable "] do
      if s.StartsWith(m) then s <- s.Substring(m.Length).TrimStart()
    match s.IndexOf('=') with
    | -1 -> false
    | eqIdx ->
      let beforeEq = s.Substring(0, eqIdx).Trim()
      beforeEq.Contains("(") || (beforeEq.Contains(" ") && not (beforeEq.Contains(":")))

/// Detect static member method definitions (not properties).
/// The F# compiler inlines simple static member bodies at the IL level,
/// eliminating the call instruction entirely and making Harmony detours invisible.
let private isStaticMemberFunction (line: string) =
  let trimmed = line.TrimStart()
  trimmed.StartsWith("static member ") &&
    let afterKw = trimmed.Substring("static member ".Length).TrimStart()
    match afterKw.IndexOf('('), afterKw.IndexOf('=') with
    | parenIdx, eqIdx when parenIdx >= 0 && (eqIdx < 0 || parenIdx < eqIdx) -> true
    | _, eqIdx when eqIdx > 0 ->
      let beforeEq = afterKw.Substring(0, eqIdx).Trim()
      beforeEq.Contains(" ") && not (beforeEq.Contains(":"))
    | _ -> false

/// Inject [<MethodImpl(MethodImplOptions.NoInlining)>] on top-level function bindings
/// and static member methods so Harmony detours work reliably.
/// Without this, the F# compiler inlines simple static member bodies at the IL level,
/// and the JIT may inline short let-binding functions — both make Harmony's
/// entry-point detour invisible to callers.
let injectNoInlining (code: string) =
  let lines = code.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
  let needsInjection line = isTopLevelFunctionBinding line || isStaticMemberFunction line
  let hasFunction = lines |> Array.exists needsInjection
  if not hasFunction then code
  else
    let sb = System.Text.StringBuilder()
    sb.Append("open System.Runtime.CompilerServices\n") |> ignore
    for line in lines do
      if needsInjection line then
        let indent = line.Length - line.TrimStart().Length
        let prefix = System.String(' ', indent)
        sb.Append(prefix + "[<MethodImpl(MethodImplOptions.NoInlining)>]\n") |> ignore
      sb.Append(line + "\n") |> ignore
    sb.ToString()

let hotReloadingMiddleware next (request, st: AppState) =
  let hotReloadFlagEnabled =
    match st.Session.TryFindBoundValue "_SageFsHotReload" with
    | Some fsiBoundValue when fsiBoundValue.Value.ReflectionValue = true -> true
    | _ -> false

  let shouldTriggerReload (m: Map<string, obj>) =
    match hotReloadFlagEnabled, Map.tryFind "hotReload" m with
    | _, Some v when v = true -> true
    | true, None -> true
    | _ -> false

  // Inject NoInlining attributes on ALL evals so that functions defined in
  // earlier evals can be reliably patched by Harmony in future hot-reload evals.
  // Without this, (a) the F# compiler inlines simple static member bodies at the
  // IL level, and (b) the JIT may inline short let-binding functions — both make
  // Harmony's entry-point detour invisible to callers.
  let request = { request with Code = injectNoInlining request.Code }

  let response, st = next (request, st)

  // Always accumulate method registrations so future hot-reload evals can find them.
  // Only trigger browser reload when the hotReload flag is explicitly set.
  match response.EvaluationResult with
  | Error _ -> response, st
  | Ok _ ->
    let asm = st.Session.DynamicAssemblies |> Array.last

    let reloadingSt, updatedMethods =
      getReloadingState st
      |> getOpenModules response.EvaluatedCode
      |> handleNewAsmFromRepl st.Logger asm

    if shouldTriggerReload request.Args && not (List.isEmpty updatedMethods) then
      triggerReload()

    let metadata =
      if shouldTriggerReload request.Args then
        response.Metadata.Add("reloadedMethods", updatedMethods)
      else
        response.Metadata

    { response with Metadata = metadata },
    { st with Custom = st.Custom.Add("hotReload", reloadingSt) }
