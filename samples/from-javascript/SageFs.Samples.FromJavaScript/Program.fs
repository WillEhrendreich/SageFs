module SageFs.Samples.FromJavaScript.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Hello.tests
