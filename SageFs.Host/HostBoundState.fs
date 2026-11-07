namespace SageFs.Server

open System.Threading

/// What the worker holds about the code in its FSI host, and so loses its meaning when the host is replaced.
///
/// A reset disposes the host and builds a new one. The worker kept four things that describe the old one: the run-test
/// function an editor buffer's tests defined (it points into the disposed host), the tests that buffer discovered, and the two
/// records of which modules have been evaluated into the host (one for the buffer evals, one for the file watcher's, which
/// `preprocessForFsi` reads to decide what a file needs opened or wrapped). None of them was cleared, so after any reset a test
/// run could be handed to a disposed host and a file evaluated as if its modules were already there. `Forget` empties all four,
/// and every reset the worker performs calls it.
type HostBound() =
  let mutable runTest : (SageFs.Features.LiveTesting.TestCase -> Async<SageFs.Features.LiveTesting.TestResult>) option = None
  let mutable discovery : SageFs.Features.LiveTesting.TestCase array = [||]
  let mutable bufferEvals = SageFs.Middleware.CompilationContext.CompilationState.empty
  let mutable watcherEvals = SageFs.Middleware.CompilationContext.CompilationState.empty

  /// The run-test function tests defined from an editor buffer's eval left in the host, if any.
  member _.RunTest = Volatile.Read(&runTest)
  member _.SetRunTest (value: SageFs.Features.LiveTesting.TestCase -> Async<SageFs.Features.LiveTesting.TestResult>) : unit =
    Volatile.Write(&runTest, Some value)

  /// The tests the latest buffer eval discovered.
  member _.Discovery = Volatile.Read(&discovery)
  member _.SetDiscovery (tests: SageFs.Features.LiveTesting.TestCase array) : unit = Volatile.Write(&discovery, tests)

  /// Which modules the buffer evals have put in the host, and the parse cache beside it.
  member _.BufferEvals = Volatile.Read(&bufferEvals)
  member _.SetBufferEvals (state: SageFs.Middleware.CompilationContext.CompilationState) : unit = Volatile.Write(&bufferEvals, state)

  /// The same for the file watcher's evals.
  member _.WatcherEvals = Volatile.Read(&watcherEvals)
  member _.SetWatcherEvals (state: SageFs.Middleware.CompilationContext.CompilationState) : unit = Volatile.Write(&watcherEvals, state)

  /// The host was replaced: nothing that described the old one is true of the new one.
  member _.Forget () : unit =
    Volatile.Write(&runTest, None)
    Volatile.Write(&discovery, [||])
    Volatile.Write(&bufferEvals, SageFs.Middleware.CompilationContext.CompilationState.empty)
    Volatile.Write(&watcherEvals, SageFs.Middleware.CompilationContext.CompilationState.empty)
