module SageFs.Samples.FromJava.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Hello.tests
