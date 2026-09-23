module SageFs.Tests.IsolatedFsiSessionTests

open System
open Expecto
open Expecto.Flip
open SageFs.IsolatedFsiSession

[<Tests>]
let tests =
  testList "IsolatedFsiSession" [
    // #142: AppContext.BaseDirectory and Assembly.Location inside a session point at the isolated host's own
    // directory (or, worse, at the worker's shadow-copy temp dir), never the project's build output.
    // primaryProjectOutputDir is the pure-over-filesystem-primitives lookup that finds the primary
    // project's real output dir straight from disk — never from the -r: references FSI was given, since
    // those are already rewritten to point at the shadow copy by the time solutionToFsiArgs runs (proven
    // live: an earlier fsiArgs-based version of this function reported the shadow-copy directory, exactly
    // reproducing #142's own Assembly.Location bug for AppContext.BaseDirectory too).
    testList "primaryProjectOutputDir" [
      test "WHY — finds the primary project's directory straight from disk, because the -r: references FSI was given are already rewritten to a shadow-copy temp dir by the time solutionToFsiArgs runs" {
        let projects = [ "/repo/App/App.fsproj" ]
        let files = Map.ofList [ ("/repo/App/bin", "App.dll"), [ "/repo/App/bin/Release/net10.0/App.dll" ] ]
        let matchingFiles dir pattern = files |> Map.tryFind (dir, pattern) |> Option.defaultValue []
        let writeTime _ = DateTime(2026, 1, 1)
        primaryProjectOutputDirWith (fun _ -> true) matchingFiles writeTime projects
        |> Expect.equal "the primary project's own output directory" (Some "/repo/App/bin/Release/net10.0")
      }

      test "uses the FIRST project, not a transitive reference" {
        let projects = [ "/repo/App/App.fsproj"; "/repo/Core/Core.fsproj" ]
        let matchingFiles dir pattern =
          match dir, pattern with
          | "/repo/App/bin", "App.dll" -> [ "/repo/App/bin/Release/net10.0/App.dll" ]
          | _ -> failtest "should never look up a transitive reference's own bin dir"
        primaryProjectOutputDirWith (fun _ -> true) matchingFiles (fun _ -> DateTime.UtcNow) projects
        |> Expect.equal "App, the first requested project, wins over Core" (Some "/repo/App/bin/Release/net10.0")
      }

      test "None when no project is requested" {
        primaryProjectOutputDirWith (fun _ -> true) (fun _ _ -> [ "/x" ]) (fun _ -> DateTime.UtcNow) []
        |> Expect.isNone "nothing to find a directory for"
      }

      test "None when the project's bin/ directory doesn't exist yet (never built)" {
        primaryProjectOutputDirWith (fun _ -> false) (fun _ _ -> failtest "must not enumerate a bin/ that doesn't exist") (fun _ -> DateTime.UtcNow) [ "/repo/App/App.fsproj" ]
        |> Expect.isNone "not built yet"
      }

      test "None when no matching dll is found under bin/ (never guess a path)" {
        primaryProjectOutputDirWith (fun _ -> true) (fun _ _ -> []) (fun _ -> DateTime.UtcNow) [ "/repo/App/App.fsproj" ]
        |> Expect.isNone "never guess a path App.dll wasn't found at"
      }

      test "the NEWEST match wins, mirroring resolveFreshestConfigOutput's own rule (the code you built is the code that's reported)" {
        let matchingFiles _ _ =
          [ "/repo/App/bin/Debug/net10.0/App.dll"; "/repo/App/bin/Release/net10.0/App.dll" ]
        let writeTime path =
          if path = "/repo/App/bin/Release/net10.0/App.dll" then DateTime(2026, 6, 1) else DateTime(2026, 1, 1)
        primaryProjectOutputDirWith (fun _ -> true) matchingFiles writeTime [ "/repo/App/App.fsproj" ]
        |> Expect.equal "the newer Release build wins over the stale Debug one" (Some "/repo/App/bin/Release/net10.0")
      }
    ]

    // #141: the project's own FSharp.Core, when it's a different BUILD from the host's (identical version,
    // different members), silently loses to the host's already-loaded copy. Detecting this at warmup and
    // reporting it beats waiting for MissingMethodException from a method the loaded copy happens to lack.
    testList "detectFSharpCoreMismatchWith" [
      test "WHY — file size differs even when file version and AssemblyVersion match, because that's exactly what #141 found between the SDK toolset copy and a NuGet-restored copy" {
        let lengths = Map.ofList [ "/host/FSharp.Core.dll", 4208424L; "/proj/FSharp.Core.dll", 2405672L ]
        detectFSharpCoreMismatchWith (fun p -> lengths.TryFind p) "/host/FSharp.Core.dll" "/proj/FSharp.Core.dll"
        |> Expect.equal
             "reports both copies and both sizes"
             (Some
               { HostCopy = "/host/FSharp.Core.dll"
                 HostSizeBytes = 4208424L
                 ProjectCopy = "/proj/FSharp.Core.dll"
                 ProjectSizeBytes = 2405672L })
      }

      test "None when both copies agree on size" {
        let lengths = Map.ofList [ "/host/FSharp.Core.dll", 4208424L; "/proj/FSharp.Core.dll", 4208424L ]
        detectFSharpCoreMismatchWith (fun p -> lengths.TryFind p) "/host/FSharp.Core.dll" "/proj/FSharp.Core.dll"
        |> Expect.isNone "same size is not proof of a mismatch"
      }

      test "None (never a false positive) when a length can't be read" {
        detectFSharpCoreMismatchWith (fun _ -> None) "/host/FSharp.Core.dll" "/proj/FSharp.Core.dll"
        |> Expect.isNone "an unreadable file means unproven, not mismatched"
      }
    ]

    testList "describeFSharpCoreMismatch" [
      test "names both paths and sizes, and points at the known symptom" {
        let m =
          { HostCopy = "/host/FSharp.Core.dll"
            HostSizeBytes = 4208424L
            ProjectCopy = "/proj/FSharp.Core.dll"
            ProjectSizeBytes = 2405672L }
        let text = describeFSharpCoreMismatch m
        text |> Expect.stringContains "host path" "/host/FSharp.Core.dll"
        text |> Expect.stringContains "project path" "/proj/FSharp.Core.dll"
        text |> Expect.stringContains "known symptom" "MissingMethodException"
      }
    ]

    testList "ProjectOutputEnvironmentVariable" [
      test "is the name the issue itself suggested" {
        ProjectOutputEnvironmentVariable |> Expect.equal "SAGEFS_PROJECT_OUTPUT, verbatim" "SAGEFS_PROJECT_OUTPUT"
      }
    ]

    // #141 follow-up: the IL-level identity rewrite (see ProjectFSharpCoreIdentity's own doc comment for
    // why it isn't wired into `start` yet). rewriteReferenceWith is the one piece proven correct in
    // isolation — these tests lock that proof in against a REAL compiled assembly, not a synthetic one,
    // so the primitive stays trustworthy for whoever picks up centralizing it inside ShadowCopy.
    //
    // Regression coverage for the live-testing discovery bug this same primitive caused when it rewrote
    // the WHOLE "FSharp.Core" reference unconditionally: a project calling into a third party (Expecto)
    // whose own API surface mentions FSharp.Core types got that call's signature retargeted too, even
    // though nothing was actually missing on that call — see the production doc comment for the full
    // live-verified story. These tests lock in the CURRENT contract: only call sites naming a member in
    // `missingFromHost` move; everything else, including the reference itself when nothing qualifies,
    // stays exactly as it was.
    testList "ProjectFSharpCoreIdentity.rewriteReferenceWith" [
      let readAssembly (bytes: byte[]) = Mono.Cecil.AssemblyDefinition.ReadAssembly(new System.IO.MemoryStream(bytes))

      // This very test assembly is a real, F#-compiled assembly that references FSharp.Core — no need for
      // a synthetic input. Finds the FIRST real call site whose declaring type is FSharp.Core, so the
      // "exactly one member redirected" tests below exercise a signature that genuinely exists in this
      // DLL's IL, not a guessed one.
      let testAssemblyBytes () = System.IO.File.ReadAllBytes(System.Reflection.Assembly.GetExecutingAssembly().Location)

      // Recurses into NestedTypes — Cecil's own `ModuleDefinition.Types` lists top-level types only, and
      // real call sites this primitive must retarget routinely live several levels of nested compiler-
      // generated closures deep (see the "exactly the shape #141 lives in" test below for why that isn't
      // hypothetical).
      let rec allTypes (t: Mono.Cecil.TypeDefinition) : Mono.Cecil.TypeDefinition seq =
        seq {
          yield t
          for nested in t.NestedTypes do
            yield! allTypes nested
        }

      let firstFSharpCoreCallKey (bytes: byte[]) : string =
        use asm = readAssembly bytes
        let original =
          asm.MainModule.AssemblyReferences
          |> Seq.find (fun r -> r.Name = "FSharp.Core")
        asm.MainModule.Types
        |> Seq.collect allTypes
        |> Seq.collect (fun t -> t.Methods)
        |> Seq.filter (fun m -> m.HasBody)
        |> Seq.collect (fun m -> m.Body.Instructions)
        |> Seq.choose (fun instr ->
          match instr.Operand with
          | :? Mono.Cecil.GenericInstanceMethod as gim -> Some gim.ElementMethod
          | :? Mono.Cecil.MethodReference as mr when not (mr :? Mono.Cecil.MethodDefinition) -> Some mr
          | _ -> None)
        |> Seq.filter (fun mr -> obj.ReferenceEquals(mr.DeclaringType.Scope, original :> Mono.Cecil.IMetadataScope))
        |> Seq.map (fun mr ->
          sprintf "%s::%s(%s)" mr.DeclaringType.FullName mr.Name (mr.Parameters |> Seq.map (fun p -> p.ParameterType.FullName) |> String.concat ","))
        |> Seq.head

      test "WHY — an empty missingFromHost is a no-op, because nothing the project has that the host lacks means no call site could possibly need redirecting" {
        let originalBytes = testAssemblyBytes ()
        ProjectFSharpCoreIdentity.rewriteReferenceWith readAssembly Set.empty originalBytes
        |> Expect.isNone "no missing members means nothing to rewrite, not a wholesale rename"
      }

      test "WHY — retargets ONLY the call site(s) naming a member in missingFromHost, leaving the original FSharp.Core reference in place for everything else" {
        let originalBytes = testAssemblyBytes ()
        let key = firstFSharpCoreCallKey originalBytes
        match ProjectFSharpCoreIdentity.rewriteReferenceWith readAssembly (Set.singleton key) originalBytes with
        | None -> failtest "a missingFromHost set naming a real call site in this assembly must produce a rewrite"
        | Some rewrittenBytes ->
          use rewritten = readAssembly rewrittenBytes
          let stillReferencesOriginal =
            rewritten.MainModule.AssemblyReferences
            |> Seq.exists (fun r -> r.Name = "FSharp.Core")
          let referencesRenamed =
            rewritten.MainModule.AssemblyReferences
            |> Seq.exists (fun r -> r.Name = ProjectFSharpCoreIdentity.RewrittenName)
          stillReferencesOriginal
          |> Expect.isTrue "the original reference must survive — every OTHER FSharp.Core call (and any third party's own FSharp.Core-typed signatures) still needs it"
          referencesRenamed |> Expect.isTrue "the renamed reference must be present for the one redirected call"
      }

      // Regression coverage for the exact bug live-verified in the real #141/#142 gate: F#'s resumable-code
      // desugaring of `task { use r = ... }` does NOT put the call to `TaskBuilderBase.Using` on the
      // enclosing function's own top-level method — it puts it on the `Invoke` method of a compiler-
      // generated closure class NESTED inside the containing type (one "Pipe #N input at line L" class per
      // CE step). `Mono.Cecil.ModuleDefinition.Types` lists top-level types only; a walk that doesn't
      // recurse into `NestedTypes` finds zero FSharp.Core call sites for this shape and silently returns
      // `None` — which is exactly what #141 itself looks like when it's broken (a session that used to
      // compute the right value starts throwing MissingMethodException again). Built with Cecil rather
      // than a real compiled fixture so this test needs no separately-built Debug output to exist on disk.
      test "WHY — finds and retargets a call site inside a NESTED type, because that is where F#'s resumable-code desugaring actually places TaskBuilderBase.Using" {
        let asmName = Mono.Cecil.AssemblyNameDefinition("NestedCallSiteProbe", System.Version(1, 0, 0, 0))
        let assembly = Mono.Cecil.AssemblyDefinition.CreateAssembly(asmName, "NestedCallSiteProbe", Mono.Cecil.ModuleKind.Dll)
        let modul = assembly.MainModule
        let fsharpCoreRef = Mono.Cecil.AssemblyNameReference("FSharp.Core", System.Version(11, 0, 0, 0))
        modul.AssemblyReferences.Add fsharpCoreRef
        let builderType = Mono.Cecil.TypeReference("Microsoft.FSharp.Control", "TaskBuilderBase", modul, fsharpCoreRef :> Mono.Cecil.IMetadataScope)
        let usingMethod =
          Mono.Cecil.MethodReference("Using", modul.TypeSystem.Object, builderType)
        usingMethod.Parameters.Add(Mono.Cecil.ParameterDefinition(modul.TypeSystem.Object))

        let outer =
          Mono.Cecil.TypeDefinition("NestedCallSiteProbe", "Outer", Mono.Cecil.TypeAttributes.Public ||| Mono.Cecil.TypeAttributes.Class, modul.TypeSystem.Object)
        modul.Types.Add outer
        let nested =
          Mono.Cecil.TypeDefinition("", "Inner", Mono.Cecil.TypeAttributes.NestedPublic ||| Mono.Cecil.TypeAttributes.Class, modul.TypeSystem.Object)
        outer.NestedTypes.Add nested
        let invoke =
          Mono.Cecil.MethodDefinition("Invoke", Mono.Cecil.MethodAttributes.Public, modul.TypeSystem.Object)
        nested.Methods.Add invoke
        let il = invoke.Body.GetILProcessor()
        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldnull))
        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldnull))
        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Call, usingMethod))
        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret))

        use output = new System.IO.MemoryStream()
        assembly.Write output
        let originalBytes = output.ToArray()

        let missingFromHost = Set.singleton "Microsoft.FSharp.Control.TaskBuilderBase::Using(System.Object)"
        match ProjectFSharpCoreIdentity.rewriteReferenceWith readAssembly missingFromHost originalBytes with
        | None -> failtest "a call site inside a NestedType must still be found and retargeted"
        | Some rewrittenBytes ->
          use rewritten = readAssembly rewrittenBytes
          let innerType = rewritten.MainModule.Types |> Seq.find (fun t -> t.Name = "Outer") |> fun t -> t.NestedTypes |> Seq.find (fun n -> n.Name = "Inner")
          let invokeMethod = innerType.Methods |> Seq.find (fun m -> m.Name = "Invoke")
          let callTarget =
            invokeMethod.Body.Instructions
            |> Seq.pick (fun i -> match i.Operand with :? Mono.Cecil.MethodReference as mr -> Some mr | _ -> None)
          callTarget.DeclaringType.Scope.Name
          |> Expect.equal "the nested type's own call site must be retargeted to the renamed identity" ProjectFSharpCoreIdentity.RewrittenName
      }

      test "None for an assembly with no FSharp.Core reference at all (a plain CLR assembly)" {
        let corlibPath = typeof<obj>.Assembly.Location
        let bytes = System.IO.File.ReadAllBytes corlibPath
        ProjectFSharpCoreIdentity.rewriteReferenceWith readAssembly (Set.singleton "irrelevant::key()") bytes
        |> Expect.isNone "System.Private.CoreLib has no FSharp.Core reference to rewrite"
      }
    ]
  ]
