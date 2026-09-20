module SageFs.Tests.Fixtures.McpToolOutcome.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Sample.tests
