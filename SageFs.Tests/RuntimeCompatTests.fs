module SageFs.Tests.RuntimeCompatTests

open System
open Expecto
open Expecto.Flip
open SageFs.RuntimeCompat

let private net11 =
  """{"runtimeOptions":{"tfm":"net11.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"11.0.0-rc.1.26425.128"},{"name":"Microsoft.AspNetCore.App","version":"11.0.0-rc.1.26425.128"}]}}"""

let private net10 =
  """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}"""

let private frameworkDirs = [ "10.0.12"; "11.0.0-rc.1.26425.128"; "9.0.4" ]

/// Map an arbitrary byte onto a runtime major in 8..13.
let private majorOf (raw: byte) = 8 + int raw % 6

[<Tests>]
let tests =
  testList "RuntimeCompat" [
    testList "parseRuntimeRequirement" [
      testCase "reads the highest major across an ASP.NET project's frameworks and flags the preview" <| fun _ ->
        parseRuntimeRequirement net11
        |> Expect.equal "net11 rc" (Result.Ok { Major = 11; Stability = Prerelease })

      testCase "reads a single-framework stable runtimeconfig" <| fun _ ->
        parseRuntimeRequirement net10
        |> Expect.equal "net10" (Result.Ok { Major = 10; Stability = Stable })

      testCase "malformed json is a typed Error, never an exception" <| fun _ ->
        match parseRuntimeRequirement "{nope" with
        | Result.Error(NotJsonRuntimeConfig _) -> ()
        | other -> failtestf "expected NotJsonRuntimeConfig, got %A" other

      testCase "a runtimeconfig that names no framework is NoFrameworkVersion" <| fun _ ->
        parseRuntimeRequirement "{}" |> Expect.equal "no framework" (Result.Error NoFrameworkVersion)

      testCase "an unparseable framework version is named in the error" <| fun _ ->
        parseRuntimeRequirement """{"runtimeOptions":{"framework":{"name":"x","version":"banana"}}}"""
        |> Expect.equal "named" (Result.Error(UnrecognisedFrameworkVersion "banana"))
    ]

    testList "decide" [
      testCase "rolls forward when the project needs a newer runtime that is installed" <| fun _ ->
        decide 10 [ 10; 11 ] (parseRuntimeRequirement net11)
        |> Expect.equal "roll" (RollForward(11, Prerelease))

      testCase "reports the missing runtime when none new enough is installed" <| fun _ ->
        decide 10 [ 10 ] (parseRuntimeRequirement net11)
        |> Expect.equal "missing" (RuntimeMissing 11)

      testCase "leaves a project the host already satisfies alone" <| fun _ ->
        decide 10 [ 10; 11 ] (parseRuntimeRequirement net10)
        |> Expect.equal "fits" HostFits

      testCase "an unreadable requirement keeps the default launch and carries the reason" <| fun _ ->
        decide 10 [ 10 ] (Error(ProjectNotBuilt "Sample.fsproj"))
        |> Expect.equal "unknown" (Unknown(ProjectNotBuilt "Sample.fsproj"))

      testProperty "rolls forward only for a strictly newer requirement, and only then sets the environment"
      <| fun (hostRaw: byte) (installedRaw: byte list) (requiredRaw: byte) (isPreview: bool) (readable: bool) ->
        let host = majorOf hostRaw
        let installed = installedRaw |> List.map majorOf
        let stability = if isPreview then Prerelease else Stable
        let requirement =
          if readable then Result.Ok { Major = majorOf requiredRaw; Stability = stability } else Error NoFrameworkVersion
        let choice = decide host installed requirement
        let sets = not (List.isEmpty (rollForwardEnv choice))
        match choice, requirement with
        | RollForward(major, _), Result.Ok required -> sets && major = required.Major && required.Major > host
        | RollForward _, Error _ -> false
        | _ -> not sets
    ]

    testList "rollForwardEnv" [
      testCase "a preview requirement also allows prerelease runtimes" <| fun _ ->
        rollForwardEnv (RollForward(11, Prerelease))
        |> Expect.equal "env" [ "DOTNET_ROLL_FORWARD", "LatestMajor"; "DOTNET_ROLL_FORWARD_TO_PRERELEASE", "1" ]

      testCase "a stable requirement does not allow prerelease runtimes" <| fun _ ->
        rollForwardEnv (RollForward(11, Stable))
        |> Expect.equal "env" [ "DOTNET_ROLL_FORWARD", "LatestMajor" ]
    ]

    testList "describe" [
      testCase "a missing runtime names what to install and where" <| fun _ ->
        let text = describe (RuntimeMissing 11)
        Expect.stringContains "names the major" "11" text
        Expect.stringContains "says where to get it" "dotnet.microsoft.com/download" text
    ]

    testList "selectFrameworkDir" [
      testCase "a net10 worker gets the net10 directory even though net11 is installed" <| fun _ ->
        selectFrameworkDir (System.Version(10, 0, 12)) frameworkDirs |> Expect.equal "10" (Result.Ok "10.0.12")

      testCase "a worker rolled forward to net11 gets the net11 preview directory" <| fun _ ->
        selectFrameworkDir (System.Version(11, 0, 0)) frameworkDirs |> Expect.equal "11" (Result.Ok "11.0.0-rc.1.26425.128")

      testCase "a different patch of the same major falls back to the highest of that major" <| fun _ ->
        selectFrameworkDir (System.Version(10, 0, 3)) frameworkDirs |> Expect.equal "same major" (Result.Ok "10.0.12")

      testCase "no directory of the running major is an Error, not a guess from another major" <| fun _ ->
        selectFrameworkDir (System.Version(8, 0, 1)) frameworkDirs
        |> Expect.equal "typed error carries the major and what was available" (Result.Error(NoFrameworkForMajor(8, frameworkDirs)))

      testProperty "never returns a directory of a different major than the running runtime"
      <| fun (majorRaw: byte) (minor: byte) (patch: byte) ->
        let major = majorOf majorRaw
        match selectFrameworkDir (System.Version(major, int minor, int patch)) frameworkDirs with
        | Result.Ok name -> System.Version.Parse(name.Split('-').[0]).Major = major
        | Error _ -> true
    ]

    testList "SessionKinds (the isolated-FSI opt-in)" [
      testCase "the opt-in variable selects Isolated for 1 and true" <| fun _ ->
        for value in [ "1"; "true" ] do
          SageFs.SessionKinds.fromEnvironmentWith (fun _ -> value)
          |> Expect.equal (sprintf "'%s' opts in" value) SageFs.SessionKinds.Isolated

      testCase "unset selects InProcess, the default" <| fun _ ->
        SageFs.SessionKinds.fromEnvironmentWith (fun _ -> null)
        |> Expect.equal "default" SageFs.SessionKinds.InProcess

      testProperty "any other value stays InProcess: nothing opts in by accident"
      <| fun (value: string) ->
        match value with
        | "1"
        | "true" -> true
        | other -> SageFs.SessionKinds.fromEnvironmentWith (fun _ -> other) = SageFs.SessionKinds.InProcess

      testCase "an isolated worker's init loads nothing of the user's: it never touches the solution" <| fun _ ->
        // The in-process init loads every project's output assembly and installs a resolver over the user's package
        // directories. The isolated one must not: it is handed a null solution and must not care.
        let name, state = SageFs.Middleware.HotReloading.isolatedInitFunction Unchecked.defaultof<_>
        Expect.equal "same key" SageFs.Middleware.HotReloading.hotReloadKey name
        Expect.equal "the empty state" (box SageFs.Middleware.HotReloading.emptyReloadingState) state

      testCase "the worker's init functions follow the session kind" <| fun _ ->
        Expect.isTrue
          "in-process keeps the loading init"
          (obj.ReferenceEquals(SageFs.ActorCreation.initFunctionsFor SageFs.SessionKinds.InProcess, SageFs.ActorCreation.commonInitFunctions))
        Expect.hasLength "isolated has exactly the inert init" 1 (SageFs.ActorCreation.initFunctionsFor SageFs.SessionKinds.Isolated)

      testCase "the switch reads exactly the documented variable" <| fun _ ->
        let mutable asked = ""
        SageFs.SessionKinds.fromEnvironmentWith (fun name ->
          asked <- name
          null)
        |> ignore
        Expect.equal "variable name" SageFs.SessionKinds.EnvironmentVariable asked
    ]
  ]
