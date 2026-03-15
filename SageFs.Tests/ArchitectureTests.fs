module SageFs.Tests.ArchitectureTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs

// ---------------------------------------------------------------------------
// Assembly references for architecture validation
// ---------------------------------------------------------------------------

let private coreAssembly =
  typeof<SageFs.WorkerProtocol.SessionId>.Assembly

let private cliAssembly =
  Assembly.Load "SageFs"

let private testAssembly =
  Assembly.GetExecutingAssembly()

let private referencedAssemblyNames (asm: Assembly) =
  asm.GetReferencedAssemblies()
  |> Array.map (fun a -> a.Name)

let private tryLoadAssembly (name: string) =
  try
    Some(Assembly.Load name)
  with
  | :? System.IO.FileNotFoundException -> None
  | :? System.IO.FileLoadException -> None
  | _ -> None

let private doesNotReference desc forbidden (asm: Assembly) =
  referencedAssemblyNames asm
  |> Array.exists (fun n -> n = forbidden)
  |> Expect.isFalse desc

let private doesNotReferenceAny desc (patterns: string list) (asm: Assembly) =
  referencedAssemblyNames asm
  |> Array.exists (fun n ->
    patterns
    |> List.exists (fun p ->
      n.Contains(p, StringComparison.OrdinalIgnoreCase)))
  |> Expect.isFalse desc

let private isDocAttribute (attr: Attribute) =
  match attr.GetType().Name with
  | "CompiledNameAttribute"
  | "StructAttribute"
  | "RequireQualifiedAccessAttribute"
  | "AutoOpenAttribute"
  | "ObsoleteAttribute"
  | "AbstractClassAttribute"
  | "SealedAttribute" -> true
  | name when name.Contains "Doc" -> true
  | _ -> false

[<Tests>]
let architectureTests =
  testList "Architecture" [

    testList "Assembly dependency rules" [

      testCase "SageFs.Core must not reference SageFs CLI assembly"
      <| fun _ ->
        coreAssembly
        |> doesNotReference
          "SageFs.Core should not depend on the CLI tool"
          "SageFs"

      testCase "SageFs.Core must not reference SageFs.Gui"
      <| fun _ ->
        coreAssembly
        |> doesNotReference
          "SageFs.Core should not depend on the GUI project"
          "SageFs.Gui"

      testCase "SageFs.Core must not reference test assemblies"
      <| fun _ ->
        coreAssembly
        |> doesNotReferenceAny
          "SageFs.Core should not depend on any test framework"
          [ "Test"; "Expecto"; "FsCheck"; "xUnit" ]

      testCase "SageFs.Core must not reference SageFs.Tests"
      <| fun _ ->
        coreAssembly
        |> doesNotReference
          "SageFs.Core should not depend on the test project"
          "SageFs.Tests"

      testCase "SageFs CLI must not reference SageFs.Tests"
      <| fun _ ->
        cliAssembly
        |> doesNotReference
          "SageFs CLI should not depend on the test project"
          "SageFs.Tests"

      testCase "SageFs.Core must not reference Raylib-cs"
      <| fun _ ->
        referencedAssemblyNames coreAssembly
        |> Array.exists (fun n ->
          n.Contains("Raylib", StringComparison.OrdinalIgnoreCase))
        |> Expect.isFalse
          "SageFs.Core should not depend on Raylib-cs — GUI deps belong in SageFs.Gui"

      testCase "SageFs.Gui must not reference SageFs CLI"
      <| fun _ ->
        match tryLoadAssembly "SageFs.Gui" with
        | Some guiAssembly ->
          guiAssembly
          |> doesNotReference
            "SageFs.Gui should depend on Core, not CLI"
            "SageFs"
        | None ->
          // SageFs.Gui not referenced by test project — rule enforced
          // at build time via project references instead
          ()
    ]

    testList "Assembly identity" [

      testCase "all test files live in SageFs.Tests assembly"
      <| fun _ ->
        testAssembly.GetName().Name
        |> Expect.equal
          "test assembly should be named SageFs.Tests"
          "SageFs.Tests"

      testCase "Core assembly is named SageFs.Core"
      <| fun _ ->
        coreAssembly.GetName().Name
        |> Expect.equal
          "Core assembly should be named SageFs.Core"
          "SageFs.Core"
    ]

    testList "Error classification consistency" [

      testCase "every SageFsError has exactly one category"
      <| fun _ ->
        let errorCases =
          FSharp.Reflection.FSharpType.GetUnionCases(typeof<SageFsError>)
        for case in errorCases do
          let args =
            case.GetFields()
            |> Array.map (fun f ->
              match f.PropertyType with
              | t when t = typeof<string> -> box ""
              | t when t = typeof<int> -> box 0
              | t when t = typeof<float> -> box 0.0
              | t when t = typeof<exn> -> box (System.Exception "test")
              | t when t = typeof<string list> -> box ([] : string list)
              | t when t = typeof<SessionState> -> box SessionState.Uninitialized
              | _ -> box null)
          let err =
            FSharp.Reflection.FSharpValue.MakeUnion(case, args) :?> SageFsError
          let categories =
            [ SageFsError.isClientError err
              SageFsError.isServerError err
              SageFsError.isGatewayError err
              SageFsError.isInfraError err ]
            |> List.filter id
          categories
          |> Expect.hasLength
            (sprintf "%s should have exactly one classification" case.Name) 1

      testCase "toHttpStatus returns valid HTTP status codes"
      <| fun _ ->
        let errorCases =
          FSharp.Reflection.FSharpType.GetUnionCases(typeof<SageFsError>)
        for case in errorCases do
          let args =
            case.GetFields()
            |> Array.map (fun f ->
              match f.PropertyType with
              | t when t = typeof<string> -> box ""
              | t when t = typeof<int> -> box 0
              | t when t = typeof<float> -> box 0.0
              | t when t = typeof<exn> -> box (System.Exception "test")
              | t when t = typeof<string list> -> box ([] : string list)
              | t when t = typeof<SessionState> -> box SessionState.Uninitialized
              | _ -> box null)
          let err =
            FSharp.Reflection.FSharpValue.MakeUnion(case, args) :?> SageFsError
          let status = SageFsError.toHttpStatus err
          (status, 100)
          |> Expect.isGreaterThanOrEqual
            (sprintf "%s status %d should be >= 100" case.Name status)
          (599, status)
          |> Expect.isGreaterThanOrEqual
            (sprintf "%s status %d should be <= 599" case.Name status)

      testCase "describe never returns empty string"
      <| fun _ ->
        let errorCases =
          FSharp.Reflection.FSharpType.GetUnionCases(typeof<SageFsError>)
        for case in errorCases do
          let args =
            case.GetFields()
            |> Array.map (fun f ->
              match f.PropertyType with
              | t when t = typeof<string> -> box "test-value"
              | t when t = typeof<int> -> box 42
              | t when t = typeof<float> -> box 1.0
              | t when t = typeof<exn> -> box (System.Exception "boom")
              | t when t = typeof<string list> -> box ([ "a"; "b" ] : string list)
              | t when t = typeof<SessionState> -> box SessionState.Uninitialized
              | _ -> box null)
          let err =
            FSharp.Reflection.FSharpValue.MakeUnion(case, args) :?> SageFsError
          let desc = SageFsError.describe err
          System.String.IsNullOrWhiteSpace desc
          |> Expect.isFalse
            (sprintf "%s.describe should not be empty" case.Name)
    ]

    testList "Module count tracking (aspirational)" [

      testCase "SageFs.Core module count tracked"
      <| fun _ ->
        let modules =
          coreAssembly.GetTypes()
          |> Array.filter (fun t ->
            FSharp.Reflection.FSharpType.IsModule t
            && not (t.Name.StartsWith "<")
            && not (t.Name.Contains "@")
            && not t.IsNested)
        printfn "  SageFs.Core modules: %d" modules.Length
        // Ceiling prevents regression. Lower as consolidation progresses.
        // Current verified baseline: 240 (2026-03-14, +2 WorkflowTypes+WorkflowErrorContext). Target: ≤60 (synthesis 3.4).
        (modules.Length, 240)
        |> Expect.isLessThanOrEqual
          (sprintf
            "SageFs.Core should have ≤240 top-level modules (currently %d)"
            modules.Length)

      testCase "SageFs.Core exported types tracked"
      <| fun _ ->
        let types =
          coreAssembly.GetExportedTypes()
          |> Array.filter (fun t ->
            not (t.Name.StartsWith "<")
            && not (t.Name.Contains "@")
            && not t.IsNested)
        printfn "  SageFs.Core exported types: %d" types.Length
        // Track — don't enforce too tightly yet
        (types.Length, 500)
        |> Expect.isLessThanOrEqual
          (sprintf
            "SageFs.Core should have ≤500 exported types (currently %d)"
            types.Length)
    ]

    testList "Module audit (synthesis 3.4)" [

      testCase "CLI assembly (SageFs) module count tracked"
      <| fun _ ->
        let modules =
          cliAssembly.GetTypes()
          |> Array.filter (fun t ->
            FSharp.Reflection.FSharpType.IsModule t
            && not (t.Name.StartsWith "<")
            && not (t.Name.Contains "@")
            && not t.IsNested)
        printfn "  SageFs CLI modules: %d" modules.Length
        // Track CLI module count too
        (modules.Length, 50)
        |> Expect.isLessThanOrEqual
          (sprintf
            "SageFs CLI should have ≤50 top-level modules (currently %d)"
            modules.Length)

      testCase "modules with zero public functions identified"
      <| fun _ ->
        let modules =
          coreAssembly.GetTypes()
          |> Array.filter (fun t ->
            FSharp.Reflection.FSharpType.IsModule t
            && not (t.Name.StartsWith "<")
            && not (t.Name.Contains "@")
            && not t.IsNested)
        let emptyModules =
          modules
          |> Array.filter (fun m ->
            let publicMethods =
              m.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
              |> Array.filter (fun mi ->
                not (mi.Name.StartsWith "get_")
                && not (mi.Name.StartsWith "set_")
                && not (mi.Name.StartsWith "<")
                && not (mi.IsSpecialName))
            let publicProps =
              m.GetProperties(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
            publicMethods.Length = 0 && publicProps.Length = 0)
        match emptyModules.Length with
        | 0 -> ()
        | n ->
          printfn "  Modules with zero public API:"
          emptyModules |> Array.iter (fun m -> printfn "    - %s" m.FullName)
          // These are candidates for removal or consolidation
          (n, 24)
          |> Expect.isLessThanOrEqual
            (sprintf "should have ≤24 empty modules (found %d)" n)

      testCase "modules with only type definitions tracked"
      <| fun _ ->
        // Modules that only contain types (no functions) are "type-bag" modules
        // These might be better as namespaces
        let modules =
          coreAssembly.GetTypes()
          |> Array.filter (fun t ->
            FSharp.Reflection.FSharpType.IsModule t
            && not (t.Name.StartsWith "<")
            && not (t.Name.Contains "@")
            && not t.IsNested)
        let typeBagModules =
          modules
          |> Array.filter (fun m ->
            let publicMethods =
              m.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
              |> Array.filter (fun mi ->
                not (mi.Name.StartsWith "get_")
                && not (mi.Name.StartsWith "set_")
                && not (mi.Name.StartsWith "<")
                && not (mi.IsSpecialName))
            let nestedTypes = m.GetNestedTypes() |> Array.length
            publicMethods.Length = 0 && nestedTypes > 0)
        printfn "  Type-bag modules (types only, no functions): %d" typeBagModules.Length
        typeBagModules |> Array.iter (fun m -> printfn "    - %s" m.FullName)
        // Track — these are candidates for namespace conversion
        (typeBagModules.Length, 30)
        |> Expect.isLessThanOrEqual
          (sprintf "should have ≤30 type-bag modules (found %d)" typeBagModules.Length)

      testCase "combined module+type count doesn't grow"
      <| fun _ ->
        let coreModules =
          coreAssembly.GetTypes()
          |> Array.filter (fun t ->
            FSharp.Reflection.FSharpType.IsModule t
            && not (t.Name.StartsWith "<")
            && not (t.Name.Contains "@")
            && not t.IsNested)
          |> Array.length
        let cliModules =
          cliAssembly.GetTypes()
          |> Array.filter (fun t ->
            FSharp.Reflection.FSharpType.IsModule t
            && not (t.Name.StartsWith "<")
            && not (t.Name.Contains "@")
            && not t.IsNested)
          |> Array.length
        let total = coreModules + cliModules
        printfn "  Total modules (Core + CLI): %d" total
        // Combined ceiling — should only go DOWN
        (total, 261)
        |> Expect.isLessThanOrEqual
          (sprintf "combined module count should be ≤261 (currently %d)" total)
    ]
  ]
