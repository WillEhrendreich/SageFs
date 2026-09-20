module SageFs.Tests.FsiHostBuildTests

open System
open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.FsiHostBuild
open SageFs.Tests.TestInfrastructure

let private sources = [ "FsiHost.fsproj", "<Project/>"; "Program.fs", "module P"; "FsiProtocol.fs", "module Q" ]

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

/// The dotnet the tests run with (honours DOTNET_HOST_PATH like the daemon does).
let private dotnet =
  match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
  | null
  | "" -> "dotnet"
  | path -> path

let private withTempCache (body: string -> unit) =
  let cache = Path.Combine(Path.GetTempPath(), "sagefs-fsihost-" + Guid.NewGuid().ToString "N")
  try body cache
  finally
    try Directory.Delete(cache, true) with _ -> ()

let private builtDll (cache: string) : string =
  match resolveSdkVersion dotnet repoRoot |> Result.bind (fun sdk -> ensureBuilt dotnet sdk cache) with
  | Result.Ok(Built dll)
  | Result.Ok(Reused dll) -> dll
  | Result.Error reason -> failtest (describeBuildError reason)

[<Tests>]
let tests =
  testList "FsiHostBuild" [
    testList "cacheKey" [
      testCase "is stable for identical inputs" <| fun _ ->
        Expect.equal "same inputs, same key" (cacheKey "10.0.401" sources) (cacheKey "10.0.401" sources)

      testCase "changes with the SDK version" <| fun _ ->
        Expect.notEqual "different SDK, different key" (cacheKey "10.0.401" sources) (cacheKey "11.0.100" sources)

      testCase "changes when any source changes" <| fun _ ->
        let edited = sources |> List.map (fun (name, content) -> if name = "Program.fs" then name, content + " " else name, content)
        Expect.notEqual "edited source, different key" (cacheKey "10.0.401" sources) (cacheKey "10.0.401" edited)

      testCase "does not depend on the order the sources are listed" <| fun _ ->
        Expect.equal "order-independent" (cacheKey "10.0.401" sources) (cacheKey "10.0.401" (List.rev sources))

      testCase "is a safe directory name" <| fun _ ->
        let key = cacheKey "11.0.100-rc.1.26425.128" sources
        Expect.isFalse "no path separators or invalid characters" (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
    ]

    testList "globalJson" [
      testCase "pins exactly one SDK with no roll-forward" <| fun _ ->
        use document = JsonDocument.Parse(globalJson "11.0.100-rc.1.26425.128")
        let sdk = document.RootElement.GetProperty "sdk"
        Expect.equal "version" "11.0.100-rc.1.26425.128" (sdk.GetProperty("version").GetString())
        Expect.equal "no roll-forward" "disable" (sdk.GetProperty("rollForward").GetString())
    ]

    testList "embedded sources" [
      testCase "every host source is embedded and non-empty" <| fun _ ->
        match embeddedSources () with
        | Result.Error reason -> failtest (describeBuildError reason)
        | Result.Ok found ->
          Expect.equal "all host files, in order" hostSourceNames (found |> List.map fst)
          for name, content in found do
            Expect.isFalse (sprintf "%s is empty" name) (String.IsNullOrWhiteSpace content)

      testCase "the embedded protocol is the file the tests were built from" <| fun _ ->
        let onDisk = File.ReadAllText(Path.Combine(repoRoot, "SageFs.FsiHost", "FsiProtocol.fs"))
        match embeddedSources () with
        | Result.Error reason -> failtest (describeBuildError reason)
        | Result.Ok found ->
          Expect.equal "embedded == source" onDisk (found |> List.find (fun (name, _) -> name = "FsiProtocol.fs") |> snd)
    ]

    testList "parseSdkList" [
      testCase "reads the version at the start of each line" <| fun _ ->
        parseSdkList "10.0.401 [/home/will/.dotnet/sdk]\n11.0.100-rc.1.26425.128 [/home/will/.dotnet/sdk]\n"
        |> Expect.equal "both versions" [ "10.0.401"; "11.0.100-rc.1.26425.128" ]

      testCase "ignores blank and non-version lines" <| fun _ ->
        parseSdkList "\n  \nWARNING: nothing here\n9.0.300 [/x]\r\n"
        |> Expect.equal "only the version" [ "9.0.300" ]
    ]

    Integration.hostList "ensureBuilt (real SDK build)" [
      // FCS's API differs between SDKs (SDK 11 changed a tooltip type), so the ONE host source must compile with
      // EVERY installed SDK. A build that fails on any of them is a real bug, not an environment quirk.
      testCase "the host builds with every installed SDK" <| fun _ ->
        withTempCache (fun cache ->
          let sdks =
            match installedSdkVersions dotnet with
            | Result.Ok sdks -> sdks
            | Result.Error reason -> failtest (describeBuildError reason)
          Expect.isNonEmpty "at least one SDK is installed" sdks
          for sdk in sdks do
            match ensureBuilt dotnet sdk cache with
            | Result.Ok _ -> ()
            | Result.Error reason -> failtestf "the host does not build with SDK %s:\n%s" sdk (describeBuildError reason))

      testCase "builds the host once, then reuses it from the cache" <| fun _ ->
        withTempCache (fun cache ->
          let sdk =
            match resolveSdkVersion dotnet repoRoot with
            | Result.Ok sdk -> sdk
            | Result.Error reason -> failtest (describeBuildError reason)
          let dll =
            match ensureBuilt dotnet sdk cache with
            | Result.Ok(Built dll) -> dll
            | other -> failtestf "first call should build, got %A" other
          Expect.isTrue "host assembly exists" (File.Exists dll)
          Expect.isTrue "the SDK's own compiler service sits beside it" (File.Exists(Path.Combine(Path.GetDirectoryName dll |> string, "FSharp.Compiler.Service.dll")))
          let stamp = File.GetLastWriteTimeUtc dll
          match ensureBuilt dotnet sdk cache with
          | Result.Ok(Reused again) ->
            Expect.equal "same path" dll again
            Expect.equal "not rebuilt" stamp (File.GetLastWriteTimeUtc dll)
          | other -> failtestf "second call should reuse, got %A" other)

      testCase "the host closure contains nothing SageFs-owned" <| fun _ ->
        withTempCache (fun cache ->
          let dll = builtDll cache
          let owned = [ "SageFs"; "Fantomas"; "Cecil"; "Harmony"; "Adaptive"; "Ionide"; "TreeSitter"; "SystemTextJson" ]
          let assemblies =
            Directory.GetFiles(Path.GetDirectoryName dll |> string, "*.dll")
            |> Array.map (fun path -> Path.GetFileName(path: string) |> string)
          let offenders = assemblies |> Array.filter (fun name -> owned |> List.exists (fun o -> name.Contains o))
          Expect.isEmpty (sprintf "SageFs-owned assemblies in the host: %A" offenders) offenders)
    ]
  ]
