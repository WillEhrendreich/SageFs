module SageFs.Samples.ConsoleTicker.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
  runTestsWithCLIArgs [] argv SageFs.Samples.ConsoleTicker.Tests.TickerTests.tests
