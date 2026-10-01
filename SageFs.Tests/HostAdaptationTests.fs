module SageFs.Tests.HostAdaptationTests

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.HostAdaptation

let private v (text: string) = Version.Parse text

let private reference (owner: string) (name: string) (version: string) : ReferencedAssembly =
  { Name = name
    Version = v version
    Path = sprintf "/pkgs/%s/%s/lib/%s.dll" (name.ToLowerInvariant()) version name
    Owner = owner
    Source = sprintf "%s %s" name version }

/// The host has these versions of what it carries.
let private host (versions: (string * string) list) (name: string) : Version option =
  versions |> List.tryFind (fun (n, _) -> n = name) |> Option.map (snd >> v)

let private newestWins (names: string list) (name: string) = List.contains name names

[<Tests>]
let tests =
  testList "HostAdaptation" [

    testList "plan: a library whose newest version is safe to run" [
      testCase "a project's newer FSharp.Core than the host's becomes an override" <| fun _ ->
        let plan =
          [ reference "App.fsproj" "FSharp.Core" "11.0.0.0" ]
          |> HostAdaptation.plan (newestWins [ "FSharp.Core" ]) (host [ "FSharp.Core", "10.1.0.0" ])
        plan.Overrides
        |> List.map (fun o -> o.Name, o.Version)
        |> Expect.equal "the project's FSharp.Core replaces the host's" [ "FSharp.Core", v "11.0.0.0" ]
        plan.Conflicts |> Expect.isEmpty "two versions of FSharp.Core are not a conflict"

      testCase "the same or an older version than the host's changes nothing" <| fun _ ->
        for version in [ "10.1.0.0"; "9.0.0.0" ] do
          let plan =
            [ reference "App.fsproj" "FSharp.Core" version ]
            |> HostAdaptation.plan (newestWins [ "FSharp.Core" ]) (host [ "FSharp.Core", "10.1.0.0" ])
          plan.Overrides |> Expect.isEmpty (sprintf "the host's own copy already answers for %s" version)

      testCase "a shared-framework assembly pinned newer than the framework's is overridden" <| fun _ ->
        let plan =
          [ reference "App.fsproj" "System.Text.Json" "99.0.0.0" ]
          |> HostAdaptation.plan (newestWins [ "System.Text.Json" ]) (host [ "System.Text.Json", "11.0.0.0" ])
        plan.Overrides
        |> List.map (fun o -> o.Name)
        |> Expect.equal "the pinned System.Text.Json is loaded in the host" [ "System.Text.Json" ]

      testCase "of several versions the newest one is the override, and it is one override" <| fun _ ->
        let plan =
          [ reference "A.fsproj" "FSharp.Core" "9.0.0.0"
            reference "B.fsproj" "FSharp.Core" "11.0.0.0"
            reference "C.fsproj" "FSharp.Core" "10.0.0.0" ]
          |> HostAdaptation.plan (newestWins [ "FSharp.Core" ]) (host [ "FSharp.Core", "8.0.0.0" ])
        plan.Overrides |> List.map (fun o -> o.Version) |> Expect.equal "only the newest" [ v "11.0.0.0" ]

      testCase "a library the host does not carry is never overridden, even when it is on the newest-wins list" <| fun _ ->
        let plan =
          [ reference "App.fsproj" "System.Foo" "2.0.0.0" ]
          |> HostAdaptation.plan (newestWins [ "System.Foo" ]) (host [])
        plan.Overrides |> Expect.isEmpty "nothing of the host's to replace"
    ]

    testList "plan: a library that is not safe to run at the newest version" [
      testCase "two projects pinning different assembly versions are a conflict that names both" <| fun _ ->
        let plan =
          [ reference "A.fsproj" "PinLib" "1.0.0.0"; reference "B.fsproj" "PinLib" "2.0.0.0" ]
          |> HostAdaptation.plan (newestWins []) (host [])
        match plan.Conflicts with
        | [ conflict ] ->
          conflict.Assembly |> Expect.equal "the assembly" "PinLib"
          conflict.Pins |> List.map (fun p -> p.Owner) |> List.sort |> Expect.equal "both projects" [ "A.fsproj"; "B.fsproj" ]
        | other -> failtestf "expected one conflict, got %A" other
        plan.Overrides |> Expect.isEmpty "a conflict is never settled by picking one"

      testCase "the same assembly version from two projects is not a conflict" <| fun _ ->
        let plan =
          [ reference "A.fsproj" "PinLib" "1.0.0.0"; reference "B.fsproj" "PinLib" "1.0.0.0" ]
          |> HostAdaptation.plan (newestWins []) (host [])
        plan.Conflicts |> Expect.isEmpty "one identity"

      testCase "the names compare without regard to case" <| fun _ ->
        let plan =
          [ reference "A.fsproj" "PinLib" "1.0.0.0"; reference "B.fsproj" "pinlib" "2.0.0.0" ]
          |> HostAdaptation.plan (newestWins []) (host [])
        plan.Conflicts |> List.length |> Expect.equal "same assembly, two versions" 1

      testCase "describeConflicts names the assembly, each version, each package and each project, and says what to do" <| fun _ ->
        let plan =
          [ reference "A.fsproj" "PinLib" "1.0.0.0"; reference "B.fsproj" "PinLib" "2.0.0.0" ]
          |> HostAdaptation.plan (newestWins []) (host [])
        let text = describeConflicts plan.Conflicts
        for fact in [ "PinLib"; "1.0.0"; "2.0.0"; "A.fsproj"; "B.fsproj"; "one session per project" ] do
          text |> Expect.stringContains (sprintf "names %s" fact) fact
    ]

    testList "the additional deps file" [
      testCase "lists each override as a package whose asset is the file itself, under the host's target framework" <| fun _ ->
        let overrides : Override list =
          [ { Name = "System.Text.Json"; Version = v "99.0.0.0"; Path = "/pkgs/stj/lib/System.Text.Json.dll" } ]
        use doc = JsonDocument.Parse(additionalDepsJson 10 overrides)
        let targetName = doc.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()
        targetName |> Expect.equal "the host's target framework" ".NETCoreApp,Version=v10.0"
        let target = doc.RootElement.GetProperty("targets").GetProperty targetName
        let entry = target.GetProperty "System.Text.Json/99.0.0.0"
        let asset = entry.GetProperty("runtime").GetProperty "System.Text.Json.dll"
        asset.GetProperty("assemblyVersion").GetString() |> Expect.equal "the version the runtime compares" "99.0.0.0"
        let library = doc.RootElement.GetProperty("libraries").GetProperty "System.Text.Json/99.0.0.0"
        library.GetProperty("path").GetString() |> Expect.equal "the probing path itself is the folder" "."

      testCase "probingPaths is the folder of each override, once" <| fun _ ->
        let overrides : Override list =
          [ { Name = "A"; Version = v "1.0.0.0"; Path = "/x/A.dll" }
            { Name = "B"; Version = v "1.0.0.0"; Path = "/x/B.dll" }
            { Name = "C"; Version = v "1.0.0.0"; Path = "/y/C.dll" } ]
        probingPaths overrides |> List.sort |> Expect.equal "distinct folders" [ "/x"; "/y" ]

      testCase "muxerOptions puts one --additionalprobingpath per folder before the host dll" <| fun _ ->
        let overrides : Override list = [ { Name = "A"; Version = v "1.0.0.0"; Path = "/x/A.dll" } ]
        muxerOptions overrides |> Expect.equal "flag and value" [ "--additionalprobingpath"; "/x" ]

      testCase "no overrides add nothing to the launch" <| fun _ ->
        muxerOptions [] |> Expect.isEmpty "no flags"
        additionalDepsEnvironment "/tmp/x.deps.json" [] |> Expect.isEmpty "no variable"
    ]

    testList "targetFrameworkFindings" [
      testCase "a .NET Framework target framework is a refusal that names the project, the framework and the issue" <| fun _ ->
        let refusals, concerns = targetFrameworkFindings [ "/src/App/App.fsproj", "net48" ]
        concerns |> Expect.isEmpty "nothing else to say"
        match refusals with
        | [ refusal ] ->
          let text = describeRefusal refusal
          for fact in [ "App.fsproj"; "net48"; "135" ] do
            text |> Expect.stringContains (sprintf "names %s" fact) fact
        | other -> failtestf "expected one refusal, got %A" other

      testCase "a modern target framework says nothing, and neither does netstandard" <| fun _ ->
        for tfm in [ "net10.0"; "net8.0"; "netstandard2.0"; "net8.0-windows" ] do
          let refusals, concerns = targetFrameworkFindings [ "/src/App/App.fsproj", tfm ]
          refusals |> Expect.isEmpty (sprintf "no refusal for %s" tfm)
          concerns |> Expect.isEmpty (sprintf "no concern for %s" tfm)

      testCase "a moniker nobody recognises is a concern on that project, not a refusal" <| fun _ ->
        let refusals, concerns = targetFrameworkFindings [ "/src/App/App.fsproj", "uap10.0" ]
        refusals |> Expect.isEmpty "not refused"
        match concerns with
        | [ project, SageFs.ProjectLoading.HostConcern.TargetFrameworkUnrecognised(tfm, _) ] ->
          project |> Expect.equal "the project" "/src/App/App.fsproj"
          tfm |> Expect.equal "the moniker" "uap10.0"
        | other -> failtestf "expected one concern, got %A" other
    ]

    testList "runtimeConcerns" [
      let requirement : Result<SageFs.RuntimeCompat.RuntimeRequirement, SageFs.RuntimeCompat.RuntimeConfigError> =
        Result.Ok { Major = 10; Stability = SageFs.RuntimeCompat.Stable }
      testCase "a runtimeconfig that is absent is ordinary and says nothing" <| fun _ ->
        runtimeConcerns
          [ "/a/A.fsproj", Result.Error(SageFs.RuntimeCompat.ProjectNotBuilt "A.fsproj")
            "/b/B.fsproj", Result.Error(SageFs.RuntimeCompat.RuntimeConfigNotFound("B.runtimeconfig.json", "/b/bin"))
            "/c/C.fsproj", requirement ]
        |> Expect.isEmpty "a library never has a runtimeconfig"

      testCase "one that is there and cannot be understood is a concern on its project, with the reason" <| fun _ ->
        let concerns =
          runtimeConcerns
            [ "/a/A.fsproj", Result.Error(SageFs.RuntimeCompat.NotJsonRuntimeConfig "unexpected token")
              "/b/B.fsproj", requirement ]
        match concerns with
        | [ project, SageFs.ProjectLoading.HostConcern.RuntimeUndetermined reason ] ->
          project |> Expect.equal "the project" "/a/A.fsproj"
          reason |> Expect.stringContains "the reason" "unexpected token"
        | other -> failtestf "expected one concern, got %A" other
    ]

    testList "compilerServiceConcerns" [
      testCase "a project's newer FSharp.Compiler.Service than the host's is a concern on its owner, naming both versions and the SDK" <| fun _ ->
        let concerns =
          [ reference "/src/App/App.fsproj" "FSharp.Compiler.Service" "43.13.101.0" ]
          |> compilerServiceConcerns (v "43.12.401.0") "10.0.401"
        match concerns with
        | [ project, concern ] ->
          project |> Expect.equal "the owner" "/src/App/App.fsproj"
          let text = SageFs.ProjectLoading.HostConcern.describe concern
          for fact in [ "43.13.101"; "43.12.401"; "10.0.401"; "global.json" ] do
            text |> Expect.stringContains (sprintf "names %s" fact) fact
        | other -> failtestf "expected one concern, got %A" other

      testCase "the same or an older one changes nothing" <| fun _ ->
        for version in [ "43.12.401.0"; "43.10.100.0" ] do
          [ reference "/src/App/App.fsproj" "FSharp.Compiler.Service" version ]
          |> compilerServiceConcerns (v "43.12.401.0") "10.0.401"
          |> Expect.isEmpty (sprintf "%s is not newer" version)

      testCase "an unrelated library says nothing" <| fun _ ->
        [ reference "/src/App/App.fsproj" "Other" "99.0.0.0" ]
        |> compilerServiceConcerns (v "43.12.401.0") "10.0.401"
        |> Expect.isEmpty "not the compiler service"
    ]

    testList "the IO edge" [
      let sharedRoot () =
        let runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(System.IO.Path.DirectorySeparatorChar)
        System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName runtimeDir)

      testCase "frameworkAssemblies reads the running runtime's assemblies, versions included" <| fun _ ->
        let assemblies = frameworkAssemblies (sharedRoot ()) Environment.Version.Major
        assemblies.ContainsKey "System.Text.Json" |> Expect.isTrue "System.Text.Json is in the shared framework"
        assemblies["System.Text.Json"].Major |> Expect.equal "it is this runtime's" Environment.Version.Major
        assemblies.ContainsKey "System.Private.CoreLib" |> Expect.isTrue "so is the core library"

      testCase "a runtime that is not installed gives nothing, and asking again after installing it would see it" <| fun _ ->
        let assemblies = frameworkAssemblies (System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sagefs-no-dotnet-here")) 99
        assemblies.Count |> Expect.equal "nothing" 0

      testCase "assemblyVersionOf is the version of a managed file and nothing for any other file" <| fun _ ->
        assemblyVersionOf (typeof<obj>.Assembly.Location)
        |> Expect.isSome "System.Private.CoreLib is managed"
        let text = System.IO.Path.GetTempFileName()
        try
          System.IO.File.WriteAllText(text, "not an assembly")
          assemblyVersionOf text |> Expect.isNone "text is not an assembly"
        finally
          System.IO.File.Delete text
    ]

    testList "properties" [
      testProperty "no assembly is both overridden and in conflict, and a conflict never names fewer than two versions" <| fun (picks: (byte * byte * byte) list) ->
        let names = [| "FSharp.Core"; "System.Text.Json"; "PinLib"; "Other" |]
        let owners = [| "A.fsproj"; "B.fsproj"; "C.fsproj" |]
        let refs =
          picks
          |> List.map (fun (n, major, o) ->
            reference owners[int o % owners.Length] names[int n % names.Length] (sprintf "%d.0.0.0" (int major % 12 + 1)))
        let result =
          refs
          |> HostAdaptation.plan (newestWins [ "FSharp.Core"; "System.Text.Json" ]) (host [ "FSharp.Core", "5.0.0.0"; "System.Text.Json", "6.0.0.0" ])
        let overridden = result.Overrides |> List.map (fun o -> o.Name.ToLowerInvariant()) |> Set.ofList
        let conflicted = result.Conflicts |> List.map (fun c -> c.Assembly.ToLowerInvariant()) |> Set.ofList
        Set.isEmpty (Set.intersect overridden conflicted)
        && result.Conflicts |> List.forall (fun c -> List.length c.Pins >= 2)
    ]
  ]
