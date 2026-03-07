module SageFs.Samples.FromJupyter.Program

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv Notebook.tests
