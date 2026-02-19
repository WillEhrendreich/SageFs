module SageFs.Middleware.HotReloading

open System
open System.IO
open System.Reflection

open SageFs.ProjectLoading
open SageFs.Utils
open SageFs.AppState

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

    { st with LastAssembly = Some asm }, List.map (fst >> _.FullName) replacementPairs

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

let hotReloadingMiddleware next (request, st: AppState) =
  let hotReloadFlagEnabled =
    match st.Session.TryFindBoundValue "_SageFsHotReload" with
    | Some fsiBoundValue when fsiBoundValue.Value.ReflectionValue = true -> true
    | _ -> false

  let shouldRunHotReload (m: Map<string, obj>) =
    match hotReloadFlagEnabled, Map.tryFind "hotReload" m with
    | _, Some v when v = true -> true
    | true, None -> true
    | _ -> false

  match request with
  | { Args = m } when shouldRunHotReload m ->
    let response, st = next (request, st)

    match response.EvaluationResult with
    | Error _ -> response, st
    | Ok _ ->
      let asm = st.Session.DynamicAssemblies |> Array.last

      let reloadingSt, updatedMethods =
        getReloadingState st
        |> getOpenModules response.EvaluatedCode
        |> handleNewAsmFromRepl st.Logger asm

      {
        response with
            Metadata = response.Metadata.Add("reloadedMethods", updatedMethods)
      },
      {
        st with
            Custom = st.Custom.Add("hotReload", reloadingSt)
      }
  | _ -> next (request, st)
