module SageFs.Tests.Fixtures.SourceState.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv DomainTests.tests
