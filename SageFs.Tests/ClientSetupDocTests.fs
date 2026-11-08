/// docs/client-setup.md holds the registration snippets that `sagefs setup` prints. Its generated part is
/// `ClientSetup.page`, written by `scripts/gen-client-setup-doc.fsx`. This fails when the page and the code differ, so the
/// text a person copies is the text the command prints.
module SageFs.Tests.ClientSetupDocTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs

let private repoRoot = RepoPaths.repoPathFull [||]

[<Tests>]
let tests =
  testList "Client setup page" [

    testCase "WHY - the setup page in docs is what the generator prints now" <| fun _ ->
      let doc = File.ReadAllText(Path.Combine(repoRoot, "docs", "client-setup.md"))
      match ClientSetup.replaceGenerated doc ClientSetup.page with
      | Ok regenerated -> regenerated |> Expect.equal "docs/client-setup.md is stale: run dotnet fsi scripts/gen-client-setup-doc.fsx" doc
      | Error reason -> failtest (ClientSetup.describeMarkerRefusal reason)
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
