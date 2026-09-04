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

    testList "Module audit (synthesis 3.4)" [

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
          (n, 30)
          |> Expect.isLessThanOrEqual
            (sprintf "should have ≤30 empty modules (found %d)" n)

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

    ]

    testList "Closure seams (daemon vs host)" [

      let hostAssembly =
        tryLoadAssembly "SageFs.Host"

      testCase "SageFs.Host must not reference the SageFs daemon assembly"
      <| fun _ ->
        match hostAssembly with
        | Some host ->
          host
          |> doesNotReference
            "FSI host closure must be Core-only — the daemon (SageFs) must never load into the worker"
            "SageFs"
        | None ->
          failwith "SageFs.Host assembly not loadable — closure seam unverifiable"

      testCase "SageFs.Host must not reference ModelContextProtocol"
      <| fun _ ->
        match hostAssembly with
        | Some host ->
          host
          |> doesNotReferenceAny
            "MCP surface must stay out of the worker process closure"
            [ "ModelContextProtocol"; "StreamJsonRpc" ]
        | None ->
          failwith "SageFs.Host assembly not loadable — closure seam unverifiable"

      testCase "SageFs.Core must not contain the MCP hub modules"
      <| fun _ ->
        let coreTypeNames =
          coreAssembly.GetTypes()
          |> Array.map (fun t -> t.FullName)
        coreTypeNames
        |> Array.exists (fun n ->
          n = "SageFs.McpAdapter"
          || n = "SageFs.McpTools")
        |> Expect.isFalse
          "Mcp.fs (McpAdapter/McpTools) belongs in the daemon project, not Core"

      testCase "SageFs.Core must not contain the daemon MCP/Jupyter push modules"
      <| fun _ ->
        let coreTypeNames =
          coreAssembly.GetTypes()
          |> Array.map (fun t -> t.FullName)
        coreTypeNames
        |> Array.exists (fun n ->
          n = "SageFs.McpPushNotifications"
          || n = "SageFs.McpStateHandlers"
          || n = "SageFs.SessionEvents"
          || n = "SageFs.JupyterKernel")
        |> Expect.isFalse
          "MCP push/state handlers, SessionEvents, and JupyterKernel belong in the daemon project, not Core"

      testCase "SageFs.Core must not contain the daemon Elm kernel"
      <| fun _ ->
        let coreTypeNames =
          coreAssembly.GetTypes()
          |> Array.map (fun t -> t.FullName)
        coreTypeNames
        |> Array.exists (fun n ->
          n = "SageFs.SageFsApp"
          || n = "SageFs.SageFsModel"
          || n = "SageFs.SageFsUpdate"
          || n = "SageFs.SageFsRender"
          || n = "SageFs.SageFsEffectHandler"
          || n = "SageFs.ElmDaemon"
          || n = "SageFs.ElmLoop")
        |> Expect.isFalse
          "The daemon's Elm kernel (SageFsApp/ElmDaemon/ElmLoop) belongs in the daemon project, not Core"

      testCase "SageFs.Core must not contain the daemon render stack"
      <| fun _ ->
        let coreTypeNames =
          coreAssembly.GetTypes()
          |> Array.map (fun t -> t.FullName)
        coreTypeNames
        |> Array.exists (fun n ->
          n = "SageFs.CellGrid"
          || n = "SageFs.RenderRegion"
          || n = "SageFs.ThemeConfig"
          || n = "SageFs.SyntaxHighlight"
          || n = "SageFs.TerminalUI"
          || n = "SageFs.SessionDisplay"
          || n = "SageFs.Draw"
          || n = "SageFs.Screen"
          || n = "SageFs.AnsiEmitter"
          || n = "SageFs.EditorState"
          || n = "SageFs.TestsPane"
          || n = "SageFs.DirectoryConfig"
          || n = "SageFs.ConnectionTracker"
          || n = "SageFs.DaemonClient")
        |> Expect.isFalse
          "The daemon UI render stack belongs in the daemon project — the FSI host closure must be session-engine only"

      testCase "SageFs.Core must not reference ModelContextProtocol"
      <| fun _ ->
        coreAssembly
        |> doesNotReferenceAny
          "MCP SDK belongs to the daemon adapter layer, not the domain core"
          [ "ModelContextProtocol"; "StreamJsonRpc" ]

      testCase "SageFs.Core still hosts the worker-domain modules"
      <| fun _ ->
        let coreTypeNames =
          coreAssembly.GetTypes()
          |> Array.map (fun t -> t.FullName)
        let found (simpleName: string) =
          coreTypeNames
          |> Array.exists (fun n ->
            n = simpleName
            || n.EndsWith("+" + simpleName, StringComparison.Ordinal)
            || n.EndsWith("." + simpleName, StringComparison.Ordinal))
        for expected in
          [ "SessionId"          // WorkerProtocol
            "SessionPhase"       // AppState
            "SessionManager"     // supervisor module
            "SessionOperations" ] do
          found expected
          |> Expect.isTrue
            (sprintf "Core must still define %s (seam test must not pass vacuously)" expected)
    ]
  ]
