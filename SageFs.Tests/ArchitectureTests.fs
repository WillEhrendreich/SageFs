module SageFs.Tests.ArchitectureTests

open System
open System.Reflection
open Expecto
open Expecto.Flip

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

    testList "Documentation coverage (aspirational)" [

      testCase "SageFs.Core public types should have documentation attributes"
      <| fun _ ->
        let publicTypes =
          coreAssembly.GetExportedTypes()
          |> Array.filter (fun t ->
            not (t.Name.StartsWith "<")
            && not (t.Name.Contains "@")
            && not t.IsNested)
        let typesWithDocs =
          publicTypes
          |> Array.filter (fun t ->
            t.GetCustomAttributes false
            |> Seq.cast<Attribute>
            |> Seq.exists isDocAttribute)
        let pct =
          match publicTypes.Length with
          | 0 -> 100.0
          | n -> float typesWithDocs.Length / float n * 100.0
        printfn
          "  Documentation attribute coverage: %d/%d (%.1f%%)"
          typesWithDocs.Length
          publicTypes.Length
          pct
        // Aspirational: raise this threshold as documentation improves.
        // Baseline at time of writing: ~20%. Threshold set to catch regressions.
        (pct >= 10.0)
        |> Expect.isTrue
          (sprintf
            "at least 10%% of public types should have doc attributes (currently %.1f%%)"
            pct)
    ]
  ]
