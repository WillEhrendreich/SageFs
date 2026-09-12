module SageFs.Demos.Tests.Program

open Expecto

// Auto-discovers every `[<Tests>]`-attributed test list in this assembly
// (DomainTests, ScheduleTests, FingerprintTests, …) instead of naming one
// module by hand — so a new test file only needs a `<Compile>` entry in the
// fsproj, never a Program.fs edit too.
[<EntryPoint>]
let main argv = Tests.runTestsInAssemblyWithCLIArgs [] argv
