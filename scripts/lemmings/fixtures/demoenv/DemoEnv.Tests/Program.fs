module DemoEnvTestRunner

open Expecto

[<EntryPoint>]
let main argv =
  Tests.runTestsInAssemblyWithCLIArgs [] argv
