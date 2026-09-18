module SageFs.Tests.IntegrationRegistryTests

open Expecto
open Expecto.Flip

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private names (t: Test) =
  t |> Test.toTestCodeList |> List.map (fun flat -> String.concat "/" flat.name)

[<Tests>]
let integrationRegistryTests =
  testList "Integration registry" [

    testCase "exclude removes a registered suite by reference and keeps its siblings" <| fun _ ->
      let integration = testList "[Integration] spawns things" [ testCase "a" ignore ]
      let unitTest = testCase "pure" ignore
      testList "root" [ unitTest; testSequenced integration ]
      |> Integration.exclude [ integration ]
      |> names
      |> Expect.equal "only the unit test survives" [ "root/pure" ]

    testCase "exclude matches test bodies, not names" <| fun _ ->
      let registered = testList "[Integration] same name" [ testCase "a" ignore ]
      let lookalike = testList "[Integration] same name" [ testCase "a" ignore ]
      testList "root" [ lookalike ]
      |> Integration.exclude [ registered ]
      |> names
      |> Expect.equal "an unregistered look-alike is kept (and caught by the guard)" [ "root/[Integration] same name/a" ]

    // Expecto's assembly discovery rebuilds each [<Tests>] value's root node;
    // exclusion must still hold for the rebuilt node.
    testCase "exclude still matches when discovery rebuilds the suite's root node" <| fun _ ->
      let registered = testList "[Integration] rebuilt" [ testCase "a" ignore ]
      let rebuilt =
        match registered with
        | Test.TestLabel (name, inner, focus) -> Test.TestLabel (name, inner, focus)
        | other -> other
      testList "root" [ rebuilt; testCase "kept" ignore ]
      |> Integration.exclude [ registered ]
      |> names
      |> Expect.equal "the rebuilt root's tests are excluded by their bodies" [ "root/kept" ]

    testCase "unregisteredTagged reports tagged names that bypassed the registry" <| fun _ ->
      testList "root" [ testCase "[Integration] rogue" ignore; testCase "fine" ignore ]
      |> Integration.unregisteredTagged
      |> Expect.equal "the rogue tag is reported" [ "root/[Integration] rogue" ]

    testCase "every registered Host suite carries the integration tag" <| fun _ ->
      Integration.hostSuites ()
      |> List.collect names
      |> List.filter (fun n -> not (n.Contains "[Integration]"))
      |> Expect.isEmpty "host suites are tagged so --filter-test-list and logs identify them"

    testCase "the real-process suites CI must run are registered as Host" <| fun _ ->
      let hostNames = Integration.hostSuites () |> List.collect names
      // NB: "Actor split", "Eval cancellation" and "StartupConfig type and storage" are no
      // longer Host suites — the first two are now DST invariants (EvalActorSim) in the default
      // suite, the third is a pure default-suite testList after re-homing.
      [ "Daemon CLI subcommands"; "Daemon lifecycle"; "SessionManager lifecycle"
        "HTTP API"; "MCP Server Integration tests"
        "MCP session isolation"; "Reset isolation"; "Session reset"; "Falco web application tests"
        "Package/Namespace Explorer"; "checkFSharpCode backing function" ]
      |> List.filter (fun suite ->
        not (hostNames |> List.exists (fun n -> n.Contains("[Integration] " + suite))))
      |> Expect.isEmpty "every listed suite runs under --integration-host"
  ]
