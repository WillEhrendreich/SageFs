/// Issue #153: a session on SageFs.Core (or any project that targets
/// `net10.0;net11.0`) faulted with "Not all DLLs are found" when only the
/// net11.0 build existed, because Ionide loads a multi-targeted project at its
/// FIRST framework and SageFs only ever looked in that framework's folder.
/// These pin the pure decision that replaced it: from the frameworks a project
/// declares, take the best one this host can load AND that has a build on disk,
/// and when none does, say every folder that was checked, which framework would
/// be accepted, and the command that builds it.
module SageFs.Tests.FrameworkPickerTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.ProjectLoading

module FP = FrameworkPicker

let private root = Path.Combine(Path.GetTempPath(), "sagefs-fp-fixture")
let private projectFile = Path.Combine(root, "Core", "Core.fsproj")
let private targetPath = Path.Combine(root, "Core", "bin", "Debug", "net10.0", "Core.dll")

let private outputsOf (tfm: string) = FP.outputsFor targetPath "net10.0" tfm
let private existing (paths: string list) = fun (p: string) -> List.contains p paths
let private sameTime = fun (_: string) -> DateTime.UnixEpoch

let private pick (host: int) (declared: string list) (built: string list) =
  FP.choose host declared outputsOf (existing built) sameTime

let private debugOutput (tfm: string) = Path.Combine(root, "Core", "bin", "Debug", tfm, "Core.dll")
let private releaseOutput (tfm: string) = Path.Combine(root, "Core", "bin", "Release", tfm, "Core.dll")

let private tfmPool =
  [ "netstandard2.0"; "net8.0"; "net9.0"; "net10.0"; "net11.0"; "net12.0"; "net48"; "net10.0-windows" ]

/// A host major, the frameworks a project declares, and which of every
/// framework's outputs happen to exist on disk.
let private genScenario : Gen<int * string list * string list> =
  gen {
    let! host = Gen.elements [ 8; 9; 10; 11 ]
    let! declared = Gen.subListOf tfmPool
    let allOutputs = tfmPool |> List.collect outputsOf
    let! built = Gen.subListOf allOutputs
    return host, declared, built
  }

let private scenarios = Arb.fromGen genScenario

[<Tests>]
let tests =
  testList "ProjectLoading.FrameworkPicker" [

    testCase "WHY: only net11.0 is built, as after dotnet build of a net11.0 consumer, so the session loads net11.0 (issue 153)" <| fun _ ->
      pick 11 [ "net10.0"; "net11.0" ] [ releaseOutput "net11.0" ]
      |> Expect.equal
           "the declared first framework has no output; the built one is chosen"
           (FP.Pick.Chosen ("net11.0", releaseOutput "net11.0"))

    testCase "both frameworks built: the host's own framework wins" <| fun _ ->
      pick 11 [ "net10.0"; "net11.0" ] [ debugOutput "net10.0"; debugOutput "net11.0" ]
      |> Expect.equal "net11.0 is the host's" (FP.Pick.Chosen ("net11.0", debugOutput "net11.0"))

    testCase "only the older framework is built: the host loads it rather than faulting" <| fun _ ->
      pick 11 [ "net10.0"; "net11.0" ] [ debugOutput "net10.0" ]
      |> Expect.equal "net10.0 loads on a net11.0 host" (FP.Pick.Chosen ("net10.0", debugOutput "net10.0"))

    testCase "a framework newer than the host is never chosen, even when it is the only build" <| fun _ ->
      pick 10 [ "net10.0"; "net11.0" ] [ debugOutput "net11.0" ]
      |> function
        | FP.Pick.NoneBuilt (_, wouldAccept) ->
          wouldAccept |> Expect.equal "only net10.0 is acceptable on a net10 host" [ "net10.0" ]
        | other -> failtestf "expected NoneBuilt, got %A" other

    testCase "a project whose frameworks are all newer than the host says so" <| fun _ ->
      pick 10 [ "net11.0" ] []
      |> Expect.equal "nothing loadable" (FP.Pick.NoLoadableFramework ([ "net11.0" ], 10))

    testCase "WHY: nothing built, so the message names every folder checked, the accepted frameworks and the build command" <| fun _ ->
      let message =
        pick 11 [ "net10.0"; "net11.0" ] []
        |> FP.describe projectFile
      for expected in
        [ Path.GetDirectoryName (debugOutput "net11.0")
          Path.GetDirectoryName (releaseOutput "net11.0")
          Path.GetDirectoryName (debugOutput "net10.0")
          Path.GetDirectoryName (releaseOutput "net10.0")
          "net11.0, then net10.0"
          sprintf "dotnet build %s -c Release -f net11.0" projectFile ] do
        message |> Expect.stringContains (sprintf "the message names %s" expected) expected

    testCase "outputsFor swaps the framework folder and keeps both configurations" <| fun _ ->
      FP.outputsFor targetPath "net10.0" "net11.0"
      |> Expect.equal "Debug and Release at the other framework" [ debugOutput "net11.0"; releaseOutput "net11.0" ]

    testCase "outputsFor an unknown layout only knows the framework the project was loaded at" <| fun _ ->
      let flat = Path.Combine(root, "artifacts", "bin", "Core", "Debug", "Core.dll")
      FP.outputsFor flat "net10.0" "net11.0"
      |> Expect.isEmpty "a layout with no framework folder cannot be translated to another framework"

    testCase "parse reads net, netstandard and refuses what it cannot load" <| fun _ ->
      [ "net11.0"; "netstandard2.0"; "net10.0-windows"; "net48"; "netcoreapp3.1"; "" ]
      |> List.map FP.Moniker.parse
      |> Expect.equal
           "platform-suffixed, .NET Framework and netcoreapp monikers are Unrecognised"
           [ FP.Moniker.Net (11, 0)
             FP.Moniker.NetStandard (2, 0)
             FP.Moniker.Unrecognised "net10.0-windows"
             FP.Moniker.Unrecognised "net48"
             FP.Moniker.Unrecognised "netcoreapp3.1"
             FP.Moniker.Unrecognised "" ]

    testProperty "PROPERTY: a chosen framework is never newer than the host and its output exists" <|
      Prop.forAll scenarios (fun (host, declared, built) ->
        match pick host declared built with
        | FP.Pick.Chosen (tfm, dll) ->
          let loadable =
            match FP.Moniker.parse tfm with
            | FP.Moniker.Net (major, _) -> major <= host
            | FP.Moniker.NetStandard _ -> true
            | FP.Moniker.Unrecognised _ -> false
          loadable && List.contains dll built && List.contains tfm declared
        | _ -> true)

    testProperty "PROPERTY: no more-preferred framework has a build when a less-preferred one is chosen" <|
      Prop.forAll scenarios (fun (host, declared, built) ->
        match pick host declared built with
        | FP.Pick.Chosen (tfm, _) ->
          FP.preference host declared
          |> List.takeWhile ((<>) tfm)
          |> List.forall (fun earlier -> outputsOf earlier |> List.forall (fun o -> not (List.contains o built)))
        | _ -> true)

    testProperty "PROPERTY: it only says NoneBuilt when no acceptable framework has any output, and it looked in all of them" <|
      Prop.forAll scenarios (fun (host, declared, built) ->
        let accepted = FP.preference host declared
        match pick host declared built with
        | FP.Pick.NoneBuilt (lookedIn, wouldAccept) ->
          wouldAccept = accepted
          && accepted |> List.collect outputsOf |> List.forall (fun o -> List.contains o lookedIn)
          && lookedIn |> List.forall (fun o -> not (List.contains o built))
        | FP.Pick.Chosen _ -> not (List.isEmpty accepted)
        | FP.Pick.NoLoadableFramework _ -> List.isEmpty accepted)

    testProperty "PROPERTY: every outcome has a message, and a NoneBuilt message names each folder it looked in" <|
      Prop.forAll scenarios (fun (host, declared, built) ->
        let choice = pick host declared built
        let message = FP.describe projectFile choice
        let named =
          match choice with
          | FP.Pick.NoneBuilt (lookedIn, wouldAccept) ->
            lookedIn |> List.forall (fun o -> message.Contains (Path.GetDirectoryName o))
            && wouldAccept |> List.forall message.Contains
          | _ -> true
        not (String.IsNullOrWhiteSpace message) && named)
  ]

let private rootOf (evaluatedAt: string) (declared: string list) : FP.Root =
  { ProjectFile = projectFile
    EvaluatedAt = evaluatedAt
    TargetPath = targetPath
    Declared = declared }

[<Tests>]
let reloadTests =
  testList "ProjectLoading.FrameworkPicker.reloads" [

    testCase "WHY: Ionide loaded the root at its first framework, but only the second is built, so it is reloaded there" <| fun _ ->
      FP.reloads 11 (existing [ releaseOutput "net11.0" ]) sameTime [ rootOf "net10.0" [ "net10.0"; "net11.0" ] ]
      |> Expect.equal "reload Core at net11.0" [ projectFile, "net11.0" ]

    testCase "a root already at the framework the picker would choose is left alone" <| fun _ ->
      FP.reloads 11 (existing [ debugOutput "net10.0" ]) sameTime [ rootOf "net10.0" [ "net10.0"; "net11.0" ] ]
      |> Expect.isEmpty "net10.0 is the only build, and it is what was loaded"

    testCase "a single-framework project is never reloaded" <| fun _ ->
      FP.reloads 11 (existing [ debugOutput "net11.0" ]) sameTime [ rootOf "net10.0" [ "net10.0" ] ]
      |> Expect.isEmpty "nothing to choose between"

    testCase "nothing built leaves the root alone so the missing-DLL message can say where it looked" <| fun _ ->
      FP.reloads 11 (existing []) sameTime [ rootOf "net10.0" [ "net10.0"; "net11.0" ] ]
      |> Expect.isEmpty "no framework has output; there is nothing better to reload at"
  ]
