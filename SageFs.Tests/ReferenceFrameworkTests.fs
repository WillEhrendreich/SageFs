/// A session on a project that references a MULTI-TARGETED project
/// (`<TargetFrameworks>net10.0;net11.0</TargetFrameworks>`) used to fault in
/// warmup with "Not all DLLs are found" even though everything was built.
/// Ionide loads each referenced project at its FIRST TFM, so a net11.0 consumer
/// got the net10.0 TargetPath of SageFs.Core/SageFs.Host/SageFs. These pin the
/// pure planner that puts every reference at the TFM MSBuild picked for it
/// (the consumer's `NearestTargetFramework`), and the error that now says
/// where it looked instead of claiming the project isn't built.
module SageFs.Tests.ReferenceFrameworkTests

open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.ProjectLoading

module RF = ReferenceFrameworks

let private root = Path.Combine(Path.GetTempPath(), "sagefs-rf-fixture")
let private proj (name: string) = Path.GetFullPath(Path.Combine(root, name, name + ".fsproj"))

let private node (name: string) (tfm: string) (refs: (string * string) list) : RF.Node =
  { ProjectFile = proj name
    EvaluatedAt = tfm
    ReferencesAt = refs |> List.map (fun (n, t) -> proj n, t) |> Map.ofList }

/// The reported shape, as Ionide hands it over: the net11.0 test project, and
/// its multi-targeted references each loaded at net10.0 (their first TFM).
/// The net10.0 build of SageFs itself says "Core at net10.0", which is the
/// trap: that answer belongs to the wrong build.
let private reportedShape =
  [ node "Tests" "net11.0" [ "Core", "net11.0"; "Host", "net11.0"; "App", "net11.0" ]
    node "Core" "net10.0" []
    node "App" "net10.0" [ "Core", "net10.0"; "Host", "net10.0" ]
    node "Host" "net10.0" [ "Core", "net10.0" ] ]

/// A tiny stand-in for MSBuild evaluating a project at a TFM: the project's
/// references at that TFM, each resolved to the nearest TFM the reference
/// supports (same TFM if it has it, otherwise its newest one not above it,
/// otherwise its lowest). Only used to generate consistent universes for the
/// properties below; production reads MSBuild's own answer.
type private Universe = {
  Supported: Map<string, string list>
  Edges: Map<string, string list>
}

let private tfmOrder = [ "netstandard2.0"; "net8.0"; "net10.0"; "net11.0" ]
let private rank (tfm: string) = tfmOrder |> List.findIndex ((=) tfm)

let private nearest (consumer: string) (supported: string list) =
  match supported |> List.contains consumer with
  | true -> consumer
  | false ->
    match supported |> List.filter (fun t -> rank t <= rank consumer) with
    | [] -> supported |> List.minBy rank
    | below -> below |> List.maxBy rank

let private evaluate (u: Universe) (path: string) (tfm: string) : RF.Node =
  { ProjectFile = path
    EvaluatedAt = tfm
    ReferencesAt =
      u.Edges.[path]
      |> List.map (fun r -> r, nearest tfm u.Supported.[r])
      |> Map.ofList }

/// How Ionide loads the closure: every project at its first supported TFM.
let private ionideLoad (u: Universe) : RF.Node list =
  u.Supported |> Map.toList |> List.map (fun (p, tfms) -> evaluate u p (List.head tfms))

let private genUniverse : Gen<Universe> =
  gen {
    let! count = Gen.choose (1, 6)
    let names = [ for i in 0 .. count - 1 -> proj (sprintf "P%d" i) ]
    // Each project supports 1-4 TFMs, declared in a random order (the first
    // one is what Ionide loads it at).
    let! supported =
      names
      |> List.map (fun _ ->
        gen {
          let! order = Gen.shuffle tfmOrder
          let! count = Gen.choose (1, tfmOrder.Length)
          return order |> Array.toList |> List.take count
        })
      |> Gen.sequenceToList
    // Edges only point forward, so the graph is a DAG like a real build.
    let! edges =
      names
      |> List.mapi (fun i _ ->
        names
        |> List.skip (i + 1)
        |> List.map (fun later -> Gen.elements [ Some later; None ])
        |> Gen.sequenceToList
        |> Gen.map (List.choose id))
      |> Gen.sequenceToList
    return
      { Supported = List.zip names supported |> Map.ofList
        Edges = List.zip names edges |> Map.ofList }
  }

[<Tests>]
let tests =
  testList "ProjectLoading.ReferenceFrameworks" [

    testCase "WHY: a net11.0 consumer's multi-targeted references are reloaded at net11.0, not left at their first TFM" <| fun _ ->
      RF.mismatches reportedShape
      |> List.sort
      |> Expect.equal
           "Core, Host and App were loaded at net10.0 but Tests builds them at net11.0"
           ([ proj "App", "net11.0"; proj "Core", "net11.0"; proj "Host", "net11.0" ] |> List.sort)

    testCase "a project loaded at the wrong TFM does not pass down its wrong-build references" <| fun _ ->
      // App@net10.0 says "Core at net10.0". If the planner trusted that, Core
      // would be pinned to net10.0 before App is even reloaded.
      let nodes =
        [ node "Tests" "net11.0" [ "App", "net11.0" ]
          node "App" "net10.0" [ "Core", "net10.0" ]
          node "Core" "net10.0" [] ]
      let wanted = RF.plan nodes
      wanted.TryFind (proj "Core")
      |> Expect.isNone "Core waits until App is at the TFM Tests builds it at"
      RF.mismatches nodes
      |> Expect.equal "only App is reloaded this round" [ proj "App", "net11.0" ]

    testCase "roots keep the TFM they were loaded at" <| fun _ ->
      let nodes = [ node "Lib" "net10.0" [] ]
      RF.plan nodes
      |> Expect.equal "a lone multi-targeted project is its own root" (Map.ofList [ proj "Lib", "net10.0" ])

    testCase "settle reloads until the reported shape has no mismatches" <| fun _ ->
      let universe =
        { Supported =
            Map.ofList [
              proj "Tests", [ "net11.0" ]
              proj "App", [ "net10.0"; "net11.0" ]
              proj "Host", [ "net10.0"; "net11.0" ]
              proj "Core", [ "net10.0"; "net11.0" ] ]
          Edges =
            Map.ofList [
              proj "Tests", [ proj "Core"; proj "Host"; proj "App" ]
              proj "App", [ proj "Core"; proj "Host" ]
              proj "Host", [ proj "Core" ]
              proj "Core", [] ] }
      let settled =
        RF.settle id (fun tfm paths -> paths |> List.map (fun p -> evaluate universe p tfm)) (ionideLoad universe)
      settled
      |> List.map (fun n -> Path.GetFileNameWithoutExtension n.ProjectFile, n.EvaluatedAt)
      |> List.sort
      |> Expect.equal
           "every project ends up at net11.0, the TFM dotnet build uses for a net11.0 consumer"
           [ "App", "net11.0"; "Core", "net11.0"; "Host", "net11.0"; "Tests", "net11.0" ]

    testCase "a reload that can't produce the TFM leaves the project alone and stops" <| fun _ ->
      let mutable calls = 0
      let settled =
        RF.settle id (fun _ _ -> calls <- calls + 1; []) reportedShape
      settled |> Expect.equal "nothing to replace with, so nothing changes" reportedShape
      calls |> Expect.equal "one attempt per TFM group, then it gives up" 1

    testProperty "PROPERTY: after settle, nothing reachable sits at a TFM its consumer doesn't build it at" <|
      Prop.forAll (Arb.fromGen genUniverse) (fun universe ->
        let settled =
          RF.settle id (fun tfm paths -> paths |> List.map (fun p -> evaluate universe p tfm)) (ionideLoad universe)
        RF.mismatches settled = [])

    testProperty "PROPERTY: settle never changes which projects are loaded, and never moves a root" <|
      Prop.forAll (Arb.fromGen genUniverse) (fun universe ->
        let before = ionideLoad universe
        let settled =
          RF.settle id (fun tfm paths -> paths |> List.map (fun p -> evaluate universe p tfm)) before
        let rootsBefore = RF.roots before |> List.map (fun n -> n.ProjectFile, n.EvaluatedAt) |> Set.ofList
        let rootsAfter = RF.roots settled |> List.map (fun n -> n.ProjectFile, n.EvaluatedAt) |> Set.ofList
        (settled |> List.map (fun n -> n.ProjectFile)) = (before |> List.map (fun n -> n.ProjectFile))
        && rootsBefore = rootsAfter)

    testProperty "PROPERTY: settle asks for each (project, TFM) at most once" <|
      Prop.forAll (Arb.fromGen genUniverse) (fun universe ->
        let asked = System.Collections.Generic.List<string * string>()
        RF.settle id (fun tfm paths ->
          for p in paths do asked.Add((p, tfm))
          // Half the time the reload fails, which is when a retry loop would spin.
          match paths.Length % 2 with
          | 0 -> []
          | _ -> paths |> List.map (fun p -> evaluate universe p tfm)) (ionideLoad universe)
        |> ignore
        asked.Count = (asked |> Seq.distinct |> Seq.length))

    testCase "referencesAtOf reads MSBuild's NearestTargetFramework, keyed by the referenced project's full path" <| fun _ ->
      let consumer = proj "Tests"
      let items =
        Map.ofList [
          "_MSBuildProjectReferenceExistent",
          Set.ofList [
            @"..\Core\Core.fsproj", Map.ofList [ "NearestTargetFramework", "net11.0"; "TargetFrameworks", "net10.0;net11.0" ]
            "../Host/Host.fsproj", Map.ofList [ "NearestTargetFramework", "net10.0" ]
            "../Legacy/Legacy.fsproj", Map.ofList [ "TargetFrameworks", "net48" ] ] ]
      RF.referencesAtOf consumer items
      |> Expect.equal
           "backslash includes resolve on every OS, and a reference with no NearestTargetFramework is left alone"
           (Map.ofList [ proj "Core", "net11.0"; proj "Host", "net10.0" ])

    testCase "referencesAtOf with no project references is empty" <| fun _ ->
      RF.referencesAtOf (proj "Tests") Map.empty
      |> Expect.isEmpty "nothing to plan"
  ]

[<Tests>]
let missingDllMessageTests =
  testList "ProjectLoading.describeMissingDlls" [

    testCase "WHY: the error names every path it checked and the TFM, so 'not built' and 'wrong folder' look different" <| fun _ ->
      let debugPath = Path.Combine(root, "Core", "bin", "Debug", "net11.0", "Core.dll")
      let releasePath = Path.Combine(root, "Core", "bin", "Release", "net11.0", "Core.dll")
      let message =
        describeMissingDlls [
          { Dll = debugPath
            LookedIn = [ debugPath; releasePath ]
            Source = MissingDllSource.ProjectOutput (proj "Core", "net11.0") } ]
      for expected in [ debugPath; releasePath; "net11.0"; "Core.fsproj" ] do
        message |> Expect.stringContains (sprintf "the message names %s" expected) expected

    testCase "the error no longer claims the project isn't built without saying for which TFM" <| fun _ ->
      let dll = Path.Combine(root, "Core", "bin", "Debug", "net10.0", "Core.dll")
      let message =
        describeMissingDlls [
          { Dll = dll; LookedIn = [ dll ]; Source = MissingDllSource.ProjectOutput (proj "Core", "net10.0") } ]
      message.Contains "isn't built yet (both Debug and Release"
      |> Expect.isFalse "the old blanket claim is gone"
      message |> Expect.stringContains "the not-built hint is scoped to the TFM" "isn't built for net10.0 yet"

    testProperty "PROPERTY: every checked path of every missing DLL appears in the message" <|
      Prop.forAll
        (Arb.fromGen (
          Gen.nonEmptyListOf (
            Gen.elements [ "A"; "B"; "C"; "D" ]
            |> Gen.map (fun name ->
              let a = Path.Combine(root, name, "bin", "Debug", "net11.0", name + ".dll")
              let b = Path.Combine(root, name, "bin", "Release", "net11.0", name + ".dll")
              { Dll = a; LookedIn = [ a; b ]; Source = MissingDllSource.Reference }))))
        (fun missing ->
          let message = describeMissingDlls missing
          missing |> List.forall (fun m -> m.LookedIn |> List.forall message.Contains))
  ]
