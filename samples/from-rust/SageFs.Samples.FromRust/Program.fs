module SageFs.Samples.FromRust.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Hello.tests
