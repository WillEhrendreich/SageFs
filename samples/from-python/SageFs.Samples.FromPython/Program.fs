module SageFs.Samples.FromPython.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Hello.tests
