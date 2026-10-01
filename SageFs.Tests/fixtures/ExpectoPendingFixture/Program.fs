module SageFs.Tests.Fixtures.ExpectoPending.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Sample.tests
