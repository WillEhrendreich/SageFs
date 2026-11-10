module SageFs.Tests.Fixtures.ConfirmKeepsRepl.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Sample.tests
