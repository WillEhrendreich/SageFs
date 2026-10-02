/// The ratchet lane (`dotnet SageFs.Tests.dll --ratchets`) runs only the registered ratchets, picked by the
/// identity of their test bodies, never by a name filter. These tests pin the three things that make that
/// safe: the selection really is by reference (a typo cannot drop a ratchet), a ratchet-shaped test cannot
/// stay outside the registry without a written reason, and the gate runs the lane first and stops on red.
module SageFs.Tests.RatchetRegistryTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

module Ratchet = SageFs.Tests.TestInfrastructure.Ratchet
module Integration = SageFs.Tests.TestInfrastructure.Integration
module TrustSignal = SageFs.Tests.TestInfrastructure.TrustSignal

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private read (relative: string) = File.ReadAllText(Path.Combine(repoRoot, relative))

let private names (t: Test) =
  t |> Test.toTestCodeList |> List.map (fun flat -> String.concat "/" flat.name)

// ---- a budget table and the source text that holds it, for the tighten tests ----

let private demoSource = "let budgets =\n  [ \"alpha.fs\", 10\n    \"beta.fs\", 5 ]\n"

let private demoTable (actual: (string * int) list) : Ratchet.Table =
  { Name = "demo"
    SourceFile = "demo.fs"
    Budgets = [ "alpha.fs", 10; "beta.fs", 5 ]
    Actual = fun () -> actual }

let private applyDemo (actual: (string * int) list) (text: string) =
  Ratchet.Tighten.apply (demoTable actual) (Map.ofList actual) text

let selectionTests =
  testList "Ratchet lane selection" [

    testCase "select keeps a registered test by reference and drops its siblings" <| fun _ ->
      let ratchet = testCase "the ratchet" ignore
      let sibling = testCase "a unit test" ignore
      testList "root" [ sibling; ratchet ]
      |> Ratchet.select [ ratchet ]
      |> names
      |> Expect.equal "only the registered body survives" [ "root/the ratchet" ]

    testCase "select matches test bodies, not names, so a mistyped or copied name cannot drop or add a ratchet" <| fun _ ->
      let registered = testCase "same name" ignore
      let lookalike = testCase "same name" ignore
      testList "root" [ lookalike ]
      |> Ratchet.select [ registered ]
      |> names
      |> Expect.isEmpty "a look-alike that was never registered is not in the lane"

    testCase "select still matches when discovery rebuilds a root node" <| fun _ ->
      let registered = testList "ratchets" [ testCase "a" ignore ]
      let rebuilt =
        match registered with
        | Test.TestLabel (name, inner, focus) -> Test.TestLabel (name, inner, focus)
        | other -> other
      testList "root" [ rebuilt; testCase "kept out" ignore ]
      |> Ratchet.select [ registered ]
      |> names
      |> Expect.equal "the rebuilt root's tests are found by their bodies" [ "root/ratchets/a" ]

    testCase "a lane with nothing registered is NothingRan and exits 3, never green" <| fun _ ->
      let rows = ResizeArray<TrustSignal.Row>()
      let tree = testList "root" [ testCase "a unit test" ignore ]
      TrustSignal.runReporting rows.Add Ratchet.entryPoint [| "--no-spinner" |] (Ratchet.select [] tree)
      |> Expect.equal "exit code" 3
      rows |> Seq.map (fun r -> r.Verdict) |> List.ofSeq |> Expect.equal "verdict" [ "NothingRan" ]
  ]

let tightenTests =
  testList "Ratchet tighten" [

    testCase "a budget above its count is lowered to the count, and the change is reported" <| fun _ ->
      match applyDemo [ "alpha.fs", 7; "beta.fs", 5 ] demoSource with
      | Result.Error reason -> failtest reason
      | Result.Ok (text, changes) ->
        text |> Expect.equal "only alpha moved" "let budgets =\n  [ \"alpha.fs\", 7\n    \"beta.fs\", 5 ]\n"
        changes |> Expect.equal "what changed" [ { Table = "demo"; Key = "alpha.fs"; From = 10; To = 7 } ]

    testCase "a budget is never raised, and an over-budget key is reported and left alone" <| fun _ ->
      let actual = [ "alpha.fs", 12; "beta.fs", 5 ]
      match applyDemo actual demoSource with
      | Result.Error reason -> failtest reason
      | Result.Ok (text, changes) ->
        text |> Expect.equal "the text is untouched" demoSource
        changes |> Expect.isEmpty "nothing was lowered"
      Ratchet.Tighten.overBudget (demoTable actual) (Map.ofList actual)
      |> Expect.equal "the over-budget key is named" [ "alpha.fs", 12, 10 ]

    testCase "a key the scan no longer sees counts zero, so its budget is lowered to zero" <| fun _ ->
      match applyDemo [ "beta.fs", 5 ] demoSource with
      | Result.Error reason -> failtest reason
      | Result.Ok (text, _) -> text |> Expect.stringContains "alpha went to zero" "\"alpha.fs\", 0"

    testCase "tightening twice changes nothing the second time" <| fun _ ->
      let actual = [ "alpha.fs", 7; "beta.fs", 2 ]
      match applyDemo actual demoSource with
      | Result.Error reason -> failtest reason
      | Result.Ok (once, _) ->
        let table = { demoTable actual with Budgets = [ "alpha.fs", 7; "beta.fs", 2 ] }
        match Ratchet.Tighten.apply table (Map.ofList actual) once with
        | Result.Error reason -> failtest reason
        | Result.Ok (twice, changes) ->
          twice |> Expect.equal "same text" once
          changes |> Expect.isEmpty "no further change"

    testCase "a literal that is not in the source is an error naming the key, never a quiet no-op" <| fun _ ->
      match applyDemo [ "alpha.fs", 7; "beta.fs", 5 ] "let budgets = []\n" with
      | Result.Ok _ -> failtest "a missing literal must not read as success"
      | Result.Error reason -> reason |> Expect.stringContains "names the key" "alpha.fs"

    testCase "a literal written twice is an error, so the wrong one is never rewritten" <| fun _ ->
      match applyDemo [ "alpha.fs", 7; "beta.fs", 5 ] (demoSource + demoSource) with
      | Result.Ok _ -> failtest "an ambiguous literal must not be guessed"
      | Result.Error reason -> reason |> Expect.stringContains "says how many it found" "found 2"

    testCase "a literal that holds a different number than the loaded table is an error (rebuild first)" <| fun _ ->
      match applyDemo [ "alpha.fs", 7; "beta.fs", 5 ] "[ \"alpha.fs\", 11\n  \"beta.fs\", 5 ]" with
      | Result.Ok _ -> failtest "a stale build must not rewrite the source"
      | Result.Error reason -> reason |> Expect.stringContains "says what the source holds" "says 11"

    testCase "run writes only the files that changed, through the injected read and write" <| fun _ ->
      let files = System.Collections.Generic.Dictionary<string, string>()
      files["demo.fs"] <- demoSource
      let written = ResizeArray<string>()
      let run () =
        Ratchet.Tighten.run (fun f -> files[f]) (fun f text -> files[f] <- text; written.Add f) [ demoTable [ "alpha.fs", 7; "beta.fs", 5 ] ]
      match run () with
      | Result.Error reason -> failtest reason
      | Result.Ok (changes, over) ->
        changes.Length |> Expect.equal "one budget lowered" 1
        over |> Expect.isEmpty "nothing over budget"
      written |> List.ofSeq |> Expect.equal "the file was written once" [ "demo.fs" ]
  ]

let private sourceFiles () =
  Directory.GetFiles(Path.Combine(repoRoot, "SageFs.Tests"), "*.fs", SearchOption.TopDirectoryOnly)
  |> Array.map (fun path -> Path.GetFileName path, File.ReadAllText path)
  |> Array.toList

/// A test source that reads the repo's own files: the shape of a ratchet.
let private readsTheTree (text: string) =
  Regex.IsMatch(text, "ReadAllText|ReadAllLines|EnumerateFiles|GetFiles")
  && Regex.IsMatch(text, "repoRoot|__SOURCE_DIRECTORY__|Directory\\.Build\\.props|BaseDirectory")

/// A test source that starts a process or belongs to an integration runner: not a pure ratchet.
let private spawnsOrIsIntegration (text: string) =
  Regex.IsMatch(text, "ProcessStartInfo|Process\\.Start|Integration\\.(hostList|hostCase|dedicated|register)|Playwright")

let private registersRatchets (text: string) = text.Contains "Ratchet."

/// Files that read the tree but are deliberately not ratchets. Each one says why; the list is checked
/// for staleness below, so a file that gets registered (or stops reading the tree) must leave it.
let private notRatchets : (string * string) list =
  [ "CohortIntegrationScopeTests.fs", "a ceiling pin on the .slnx project count inside a synthetic-XML unit list"
    "CohortLedgerExportTests.fs", "product behaviour on a fixture it reads; no rule over the tree"
    "CohortPropertyTests.fs", "property tests of the cohort model; reads one fixture"
    "DashboardHealthVerdictRenderingTests.fs", "one string pin on a message, not a rule over the tree"
    "FirstRunLinuxTests.fs", "locates the repo from the binary and swaps the global Console.Out"
    "FsiNamingContractTests.fs", "pins a vendored compiler excerpt; a contract with FCS, not the tree"
    "HostManifestTests.fs", "needs the SageFs.Host build output beside the tests and passes vacuously without it"
    "LiveTestingGoldenFixtureTests.fs", "golden client-contract fixtures, mixed with behaviour tests"
    "NativeResolutionTests.fs", "builds a temp project to prove native resolution"
    "ProjectLoadProgressTests.fs", "product behaviour on temp projects"
    "ReloadPlanningTests.fs", "runs the product parser over every Core file: a real-source stress test, too heavy for the lane"
    "SageFsErrorJsonShapeTests.fs", "one golden JSON file inside a unit list"
    "StableIdentityEvalTests.fs", "product behaviour on a fixture"
    "TestCountBadge.fs", "a helper that stamps the README, not a test"
    "TutorialTests.fs", "pins the shape of one sample script"
    "UsabilityFixesTests.fs", "one case reads Directory.Build.props inside a list of unit tests" ]

let registryTests =
  testList "Ratchet registry" [

    testCase "the lane has ratchets, and both classes are registered" <| fun _ ->
      let registered = Ratchet.registered ()
      registered |> Expect.isNonEmpty "ratchets are registered"
      registered |> List.map fst |> List.distinct |> List.sortBy Ratchet.Class.name
      |> Expect.equal "both classes have members" [ Ratchet.Invariant; Ratchet.SizeProxy ]

    testCase "every registered ratchet is in the default suite by reference, so the TRUST ledger there still counts it" <| fun _ ->
      let defaultBodies = Integration.bodiesOf [ Integration.defaultSuite () ]
      let missing =
        Ratchet.registered ()
        |> List.filter (fun (_, test) -> not (defaultBodies.IsSupersetOf(Integration.bodiesOf [ test ])))
        |> List.collect (snd >> names)
      missing |> Expect.isEmpty "a ratchet registered but not in the default tree (a stray copy, not the real test)"

    testCase "the lane is exactly the registered ratchets whose class the lane runs" <| fun _ ->
      let lane = Ratchet.laneSuite ()
      let expected = Integration.bodiesOf (Ratchet.laneTests ())
      let actual = Integration.bodiesOf [ lane ]
      actual.SetEquals expected |> Expect.isTrue "the lane tree holds those bodies and nothing else"
      (TrustSignal.registeredCount lane, 0) |> Expect.isGreaterThan "the lane is not empty"

    testCase "the file-size budgets are the SizeProxy class and the blocking-call budgets are Invariant" <| fun _ ->
      let inClass cls =
        Ratchet.registered () |> List.filter (fun (c, _) -> c = cls) |> List.collect (snd >> names) |> String.concat "\n"
      inClass Ratchet.SizeProxy |> Expect.stringContains "line budgets are a size proxy" "file-size budgets"
      inClass Ratchet.Invariant |> Expect.stringContains "blocking calls starve the pool: an invariant" "blocking-call budgets"

    testCase "every budget table is registered, found in its own file, and its literals are each written once" <| fun _ ->
      let tables = Ratchet.tables ()
      tables |> List.map (fun t -> t.Name) |> List.sort
      |> Expect.equal "the four budget tables" [ "JSON centralization"; "blocking-call budgets"; "file-size budgets"; "timeout literals" ]
      let problems =
        tables
        |> List.choose (fun table ->
          // Pretend every count is zero: every literal must be found once and be rewritable.
          match Ratchet.Tighten.apply table Map.empty (read table.SourceFile) with
          | Result.Ok _ -> None
          | Result.Error reason -> Some reason)
      problems |> Expect.isEmpty "every table's literals can be found in the source, so --tighten cannot fail on a real run"

    testCase "every test source that defines a budgets table registers it as a ratchet table" <| fun _ ->
      let missing =
        sourceFiles ()
        |> List.filter (fun (name, text) ->
          name <> "RatchetRegistryTests.fs" && Regex.IsMatch(text, "let\\s+(private\\s+)?budgets\\s*(:[^=\\n]*)?=\\s*\\n\\s*\\[") && not (text.Contains "Ratchet.table"))
        |> List.map fst
      missing |> Expect.isEmpty "a budget table that --tighten cannot see (register it with Ratchet.table)"

    testCase "a test source that reads the repo and runs in process is registered as a ratchet, or named in the not-ratchets list with a reason" <| fun _ ->
      let known = notRatchets |> List.map fst |> Set.ofList
      let unplaced =
        sourceFiles ()
        |> List.filter (fun (name, text) ->
          readsTheTree text && not (spawnsOrIsIntegration text) && not (registersRatchets text) && not (known.Contains name))
        |> List.map fst
      unplaced |> Expect.isEmpty "ratchet-shaped files outside the registry: wrap the test with Ratchet.register, or add the file to notRatchets with the reason"

    testCase "the not-ratchets list only names files that exist, read the tree, and are still unregistered" <| fun _ ->
      let files = sourceFiles () |> Map.ofList
      let stale =
        notRatchets
        |> List.choose (fun (name, _) ->
          match Map.tryFind name files with
          | None -> Some (sprintf "%s does not exist" name)
          | Some text when registersRatchets text -> Some (sprintf "%s is registered now: drop it from the list" name)
          | Some text when not (readsTheTree text) -> Some (sprintf "%s no longer reads the tree: drop it from the list" name)
          | Some _ -> None)
      stale |> Expect.isEmpty "every entry is current"

    testCase "a test whose own name calls it a ratchet is registered" <| fun _ ->
      let ratchetBodies = Integration.bodiesOf (Ratchet.registered () |> List.map snd)
      let tree = Integration.defaultSuite ()
      let rec walk (parents: string list) (t: Test) : (string * obj) list =
        match t with
        | Test.TestCase (code, _) -> [ String.concat "/" (List.rev parents), box code ]
        | Test.TestList (tests, _) -> tests |> List.collect (walk parents)
        | Test.TestLabel (name, inner, _) -> walk (name :: parents) inner
        | Test.Sequenced (_, inner) -> walk parents inner
      let unplaced =
        walk [] tree
        |> List.filter (fun (name, code) ->
          Regex.IsMatch(name, "ratchet", RegexOptions.IgnoreCase) && not (ratchetBodies.Contains code))
        |> List.map fst
      unplaced |> Expect.isEmpty "tests that call themselves ratchets but are outside the registry"
  ]

let wiringTests =
  testList "Ratchet CI and ship wiring" [

    testCase "the lane's entry point is --ratchets" <| fun _ ->
      Ratchet.entryPoint |> Expect.equal "the flag" "--ratchets"

    testCase "Program.fs dispatches the entry point, so the flag runs something" <| fun _ ->
      read "SageFs.Tests/Program.fs" |> Expect.stringContains "dispatched by the registry's own name" "Ratchet.entryPoint"

    testCase "ci-pipeline.fsx invokes the lane through the ledgered testTier step" <| fun _ ->
      TrustSignal.pipelineTierArgs (read "ci-pipeline.fsx")
      |> List.map TrustSignal.tierOfArgs
      |> Expect.contains "a registered tier no stage invokes is a dark gate" Ratchet.entryPoint

    testCase "the ratchets stage comes straight after build and before every other stage" <| fun _ ->
      let stages =
        Regex.Matches(read "ci-pipeline.fsx", "^  stage \"([^\"]+)\"", RegexOptions.Multiline)
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> List.ofSeq
      let at name = stages |> List.tryFindIndex (fun s -> s = name)
      match at "build", at "ratchets" with
      | Some build, Some ratchets ->
        ratchets |> Expect.equal "ratchets is the stage right after build" (build + 1)
      | _ -> failtest "ci-pipeline.fsx needs both a build and a ratchets stage"

    testCase "scripts/ship runs the ratchets before it bumps the version or calls the gate" <| fun _ ->
      let ship = read "scripts/ship"
      ship |> Expect.stringContains "ship runs the lane binary" "SageFs.Tests.dll --ratchets"
      // The two call sites: a given commit, and HEAD about to be bumped.
      let givenCommit = ship.IndexOf "run_ratchets \"$sha\""
      let beforeBump = ship.IndexOf "run_ratchets \"$(git"
      (givenCommit, -1) |> Expect.isGreaterThan "a shipped commit is checked"
      (beforeBump, -1) |> Expect.isGreaterThan "HEAD is checked before it is bumped"
      (beforeBump, ship.IndexOf "scripts/bump-version\")") |> Expect.isLessThan "before the bump"
      (givenCommit, ship.IndexOf "scripts/local-gate\" \"$sha\"") |> Expect.isLessThan "and before the gate"
  ]

do
  for list in [ selectionTests; tightenTests; registryTests; wiringTests ] do
    Ratchet.register Ratchet.Invariant list |> ignore

[<Tests>]
let tests = testList "Ratchet lane" [ selectionTests; tightenTests; registryTests; wiringTests ]
