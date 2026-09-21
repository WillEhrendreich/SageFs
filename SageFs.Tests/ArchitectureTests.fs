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
          // The DirectoryConfig record itself is session-engine data (the isolated host evaluates config.fsx into it,
          // and it no longer carries keybindings or themes); only the daemon's loading module stays out of Core.
          || n = "SageFs.DirectoryConfigModule"
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

    testList "Documentation site" [

      // The GitHub Pages site at willehrendreich.github.io/SageFs was a DIFFERENT,
      // older product: it linked to none of the current docs, said "Visual Studio"
      // four times, claimed 30 MCP tools when there were 50, documented CLI
      // subcommands that do not exist, capitalised the binary as `SageFs` (which does
      // not run on Linux/macOS), and served docs/internal/** publicly — raw working
      // notes with local Windows paths in them. Nothing validated any of it, so it
      // drifted silently while the product moved. It has been taken down; the
      // documentation site is sagetech.dev/sagefs.
      //
      // This guard exists because a dead URL comes back the moment someone writes a
      // "docs" link from memory.
      testCase "WHY — no tracked file references the retired GitHub Pages docs site, because a link to it sends users to a different product" <| fun _ ->
        let offenders =
          System.Diagnostics.Process.Start(
            System.Diagnostics.ProcessStartInfo(
              FileName = "git",
              Arguments = "ls-files",
              WorkingDirectory = repoRoot,
              RedirectStandardOutput = true,
              UseShellExecute = false))
          |> fun p ->
            let out = p.StandardOutput.ReadToEnd()
            p.WaitForExit()
            out.Split('\n')
          |> Array.map (fun f -> f.Trim())
          |> Array.filter (fun f -> f.Length > 0)
          // The guard names the URL itself, so exclude this file from its own scan.
          |> Array.filter (fun f -> not (f.EndsWith "ArchitectureTests.fs"))
          |> Array.choose (fun relative ->
            let full = System.IO.Path.Combine(repoRoot, relative)
            match System.IO.File.Exists full with
            | false -> None
            | true ->
              try
                let text = System.IO.File.ReadAllText full
                match text.Contains "github.io/SageFs" with
                | true -> Some relative
                | false -> None
              with _ -> None)
        offenders
        |> Expect.isEmpty
          (sprintf
            "these tracked files link to the retired GitHub Pages docs site — use https://sagetech.dev/sagefs instead: %s"
            (String.concat ", " offenders))

      testCase "WHY — the site's own HTML is gone, so it cannot be republished by re-enabling Pages" <| fun _ ->
        [ "docs/index.html"; "docs/documentation.html"; "docs/architecture-graph.html" ]
        |> List.filter (fun f -> System.IO.File.Exists(System.IO.Path.Combine(repoRoot, f)))
        |> Expect.isEmpty "the retired docs-site HTML must stay deleted"
    ]

    testList "Cohort command/effect wiring (roast-7 §5/§6 — dead-limb detection)" [

      // Cases with no production construction site today. Each entry names
      // the reason — remove the entry the moment a real construction site
      // lands, or the "allow-list rot" test below will fail.
      // NOTE (roast-7 §5, now wired): RenewLease and Tick were removed from this
      // list when the daemon's cohort lease reaper landed — a 60s timer in
      // DaemonMode.cohortReaperCallback renews each active Present member's lease
      // and posts Tick, so silent-member detection via
      // `Clock - LastRenewal >= leaseWindow` runs in production. Their presence
      // here would now fail the "allow-list rot" test below.
      let cohortCommandAllowList =
        Map.ofList [
          "DelegateConductor",
          "constructed only by SageFs.Tests today; no MCP tool or dashboard \
           action delegates the conductor role yet (roast-7 §5)"
          "WithdrawLanding",
          "constructed only by SageFs.Tests today; no MCP tool or dashboard \
           action withdraws a queued landing yet (roast-7 §5)"
          "VetoLanding",
          "constructed only by SageFs.Tests today; no MCP tool or dashboard \
           action vetoes a queued landing yet (roast-7 §5)"
          "ResolveVeto",
          "constructed only by SageFs.Tests/SageFs.Simulation today; no MCP \
           tool or dashboard action resolves a veto yet — the command exists \
           so `VetoLanding`'s NextAction.AwaitConductor has a real recovery \
           path, wiring the MCP/dashboard verb is follow-up work (armfix, \
           cmd-handoff.md item B2)"
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

// ---------------------------------------------------------------------------
// Actor wait-for graph acyclicity (roast-7 §2) — the formal-verification
// beachhead.
//
// Deadlock = a cycle in the actor wait-for relation: actor A issues a
// BLOCKING request to actor B and suspends → edge A→B. A cycle where every
// participant is blocked is a deadlock. `ResilientActor.wrapLoop` cannot
// catch this — it only catches exceptions a loop throws, and a deadlocked
// loop throws nothing; it just never returns from the blocking call.
//
// HARD CONSTRAINT this test honors: the edges are EXTRACTED by scanning the
// real source every run — a hand-declared "who waits on whom" adjacency list
// is exactly the drifting model this effort exists to replace. The only
// hand-declared knowledge below is `receiverToNode`, a small identifier→node
// map (e.g. "sessionManager" is the local name bound to the SessionManager
// mailbox) — never an edge. Every edge comes from a regex match against a
// real blocking call-site in the actual tree, with its file:line recorded so
// a failure can name the cycle.
// ---------------------------------------------------------------------------

/// The graph algebra: build {nodes, edges(with source locations)}, then a
/// plain Kahn's-algorithm topological sort — v1 uses no external solver.
/// Separate from `ActorWaitForExtraction` so the RED-phase fixture tests can
/// prove the checker itself catches a cycle before any real source is ever
/// scanned.
module private WaitForGraph =

  type Edge = {
    From: string
    To: string
    File: string
    Line: int
    Snippet: string
  }

  type Graph = {
    Nodes: Set<string>
    Edges: Edge list
  }

  let mkEdge (fromNode: string) (toNode: string) (file: string) (line: int) (snippet: string) : Edge =
    { From = fromNode; To = toNode; File = file; Line = line; Snippet = snippet }

  /// Kahn's algorithm: repeatedly remove a zero-indegree node. `Ok order` =
  /// every node was eventually removable, so the graph is acyclic. `Error
  /// stuck` = the nodes still remaining once no zero-indegree node is left —
  /// every one of them has an unresolved incoming edge, i.e. participates in
  /// some cycle. Deterministic (`Set.minElement`) so a failure is
  /// reproducible, not order-dependent.
  let topoSort (graph: Graph) : Result<string list, Set<string>> =
    let outgoing =
      graph.Edges
      |> List.groupBy (fun e -> e.From)
      |> Map.ofList
    let initialIndegree =
      graph.Nodes
      |> Set.toList
      |> List.map (fun n -> n, (graph.Edges |> List.filter (fun e -> e.To = n) |> List.length))
      |> Map.ofList
    let rec loop (remaining: Set<string>) (indegree: Map<string, int>) (order: string list) =
      let ready = remaining |> Set.filter (fun n -> Map.find n indegree = 0)
      match Set.isEmpty remaining with
      | true -> Ok(List.rev order)
      | false ->
        match Set.isEmpty ready with
        | true -> Error remaining
        | false ->
          let picked = Set.minElement ready
          let remaining' = Set.remove picked remaining
          let indegree' =
            outgoing
            |> Map.tryFind picked
            |> Option.defaultValue []
            |> List.fold
              (fun acc (e: Edge) ->
                match Set.contains e.To remaining' with
                | true -> Map.add e.To (Map.find e.To acc - 1) acc
                | false -> acc)
              indegree
          loop remaining' indegree' (picked :: order)
    loop graph.Nodes initialIndegree []

  /// DFS back-edge search — only invoked to build a human-readable cycle
  /// path once `topoSort` has already proven a cycle exists. Not exhaustive
  /// (it stops at the first cycle found), which is all a build-failure
  /// message needs.
  let findCycle (graph: Graph) : Edge list option =
    let outgoing =
      graph.Edges
      |> List.groupBy (fun e -> e.From)
      |> Map.ofList
    let settled = System.Collections.Generic.HashSet<string>()
    let rec visit (node: string) (pathNodes: string list) (pathEdges: Edge list) : Edge list option =
      match List.tryFindIndex ((=) node) pathNodes with
      | Some idx -> Some(pathEdges |> List.skip idx)
      | None ->
        match settled.Contains node with
        | true -> None
        | false ->
          settled.Add node |> ignore
          outgoing
          |> Map.tryFind node
          |> Option.defaultValue []
          |> List.tryPick (fun edge -> visit edge.To (pathNodes @ [ node ]) (pathEdges @ [ edge ]))
    graph.Nodes |> Set.toList |> List.tryPick (fun n -> visit n [] [])

  let isAcyclic (graph: Graph) : bool =
    graph |> topoSort |> Result.isOk

  let describeCycle (edges: Edge list) : string =
    edges
    |> List.map (fun e -> sprintf "%s.%s:%d → %s" e.From e.File e.Line e.To)
    |> String.concat "\n    "

/// Extracts the REAL wait-for graph by scanning production source text.
/// Nodes are actor mailbox owners confirmed against the tree (see the doc
/// comments on each `ScanRegion` below); edges are found, never declared, by
/// matching known blocking-call shapes inside each actor's own
/// message-handling code.
module private ActorWaitForExtraction =
  open WaitForGraph

  /// Receiver-identity map (allowed to be hand-declared): the local
  /// identifier a blocking call is issued THROUGH → the actor node it
  /// targets. This is not "who waits on whom" — it is just naming, exactly
  /// like `productionFsFiles`'s file-path filter above naming which
  /// directories are production code. The edges themselves (which lines
  /// actually call through these identifiers) are found by regex, below.
  let receiverToNode =
    Map.ofList [
      "sessionManager", "SessionManager" // SessionManager.fs's own mailbox handle
      "cohortLandingCacheOwner", "LandingCacheOwner" // CohortOwner.fs's LandingCacheOwner.Handle
      "manifestOwner", "ManifestOwner" // ManifestOwner.fs's Handle
    ]

  /// One actor's own message-handling source, as a (file, start-marker,
  /// end-marker) slice of REAL source text. This is architecture knowledge
  /// (which function defines an actor's own mailbox loop), not an edge — no
  /// call-site or target is named here. `None` markers mean "whole file."
  /// Confirmed against the tree at authoring time (2026-09):
  ///  - SessionManager: SessionManager.fs is the whole supervisor module —
  ///    its mailbox loop plus every `Async.Start`'d helper it spawns and
  ///    feeds back into itself via `inbox.Post`.
  ///  - ElmLoop: `ElmDaemon.fs` builds the `EffectDeps` record ElmLoop's
  ///    own `ExecuteEffect` calls into (ElmLoop.fs:253); `DaemonMode.fs`'s
  ///    `createElmRuntime` builds the SAME `EffectDeps` for the daemon,
  ///    with its own additional overrides — both are ElmLoop's own effect
  ///    code, never the mailbox's caller.
  ///  - CohortOwner: `CohortOwner.fs` up to (not including) the nested
  ///    `LandingCacheOwner` module is `decide`/`handle`/the effect
  ///    dispatcher; `DaemonMode.fs`'s `cohortLandingPerformer` binding is
  ///    the `LandingPerformer` CohortOwner's own effect dispatcher invokes
  ///    (`dispatchLandingEffects`, CohortOwner.fs:234-251) to perform a
  ///    `RunTests` effect — off the mailbox thread via `Async.Start`, but
  ///    still CohortOwner's own effect-completion work: a hang here means
  ///    that landing's own effect never posts its completion command back.
  ///  - LandingCacheOwner: the nested `LandingCacheOwner` module in
  ///    `CohortOwner.fs` (from its own `module LandingCacheOwner =` to end
  ///    of file) — a single-writer mailbox exactly like `ManifestOwner`.
  ///  - QueryActor / EvalActor / RouterActor: the three
  ///    `MailboxProcessor.Start` bindings inside `AppState.fs`'s session
  ///    actor constructor, confirmed present at authoring time.
  ///  - ManifestOwner: `ManifestOwner.fs` is the whole module.
  type ScanRegion = {
    Node: string
    File: string
    StartContains: string option
    EndContains: string option
  }

  let scanRegions : ScanRegion list = [
    { Node = "SessionManager"; File = "SageFs.Core/SessionManager.fs"; StartContains = None; EndContains = None }

    { Node = "ElmLoop"; File = "SageFs/ElmDaemon.fs"; StartContains = None; EndContains = None }
    { Node = "ElmLoop"
      File = "SageFs/DaemonMode.fs"
      StartContains = Some "let createElmRuntime"
      EndContains = Some "let dispatchOutputAndWait" }

    { Node = "CohortOwner"
      File = "SageFs.Core/Features/CohortOwner.fs"
      StartContains = None
      EndContains = Some "module LandingCacheOwner =" }
    { Node = "CohortOwner"
      File = "SageFs/DaemonMode.fs"
      StartContains = Some "let cohortLandingPerformer"
      EndContains = Some "let cohortLedgerPort" }

    { Node = "LandingCacheOwner"
      File = "SageFs.Core/Features/CohortOwner.fs"
      StartContains = Some "module LandingCacheOwner ="
      EndContains = None }

    { Node = "QueryActor"
      File = "SageFs.Core/AppState.fs"
      StartContains = Some "let queryActor = MailboxProcessor<QueryCommand>.Start"
      EndContains = Some "let evalActor = MailboxProcessor<EvalCommand>.Start" }
    { Node = "EvalActor"
      File = "SageFs.Core/AppState.fs"
      StartContains = Some "let evalActor = MailboxProcessor<EvalCommand>.Start"
      EndContains = Some "let actor = MailboxProcessor.Start" }
    { Node = "RouterActor"
      File = "SageFs.Core/AppState.fs"
      StartContains = Some "let actor = MailboxProcessor.Start"
      EndContains = Some "let getSessionState () =" }

    { Node = "ManifestOwner"; File = "SageFs.Core/Features/ManifestOwner.fs"; StartContains = None; EndContains = None }
  ]

  /// Blocking-call shapes recognized today (roast-7 §2's brief). Each is a
  /// real construct that suspends the caller until the target actor's
  /// mailbox replies:
  ///  1. `<receiver>.PostAndReply(` / `.PostAndAsyncReply(` — a direct
  ///     mailbox round-trip.
  ///  2. `<receiver>.<OwnerMethod>` where `<OwnerMethod>` is one of a
  ///     single-writer `Handle`'s own round-tripping members (`Verify`,
  ///     `Commit`, `Read`, `Flush`, `QuarantineCorrupt` — confirmed against
  ///     `ManifestOwner.fs`'s and `CohortOwner.fs`'s `Handle` types).
  ///  3. `proxy (WorkerMessage. ...)` — an awaited call across the
  ///     daemon↔worker HTTP port (`WorkerProtocol.SessionProxy`).
  let private receiverAlternation =
    receiverToNode |> Map.toList |> List.map (fst >> Text.RegularExpressions.Regex.Escape) |> String.concat "|"

  let private ownerMethodNames = [ "Verify"; "Commit"; "Read"; "Flush"; "QuarantineCorrupt" ]

  let private postAndReplyPattern =
    Text.RegularExpressions.Regex(sprintf @"\b(%s)\.(PostAndAsyncReply|PostAndReply)\s*\(" receiverAlternation)

  let private ownerMethodPattern =
    Text.RegularExpressions.Regex(
      sprintf @"\b(%s)\.(%s)\b" receiverAlternation (ownerMethodNames |> String.concat "|"))

  let private workerCallPattern =
    Text.RegularExpressions.Regex(@"\bproxy\s*\(\s*WorkerMessage\.")

  /// The (1-indexed line number, text) pairs inside `region`'s slice of its
  /// file — the marker lines are included (a call could in principle share
  /// the start-marker's own line), everything after the end-marker is not.
  let private regionLines (repoRoot: string) (region: ScanRegion) : (int * string) list =
    let path = System.IO.Path.Combine(repoRoot, region.File)
    match System.IO.File.Exists path with
    | false -> []
    | true ->
      let lines = System.IO.File.ReadAllLines path
      let startIdx =
        match region.StartContains with
        | None -> 0
        | Some marker ->
          lines |> Array.tryFindIndex (fun l -> l.Contains marker) |> Option.defaultValue 0
      let endIdx =
        match region.EndContains with
        | None -> lines.Length
        | Some marker ->
          lines.[startIdx + 1 ..]
          |> Array.tryFindIndex (fun l -> l.Contains marker)
          |> Option.map (fun i -> startIdx + 1 + i)
          |> Option.defaultValue lines.Length
      [ for i in startIdx .. endIdx - 1 -> (i + 1, lines.[i]) ]

  /// Every edge a single source line matches, resolving the receiver
  /// through `receiverToNode` — never a hand-picked target.
  let private edgesInLine (region: ScanRegion) (lineNo: int) (line: string) : WaitForGraph.Edge list =
    [ if postAndReplyPattern.IsMatch line then
        let receiver = postAndReplyPattern.Match(line).Groups.[1].Value
        match Map.tryFind receiver receiverToNode with
        | Some target -> yield WaitForGraph.mkEdge region.Node target region.File lineNo (line.Trim())
        | None -> ()
      if ownerMethodPattern.IsMatch line then
        let receiver = ownerMethodPattern.Match(line).Groups.[1].Value
        match Map.tryFind receiver receiverToNode with
        | Some target -> yield WaitForGraph.mkEdge region.Node target region.File lineNo (line.Trim())
        | None -> ()
      if workerCallPattern.IsMatch line then
        yield WaitForGraph.mkEdge region.Node "Worker" region.File lineNo (line.Trim()) ]

  let extract (repoRoot: string) : WaitForGraph.Graph =
    let edges =
      scanRegions
      |> List.collect (fun region ->
        regionLines repoRoot region
        |> List.collect (fun (lineNo, line) -> edgesInLine region lineNo line))
    let declaredNodes = scanRegions |> List.map (fun r -> r.Node) |> Set.ofList
    let mentionedNodes = edges |> List.collect (fun e -> [ e.From; e.To ]) |> Set.ofList
    { Nodes = Set.union declaredNodes mentionedNodes
      Edges = edges }

[<Tests>]
let waitForGraphTests =
  testList "WaitForGraph" [

    testList "checker proof (RED phase — a fixture, not the real source)" [

      let cyclicFixture : WaitForGraph.Graph =
        { Nodes = Set.ofList [ "A"; "B"; "C" ]
          Edges =
            [ WaitForGraph.mkEdge "A" "B" "Fixture.fs" 1 "A blocks on B"
              WaitForGraph.mkEdge "B" "C" "Fixture.fs" 2 "B blocks on C"
              WaitForGraph.mkEdge "C" "A" "Fixture.fs" 3 "C blocks on A" ] }

      let acyclicFixture : WaitForGraph.Graph =
        { Nodes = Set.ofList [ "A"; "B"; "C" ]
          Edges =
            [ WaitForGraph.mkEdge "A" "B" "Fixture.fs" 1 "A blocks on B"
              WaitForGraph.mkEdge "B" "C" "Fixture.fs" 2 "B blocks on C" ] }

      testCase "a deliberate 3-cycle (A→B→C→A) is flagged, not silently accepted"
      <| fun _ ->
        cyclicFixture
        |> WaitForGraph.isAcyclic
        |> Expect.isFalse "A→B→C→A is a deadlock cycle — the checker must flag it"
        let cycle = WaitForGraph.findCycle cyclicFixture
        cycle
        |> Option.isSome
        |> Expect.isTrue "findCycle must actually report the participating edges, not just fail silently"
        cycle
        |> Option.get
        |> List.length
        |> Expect.equal "the reported cycle should name all 3 participating edges" 3

      testCase "an acyclic graph (A→B→C, no back-edge) passes"
      <| fun _ ->
        acyclicFixture
        |> WaitForGraph.isAcyclic
        |> Expect.isTrue "A→B→C with no back-edge is not a deadlock — the checker must not false-positive"
        acyclicFixture
        |> WaitForGraph.findCycle
        |> Expect.isNone "no cycle exists, so findCycle must report none"
    ]

    testList "extraction anti-vacuity (mirrors the Cohort wiring guards above)" [

      testCase "the extractor finds at least one actor node — a scan/path-resolution bug must not pass vacuously"
      <| fun _ ->
        let graph = ActorWaitForExtraction.extract repoRoot
        graph.Nodes
        |> Set.isEmpty
        |> Expect.isFalse
          "extraction found zero actor nodes — source scanning is broken (wrong repoRoot / renamed files), not proof the architecture has no actors"

      testCase "the extractor finds at least one blocking call-site — a scan bug must not pass vacuously as 'acyclic'"
      <| fun _ ->
        let graph = ActorWaitForExtraction.extract repoRoot
        graph.Edges
        |> List.isEmpty
        |> Expect.isFalse
          "extraction found zero edges — an empty graph is trivially 'acyclic', which is exactly the silent-degrade failure mode this guard exists to catch"

      testCase "the extractor finds the known-real CohortOwner -> LandingCacheOwner .Verify edge"
      <| fun _ ->
        // Confirmed by reading the code: DaemonMode.fs's `cohortLandingPerformer`
        // (CohortOwner's own RunTests effect performer) calls
        // `cohortLandingCacheOwner.Verify inputHashOf sessionId liveTests runMisses`
        // and awaits it — CohortOwner.fs's `LandingCacheOwner.Handle.Verify` is a
        // `mailbox.PostAndAsyncReply` round-trip. A scanner that cannot find THIS
        // specific, hand-verified edge has regressed, independent of whatever
        // else it does or doesn't find.
        let graph = ActorWaitForExtraction.extract repoRoot
        graph.Edges
        |> List.exists (fun e ->
          e.From = "CohortOwner"
          && e.To = "LandingCacheOwner"
          && e.File = "SageFs/DaemonMode.fs"
          && e.Snippet.Contains "cohortLandingCacheOwner.Verify")
        |> Expect.isTrue
          "the cohortLandingCacheOwner.Verify call in DaemonMode.fs's cohortLandingPerformer must be extracted as a CohortOwner -> LandingCacheOwner edge"
    ]

    testCase "the real actor wait-for graph, extracted from the current tree, is acyclic"
    <| fun _ ->
      let graph = ActorWaitForExtraction.extract repoRoot
      match WaitForGraph.topoSort graph with
      | Ok _ -> ()
      | Error _ ->
        let cycle = WaitForGraph.findCycle graph |> Option.defaultValue []
        failwith (
          sprintf
            "DEADLOCK: the actor wait-for graph has a cycle — every actor on this path blocks waiting for the next, forever:\n    %s"
            (WaitForGraph.describeCycle cycle)
        )
  ]

[<Tests>]
let fileSizeBudgets =
  // Ratchet guard (roast-8 §2 / §14 item 2): the "god files" the roasts keep
  // flagging must not keep growing. Budgets sit just above current size; when a
  // file is split, RATCHET THE BUDGET DOWN — never up. A failure here means
  // "split before you add," not "raise the number."
  let repoRoot = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..")
  let budgets =
    [ // 4200 -> 4270: a one-time bump for the F16/F5/F6 cohort-integration
      // bootstrap fix (cohort-dogfood-findings.md) — main-repo-root
      // resolution now reads the caller's own session instead of the
      // daemon's cwd, and a worktree build now runs before session
      // creation, fail-fast on failure. A deliberate, reviewed fix, not
      // silent accretion. Ratchet back DOWN when this file is split; never
      // bump to paper over drift.
      "SageFs/Mcp.fs", 4257
      // 850 -> 830: ratcheted DOWN (never up) after moving the
      // session-path-containment validator (resolveRealSessionPath/
      // isUncPath/validateSessionCreateRequest) out into its own
      // SessionPathValidation.fs module, shared by Mcp.fs's create_session
      // MCP tool and McpServer.fs's /api/sessions/create route (one rule,
      // one implementation — sagefs-roast.md Finding #1/#13). File dropped
      // to 821 lines; budget set just above that, not left at the old
      // ceiling.
      "SageFs/McpAdapter.fs", 830
      // 5100 -> 5160: a one-time bump for the live-testing-asyoutype-plan.md
      // Brief 4 keystone (EvalThenRunRequest, TestCycleEffect.
      // EvalBufferThenRunAffected, TestCycleEffects.redirectToEvalBuffer,
      // and its handleFcsResult wiring) — a deliberate, reviewed feature,
      // not silent accretion. Ratchet back DOWN when this file is split;
      // never bump to paper over drift.
      // 5160 -> 4425: the split this entry asked for. The inline-feedback read
      // model (annotations, code lenses, coverage view, run explainer, session
      // invariants — ~800 lines of editor PRESENTATION, referenced by nothing
      // in the FSI host's embedded source closure) moved to
      // Features/TestAnnotations.fs. Set to the file's exact post-split size:
      // the next addition earns a reviewed bump rather than inheriting slack.
      // 4425 -> 4380: the requested-run identity (RunRequestId, RequestedRun,
      // LiveTestState.RunRequests/ResultGenerations) had to live beside
      // LiveTestState; its lifecycle module (Features/RequestedRuns.fs) and the
      // unrelated dashboard treemap projection (Features/TestTreemap.fs) moved
      // out, so the file SHRANK. Exact post-split size, per the rule above.
      "SageFs.Core/Features/LiveTestingTypes.fs", 4380
      // 2900 -> 2950: a one-time bump for the roast UX-6 keystone (per-session
      // live-testing enable/disable — EnableLiveTestingForSession /
      // DisableLiveTestingForSession, resolveOrCreateLiveTestingTarget) — a
      // deliberate, reviewed feature, not silent accretion. Ratchet back DOWN
      // when this file is split; never bump to paper over drift.
      // 2950 -> 3060: a one-time bump for the live-testing-asyoutype-plan.md
      // Brief 4 keystone (TuiEvent.LiveDiscoveryMerged handler and the
      // EvalBufferThenRunAffected effect interpreter — the daemon-side call
      // into WorkerMessage.EvalLiveTestFile) — a deliberate, reviewed
      // feature, not silent accretion. Ratchet back DOWN when this file is
      // split; never bump to paper over drift.
      // 3060 -> 3130: a one-time bump for wiring QuarantineLogic into
      // production (sagefs-roast.md: the module was complete, correct, and
      // property-tested with ZERO non-test callers). evaluateQuarantineForBatch
      // now folds every result's flaky classification through
      // QuarantineLogic.evaluate/apply in the same fold that already updates
      // FlakyHistory, and AffectedTestsComputed excludes a quarantined test
      // from the next RunAffectedTests selection — a deliberate, reviewed
      // correctness fix, not silent accretion. Ratchet back DOWN when this
      // file is split; never bump to paper over drift.
      // 3130 -> 3124: requested-run wiring (RunTestsRequested allocates once,
      // TestRunStartedAt, result stamping) was paid for by splitting the
      // dispatch-batch reducer out to SageFsDispatchReduction.fs. Exact size.
      "SageFs/SageFsApp.fs", 3124
      "SageFs.Core/AppState.fs", 2000
      // 1850 -> 1860: a one-time bump for the #82 app-output routing (the
      // WorkerAppOutput command + the kept-alive stdout reader) — a deliberate,
      // reviewed feature, not silent accretion. Ratchet back DOWN when
      // SessionManager is split; never bump to paper over drift.
      "SageFs.Core/SessionManager.fs", 1860 ]
  testList "Architecture — file-size budgets (ratchet down, never raise)" [
    for (rel, budget) in budgets ->
      testCase (sprintf "WHY — %s stays within its line budget, so the accretion hub can't silently keep growing" rel) <| fun _ ->
        let path = System.IO.Path.Combine(repoRoot, rel)
        let lines = System.IO.File.ReadAllLines(path).Length
        (lines <= budget)
        |> Expect.isTrue
          (sprintf "%s is %d lines, over its %d budget — split it (and ratchet the budget DOWN), never raise the budget" rel lines budget)
  ]

[<Tests>]
let blockingCallBudgets =
  // Ratchet guard (roast-8 §9 / §14 item 5): blocking calls in test bodies —
  // Async.RunSynchronously, Thread.Sleep poll loops, .Wait(, and
  // GetAwaiter().GetResult() — starve the thread pool and make the suite time
  // out. The repo bans them; genuinely-justified sleeps (real timer tests,
  // signal-wait helpers) are the documented exceptions the current counts
  // already fold in. These budgets FREEZE the current debt at exactly its
  // present level: any NEW blocking call fails the build. When a test is
  // converted to testTask/testAsync + awaitable conditions, RATCHET THE BUDGET
  // DOWN — never up. A failure here means "convert, don't add."
  let testsRoot = __SOURCE_DIRECTORY__
  // Every test source file EXCEPT this one (it names the patterns as string
  // literals below, which would otherwise count itself) and generated bin/obj.
  let sourceFiles =
    System.IO.Directory.GetFiles(testsRoot, "*.fs", System.IO.SearchOption.AllDirectories)
    |> Array.filter (fun p ->
      let n = p.Replace('\\', '/')
      not (n.Contains "/bin/")
      && not (n.Contains "/obj/")
      && not (n.EndsWith "ArchitectureTests.fs"))
  let countPattern (pattern: string) =
    sourceFiles
    |> Array.sumBy (fun f ->
      System.IO.File.ReadAllLines f
      |> Array.filter (fun line -> line.Contains pattern)
      |> Array.length)
  // pattern, current frozen count. Ratchet DOWN as tests are converted.
  let budgets =
    [ "Async.RunSynchronously", 44
      "Thread.Sleep", 42
      ".Wait(", 23
      "GetAwaiter().GetResult()", 24 ]
  testList "Architecture — blocking-call budgets (ratchet down, never raise)" [
    for (pattern, budget) in budgets ->
      testCase (sprintf "WHY — test bodies keep '%s' at or below %d, so the thread-pool-starving blocking-call debt can only shrink" pattern budget) <| fun _ ->
        let actual = countPattern pattern
        (actual <= budget)
        |> Expect.isTrue
          (sprintf "'%s' now appears on %d test lines, over the %d budget — convert a test to testTask/testAsync + awaitable conditions (and ratchet the budget DOWN), never raise it" pattern actual budget)
  ]

[<Tests>]
let integrationSampleBuildCoverage =
  // A host-integration suite that creates a real session on a sample project
  // needs that sample BUILT before the suite runs — ci-pipeline.fsx's
  // "build samples for integration suites" stage does it.
  //
  // Forgetting an entry there does not fail loudly. Warmup cannot find the
  // DLL, the session faults, and the suite reports "session should reach
  // Ready ... Actual value was false" — which reads like a product bug, not a
  // missing build line. That cost a full CI cycle (stage #10, 6 errored) when
  // McpAppRunOutcomeTests began sessioning on SageFs.Samples.ConsoleTicker
  // while the stage still listed only WebappDatastar and FromCSharp.
  //
  // Deliberately conservative: any `*.fsproj` basename starting with
  // "SageFs.Samples." named anywhere in a test file that registers a HOST
  // integration suite must appear in ci-pipeline.fsx. A false positive costs
  // one build line; a false negative costs a CI cycle, so it fails toward
  // building. Path.Combine-assembled paths are covered too, because the
  // `.fsproj` basename is a literal either way.
  let pipelineText =
    let path = System.IO.Path.Combine(repoRoot, "ci-pipeline.fsx")
    match System.IO.File.Exists path with
    | true -> System.IO.File.ReadAllText path
    | false -> ""

  /// Test sources that register a real-session host-integration suite.
  let hostIntegrationTestFiles () =
    let dir = System.IO.Path.Combine(repoRoot, "SageFs.Tests")
    match System.IO.Directory.Exists dir with
    | false -> []
    | true ->
      System.IO.Directory.GetFiles(dir, "*.fs", System.IO.SearchOption.TopDirectoryOnly)
      |> Array.filter (fun p -> System.IO.File.ReadAllText(p).Contains "Integration.hostList")
      |> Array.toList

  let sampleProjectsNamedIn (path: string) =
    System.Text.RegularExpressions.Regex.Matches(
      System.IO.File.ReadAllText path,
      @"SageFs\.Samples\.[A-Za-z0-9.]+\.fsproj")
    |> Seq.map (fun m -> m.Value)
    |> Seq.distinct
    |> Seq.toList

  testList "Architecture — CI builds every sample an integration suite sessions on" [
    for file in hostIntegrationTestFiles () do
      for sample in sampleProjectsNamedIn file do
        let testFile = System.IO.Path.GetFileName file
        testCase
          (sprintf
            "WHY — %s names %s, so ci-pipeline.fsx must build it or the session silently faults instead of reaching Ready"
            testFile sample)
        <| fun _ ->
          pipelineText.Contains sample
          |> Expect.isTrue
            (sprintf
              "%s creates a host-integration session on %s, but ci-pipeline.fsx's \"build samples for integration suites\" stage never builds it. An unbuilt sample makes warmup fault with \"Not all DLLs are found\" and the suite reports that the session never reached Ready. Add: run \"dotnet build samples/**/%s -c Release --nologo\""
              testFile sample sample)
  ]
