module SageFs.Tests.Fixtures.HonestEmpties.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Sample.tests
