module RingBufferOracle.Program

open Expecto

/// Runs the RingBuffer tests the copy carries. Expecto's own exit code is the verdict, and
/// the run is unfiltered, so a typo in a filter can never turn this green by running nothing.
[<EntryPoint>]
let main argv = runTestsInAssemblyWithCLIArgs [] argv
