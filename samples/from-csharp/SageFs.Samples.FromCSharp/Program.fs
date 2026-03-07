module SageFs.Samples.FromCSharp.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Hello.tests
