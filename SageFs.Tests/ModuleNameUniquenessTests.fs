/// A module's fully qualified name belongs to one assembly. `module SageFs.SessionLifecycle` was declared in SageFs.Core
/// (worker-exit decisions) and again in SageFs (session-list events), so which one `open SageFs` reached depended on
/// the order the assemblies were referenced.
module SageFs.Tests.ModuleNameUniquenessTests

open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection

let private shippedAssemblies : (string * System.Reflection.Assembly) list =
  [ "SageFs.Core", typeof<SageFs.WorkerRoute>.Assembly
    "SageFs.Host", typeof<SageFs.WorkerHttpTransport.HttpWorkerServer>.Assembly
    "SageFs", typeof<SageFs.SessionListWatch>.Assembly ]

let private topLevelModuleNames (assembly: System.Reflection.Assembly) : string list =
  let types =
    try assembly.GetTypes()
    with :? System.Reflection.ReflectionTypeLoadException as partial -> partial.Types |> Array.filter (isNull >> not)
  types
  |> Array.filter (fun t -> FSharpType.IsModule t && not t.IsNested && not (t.FullName.Contains "StartupCode"))
  |> Array.map (fun t -> t.FullName)
  |> Array.toList

[<Tests>]
let tests =
  testList "module names" [
    testCase "WHY: no module's full name is declared in two shipped assemblies, because name resolution would pick one by reference order" <| fun _ ->
      shippedAssemblies
      |> List.collect (fun (assemblyName, assembly) -> topLevelModuleNames assembly |> List.map (fun name -> name, assemblyName))
      |> List.groupBy fst
      |> List.filter (fun (_, homes) -> homes |> List.map snd |> List.distinct |> List.length > 1)
      |> List.map (fun (name, homes) -> sprintf "%s is in %s" name (homes |> List.map snd |> String.concat " and "))
      |> Expect.isEmpty "every module name has one assembly"
  ]
