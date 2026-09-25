module SageFs.Tests.TestingPlatformAdapterTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.TestingPlatform

let private mtpHelp =
  """Options:
  --list-tests       List available tests.
  --filter-uid       Provides a list of test node UIDs to filter by.
  --treenode-filter  Use a tree filter to filter down tests.
  --output           Output format.
  --no-banner        Hide startup text.
"""

[<Tests>]
let tests =
  testList "Microsoft Testing Platform adapter" [
    test "complete MTP surface is available" {
      Adapter.probeHelp mtpHelp
      |> Expect.equal "available" Capability.Available
    }

    test "legacy runner reports exact missing options" {
      let expectoHelp = "Usage: dotnet Tests.dll\nOptions:\n  --filter-test-list  Filter lists\n  --list-tests         List tests\n"
      Adapter.probeHelp expectoHelp
      |> Expect.equal "typed capability gap"
        (Capability.Unavailable [ "--filter-uid"; "--treenode-filter"; "--output"; "--no-banner" ])
    }

    testProperty "any nonblank UIDs produce one exact filter-uid argument" <|
      fun (NonEmptyString uid) ->
        let surface = Adapter.surface [ "dotnet"; "Tests.dll" ] mtpHelp
        match Adapter.runArguments surface [ uid ] with
        | Error _ -> failtest "available surface rejected a UID"
        | Ok args ->
          args |> List.contains "--filter-uid" |> Expect.isTrue "filter option"
          let expectedUid = args |> List.tryFindIndex ((=) uid)
          let filterIndex = args |> List.tryFindIndex ((=) "--filter-uid")
          expectedUid |> Expect.equal "UID follows its option" (filterIndex |> Option.map (fun index -> index + 1))

    test "available surface never runs without a UID" {
      let surface = Adapter.surface [ "dotnet"; "Tests.dll" ] mtpHelp
      match Adapter.runArguments surface [] with
      | Error message -> message |> Expect.stringContains "typed refusal" "at least one"
      | Ok _ -> failtest "empty run should be refused"
    }

    test "list arguments stay exact and deterministic" {
      let surface = Adapter.surface [ "dotnet"; "Tests.dll" ] mtpHelp
      Adapter.listArguments surface
      |> Expect.equal "list args"
        [ "dotnet"; "Tests.dll"; "--no-banner"; "--output"; "Detailed"; "--list-tests" ]
    }

    test "failed process termination preserves stderr" {
      let termination =
        Adapter.terminationOf { ExitCode = 7; StandardOutput = ""; StandardError = "MTP failed" }
      match termination with
      | Termination.TransportFailed message -> message |> Expect.stringContains "stderr" "MTP failed"
      | _ -> failtestf "unexpected termination: %A" termination
    }
  ]
