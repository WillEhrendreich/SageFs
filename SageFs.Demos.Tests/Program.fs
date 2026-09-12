module SageFs.Demos.Tests.Program

open Expecto

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv SageFs.Demos.Tests.DomainTests.tests
