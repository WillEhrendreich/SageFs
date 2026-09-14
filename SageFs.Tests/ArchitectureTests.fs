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

// ---------------------------------------------------------------------------
// Cohort command/effect wiring — dead-limb detection (roast-7 §5/§6, item 6)
// ---------------------------------------------------------------------------
//
// `CohortCommand<'m>` and `CohortEffect<'m>` (SageFs.Core/Cohort.fs) are
// interpreted EXHAUSTIVELY by `decide` and by `CohortOwner`'s effect
// dispatcher — but exhaustive interpretation says nothing about whether any
// PRODUCTION code path ever CONSTRUCTS a given case. A case that only tests
// construct (or that `decide` only pattern-matches and never emits as an
// effect) is a dead limb: it compiles, it is "handled," and it does nothing
// for a real user or agent. This scans SageFs/ and SageFs.Core/ source
// (excluding SageFs.Tests and any worktree) for a real construction site —
// an occurrence of `CohortCommand.<Case>` / `CohortEffect.<Case>` that is
// NOT itself the pattern-match arm that interprets the case and NOT inside a
// comment — for every case reflection finds on the type today. A case with
// no such site must be named on the allow-list below, with a reason;
// forgetting the allow-list entry fails the build instead of waiting for the
// next roast to notice.

let private repoRoot =
  System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, ".."))

/// Every `.fs` file under SageFs/ and SageFs.Core/ — the two production
/// projects — excluding the test project, build output, and any worktree.
let private productionFsFiles () =
  [ "SageFs"; "SageFs.Core" ]
  |> List.collect (fun projectDir ->
    let dir = System.IO.Path.Combine(repoRoot, projectDir)
    if System.IO.Directory.Exists dir then
      System.IO.Directory.GetFiles(dir, "*.fs", System.IO.SearchOption.AllDirectories)
      |> Array.toList
    else
      [])
  |> List.filter (fun path ->
    // Filter on the path RELATIVE to repoRoot, not the absolute path — this
    // worktree's own checkout root commonly sits under
    // `.claude/worktrees/<agent>/`, so testing the absolute path against
    // "/worktrees/" would exclude every file in the current checkout too.
    let relative =
      System.IO.Path.GetRelativePath(repoRoot, path).Replace(System.IO.Path.DirectorySeparatorChar, '/')
    not (relative.StartsWith "SageFs.Tests/")
    && not (relative.Contains "/obj/")
    && not (relative.Contains "/bin/")
    && not (relative.Contains "/worktrees/"))

/// True when `line` is the pattern-match ARM that interprets `needle`
/// (`| needle ... ->`), never a value construction of it.
let private isMatchArmFor (needle: string) (line: string) =
  let trimmed = line.TrimStart()
  trimmed.StartsWith("|")
  && not (trimmed.StartsWith("|>"))
  && trimmed.Substring(1).TrimStart().StartsWith(needle)

let private isCommentLine (line: string) =
  line.TrimStart().StartsWith("//")

/// A real construction site: `qualifiedNeedle` appears in `line` as a whole
/// identifier (word-boundary after the case name, so `FastForward` does not
/// false-match inside `FastForwardCompleted`), the line is not a `///`/`//`
/// comment, and the line is not itself the match arm that interprets the
/// case.
let private isConstructionSite (qualifiedNeedle: string) (line: string) =
  not (isCommentLine line)
  && Text.RegularExpressions.Regex.IsMatch(line, Text.RegularExpressions.Regex.Escape qualifiedNeedle + @"\b")
  && not (isMatchArmFor qualifiedNeedle line)

/// Does any production `.fs` file construct `TypeName.CaseName` (optionally
/// module-qualified as `Cohort.TypeName.CaseName`) outside a comment and
/// outside the arm that pattern-matches it?
let private hasProductionConstructionSite (typeName: string) (caseName: string) (files: string list) =
  let unqualified = typeName + "." + caseName
  let qualified = "Cohort." + unqualified
  files
  |> List.exists (fun path ->
    System.IO.File.ReadAllLines path
    |> Array.exists (fun line ->
      isConstructionSite unqualified line || isConstructionSite qualified line))

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
              | t when t = typeof<BuildDiagnostic list> -> box ([ BuildDiagnostic.ofLine "test error" ] : BuildDiagnostic list)
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
              | t when t = typeof<BuildDiagnostic list> -> box ([ BuildDiagnostic.ofLine "test error" ] : BuildDiagnostic list)
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
              | t when t = typeof<BuildDiagnostic list> -> box ([ BuildDiagnostic.ofLine "test error" ] : BuildDiagnostic list)
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
          || n = "SageFs.Server.SseEvent"
          || n = "SageFs.JupyterKernel")
        |> Expect.isFalse
          "MCP push/state handlers, the unified SseEvent wire vocabulary, and JupyterKernel belong in the daemon project, not Core"

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

    testList "Cohort command/effect wiring (roast-7 §5/§6 — dead-limb detection)" [

      // Cases with no production construction site today. Each entry names
      // the reason — remove the entry the moment a real construction site
      // lands, or the "allow-list rot" test below will fail.
      let cohortCommandAllowList =
        Map.ofList [
          "RenewLease",
          "no production poster for member-liveness renewal exists yet — \
           `decide` only pattern-matches it (SageFs.Core/Cohort.fs); nothing \
           in the shell renews a lease on a member's behalf"
          "Tick",
          "the lease reaper — the shell never posts a periodic Tick, so \
           silent-member detection via `Clock - LastRenewal >= leaseWindow` \
           is unreachable in production (roast-7 §5)"
          "DelegateConductor",
          "constructed only by SageFs.Tests today; no MCP tool or dashboard \
           action delegates the conductor role yet (roast-7 §5)"
          "WithdrawLanding",
          "constructed only by SageFs.Tests today; no MCP tool or dashboard \
           action withdraws a queued landing yet (roast-7 §5)"
          "VetoLanding",
          "constructed only by SageFs.Tests today; no MCP tool or dashboard \
           action vetoes a queued landing yet (roast-7 §5)"
        ]

      let cohortEffectAllowList =
        Map.ofList [
          "Notify",
          "`decide` defines CohortEffect.Notify but no `decide` arm ever \
           constructs one — fire-and-forget member notification is not \
           implemented (roast-7 §5; CohortOwner.fs's own doc comment says \
           as much)"
        ]

      testCase "every CohortCommand case has a production construction site or is on the allow-list"
      <| fun _ ->
        let files = productionFsFiles ()
        files
        |> List.isEmpty
        |> Expect.isFalse
          "source scan must actually find SageFs/SageFs.Core .fs files — path resolution is broken"
        let cases =
          FSharp.Reflection.FSharpType.GetUnionCases(typeof<Cohort.CohortCommand<string>>)
        (cases.Length, 0)
        |> Expect.isGreaterThan
          "CohortCommand must expose at least one case (guards against a rename making this test vacuous)"
        for case in cases do
          if not (Map.containsKey case.Name cohortCommandAllowList) then
            hasProductionConstructionSite "CohortCommand" case.Name files
            |> Expect.isTrue
              (sprintf
                "CohortCommand.%s has no production construction site in SageFs/SageFs.Core and is not on the allow-list — wire it up (post it from the shell) or add a documented allow-list entry explaining why it is still unimplemented"
                case.Name)

      testCase "every CohortEffect case has a production construction site or is on the allow-list"
      <| fun _ ->
        let files = productionFsFiles ()
        files
        |> List.isEmpty
        |> Expect.isFalse
          "source scan must actually find SageFs/SageFs.Core .fs files — path resolution is broken"
        let cases =
          FSharp.Reflection.FSharpType.GetUnionCases(typeof<Cohort.CohortEffect<string>>)
        (cases.Length, 0)
        |> Expect.isGreaterThan
          "CohortEffect must expose at least one case (guards against a rename making this test vacuous)"
        for case in cases do
          if not (Map.containsKey case.Name cohortEffectAllowList) then
            hasProductionConstructionSite "CohortEffect" case.Name files
            |> Expect.isTrue
              (sprintf
                "CohortEffect.%s has no production construction site in SageFs/SageFs.Core and is not on the allow-list — wire it up (have `decide` emit it) or add a documented allow-list entry explaining why it is still unimplemented"
                case.Name)

      testCase "the Cohort wiring allow-list only names cases that genuinely have no construction site"
      <| fun _ ->
        // The other direction of rot: once a previously-unimplemented case
        // gets wired up, its allow-list entry becomes a stale lie unless
        // someone removes it. The two tests above skip allow-listed cases
        // entirely, so this is the only test that would catch that drift.
        let files = productionFsFiles ()
        for caseName in Map.toList cohortCommandAllowList |> List.map fst do
          hasProductionConstructionSite "CohortCommand" caseName files
          |> Expect.isFalse
            (sprintf
              "CohortCommand.%s now HAS a production construction site — remove it from the allow-list in ArchitectureTests.fs"
              caseName)
        for caseName in Map.toList cohortEffectAllowList |> List.map fst do
          hasProductionConstructionSite "CohortEffect" caseName files
          |> Expect.isFalse
            (sprintf
              "CohortEffect.%s now HAS a production construction site — remove it from the allow-list in ArchitectureTests.fs"
              caseName)
    ]
  ]
