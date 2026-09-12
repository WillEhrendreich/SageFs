/// RED-then-GREEN tests for pure content fingerprinting (demo-gif-plan.md
/// §4.10): `ofInputs` combines per-input content hashes into one
/// order-independent digest, and `check` diffs a scenario's previously
/// recorded per-input digests against its current ones to report exactly
/// which `Input` categories changed.
module SageFs.Demos.Tests.FingerprintTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Demos.Domain
open SageFs.Demos.Fingerprint

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 100 }

/// A fixed pool of concrete `Input` values covering every case shape
/// (§4.10's own categories), so generated `Inputs` lists use ids and
/// commits a scenario could actually declare, never made-up placeholders.
let private inputPool : Input list =
  [ Input.SampleTree Sample.WebappDatastar
    Input.SampleTree Sample.RaylibGame
    Input.SampleTree Sample.ConsoleTicker
    Input.SampleTree Sample.FromCSharp
    Input.ClientSurface Client.Dashboard
    Input.ClientSurface Client.VsCode
    Input.ClientSurface Client.Neovim
    Input.DaemonRoutes
    Input.ScenarioDefinition (ScenarioId.derive Capability.HotReload Client.VsCode AppKind.Web)
    Input.ScenarioDefinition (ScenarioId.derive Capability.Repl Client.Neovim AppKind.NoApp)
    Input.StyleAndProfiles
    Input.ToolVersions
    Input.NvimPluginCommit "3f2a1111111111111111111111111111111111"
    Input.NvimPluginCommit "ce2f222222222222222222222222222222222222" ]

let private genDigest : Gen<Digest> =
  Gen.choose (0, 0xFFFFFF) |> Gen.map (fun n -> Digest(sprintf "%06x" n))

/// A `ResolvedInput` list with distinct categories — a random-size subset
/// of `inputPool`, each paired with a random hash.
let private genResolvedInputs : Gen<Inputs> =
  gen {
    let! n = Gen.choose (1, List.length inputPool)
    let! shuffled = Gen.shuffle (List.toArray inputPool)
    let categories = shuffled |> Array.toList |> List.truncate n
    let! hashes = Gen.listOfLength n genDigest
    return List.map2 (fun category hash -> { Category = category; Hash = hash }) categories hashes
  }

/// Pairs an `Inputs` value with a genuine permutation of itself, for the
/// order-independence property.
let private genInputsWithShuffle : Gen<Inputs * Inputs> =
  gen {
    let! inputs = genResolvedInputs
    let! shuffled = Gen.shuffle (List.toArray inputs)
    return inputs, Array.toList shuffled
  }

/// Builds a `(previous, current, expectedChanged)` triple: `previous` is a
/// recorded `Inputs`, `current` is derived from it by keeping, re-hashing,
/// or dropping each entry (plus optionally adding brand-new categories),
/// and `expectedChanged` is the ground truth computed independently of
/// `Fingerprint.check` — so the property proves `check` against a diff this
/// test built itself, not against its own logic mirrored back.
let private genCheckCase : Gen<Inputs * Inputs * Input list> =
  gen {
    let! previous = genResolvedInputs
    let! decisions = Gen.listOfLength previous.Length (Gen.elements [ 0; 1; 2 ]) // 0=keep 1=re-hash 2=drop
    let! newHashes = Gen.listOfLength previous.Length genDigest
    let triples = List.zip3 previous decisions newHashes
    let kept =
      triples
      |> List.choose (fun (ri, decision, newHash) ->
        match decision with
        | 0 -> Some ri
        | 1 -> Some { ri with Hash = newHash }
        | _ -> None)
    let removedCategories =
      triples |> List.choose (fun (ri, decision, _) -> if decision = 2 then Some ri.Category else None)
    let reHashedChangedCategories =
      triples
      |> List.choose (fun (ri, decision, newHash) ->
        if decision = 1 && newHash <> ri.Hash then Some ri.Category else None)
    let previousCategories = previous |> List.map (fun ri -> ri.Category) |> Set.ofList
    let candidateNewCategories = inputPool |> List.filter (fun c -> not (Set.contains c previousCategories))
    let! extraCount = Gen.choose (0, List.length candidateNewCategories)
    let! shuffledExtras = Gen.shuffle (List.toArray candidateNewCategories)
    let extraCategories = shuffledExtras |> Array.toList |> List.truncate extraCount
    let! extraHashes = Gen.listOfLength extraCategories.Length genDigest
    let extras = List.map2 (fun category hash -> { Category = category; Hash = hash }) extraCategories extraHashes
    let current = kept @ extras
    let expectedChanged =
      (removedCategories @ reHashedChangedCategories @ extraCategories)
      |> Set.ofList
      |> Set.toList
    return previous, current, expectedChanged
  }

type private FingerprintGenerators =
  static member Inputs() = Arb.fromGen genResolvedInputs
  static member InputsWithShuffle() = Arb.fromGen genInputsWithShuffle
  static member CheckCase() = Arb.fromGen genCheckCase

let private fpConfig = { propConfig with arbitrary = [ typeof<FingerprintGenerators> ] }

[<Tests>]
let tests =
  testList "Fingerprint" [

    testPropertyWithConfig fpConfig "ofInputs is order-independent — shuffling the inputs never changes the digest" <|
      fun ((inputs, shuffled): Inputs * Inputs) ->
        ofInputs shuffled |> Expect.equal "a shuffled permutation hashes identically to the original order" (ofInputs inputs)

    testCase "ofInputs is deterministic for the same inputs" <| fun _ ->
      let inputs = [ { Category = Input.DaemonRoutes; Hash = Digest "abc123" } ]
      ofInputs inputs |> Expect.equal "hashing twice gives the same digest" (ofInputs inputs)

    testCase "ofInputs differs when a content hash differs" <| fun _ ->
      let a = [ { Category = Input.DaemonRoutes; Hash = Digest "abc123" } ]
      let b = [ { Category = Input.DaemonRoutes; Hash = Digest "def456" } ]
      Expect.notEqual "a changed hash changes the digest" (ofInputs a) (ofInputs b)

    testCase "check is Missing when nothing was ever recorded" <| fun _ ->
      let inputs = [ { Category = Input.DaemonRoutes; Hash = Digest "abc123" } ]
      check RecordedDigest.Never inputs |> Expect.equal "never recorded is Missing" Freshness.Missing

    testPropertyWithConfig fpConfig "a missing recorded digest is always Missing, regardless of current inputs" <|
      fun (inputs: Inputs) ->
        check RecordedDigest.Never inputs |> Expect.equal "never recorded is always Missing" Freshness.Missing

    testCase "check is Fresh when every current hash matches the recording" <| fun _ ->
      let inputs =
        [ { Category = Input.DaemonRoutes; Hash = Digest "abc123" }
          { Category = Input.ToolVersions; Hash = Digest "def456" } ]
      check (RecordedDigest.At inputs) inputs |> Expect.equal "identical inputs are Fresh" Freshness.Fresh

    testCase "check is Stale naming exactly the input whose hash changed" <| fun _ ->
      let previous = [ { Category = Input.DaemonRoutes; Hash = Digest "abc123" } ]
      let current = [ { Category = Input.DaemonRoutes; Hash = Digest "def456" } ]
      check (RecordedDigest.At previous) current
      |> Expect.equal "the re-hashed category is reported as changed" (Freshness.Stale [ Input.DaemonRoutes ])

    testCase "check is Stale naming a newly added input not previously recorded" <| fun _ ->
      let previous = [ { Category = Input.DaemonRoutes; Hash = Digest "abc123" } ]
      let current =
        [ { Category = Input.DaemonRoutes; Hash = Digest "abc123" }
          { Category = Input.ToolVersions; Hash = Digest "new000" } ]
      check (RecordedDigest.At previous) current
      |> Expect.equal "the newly added category is reported as changed" (Freshness.Stale [ Input.ToolVersions ])

    testCase "check is Stale naming an input removed since the recording" <| fun _ ->
      let previous =
        [ { Category = Input.DaemonRoutes; Hash = Digest "abc123" }
          { Category = Input.ToolVersions; Hash = Digest "def456" } ]
      let current = [ { Category = Input.DaemonRoutes; Hash = Digest "abc123" } ]
      check (RecordedDigest.At previous) current
      |> Expect.equal "the removed category is reported as changed" (Freshness.Stale [ Input.ToolVersions ])

    testPropertyWithConfig
      fpConfig
      "check returns Fresh iff every input hash matches the recorded one, and Stale lists exactly the changed inputs" <|
      fun ((previous, current, expectedChanged): Inputs * Inputs * Input list) ->
        let expected = if List.isEmpty expectedChanged then Freshness.Fresh else Freshness.Stale expectedChanged
        check (RecordedDigest.At previous) current
        |> Expect.equal "check matches the ground-truth diff between previous and current" expected
  ]
