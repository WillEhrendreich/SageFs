# Running tests and slow gates

Read this when you are about to run tests, wait on a build or a test suite, or decide whether a green result actually covered anything.

Running tests from the session: evaluate
`Expecto.Tests.runTestsWithCLIArgs [] [| "--filter-test-case"; "name" |] MyTests.tests`.
It returns the exit code. The runner's console output goes to the worker, so if
you need the details, use `explain_test_failure`.

### Don't await a slow gate

A full test suite, a build, or `scripts/local-gate` takes minutes. Blocking on
one — or polling it in a loop — is the same waste as having run it inline, and
it blocks the actual work.

- **Launch it, then keep working.** Use a background agent or a background
  shell task, and read the result when you need it.
- **The release gate is a decision, not an iteration tool.** Do not re-roll it
  to escape a flake; that costs minutes and proves nothing. Isolate the
  suspicion once with a cheap filtered run, and report the evidence.
- **A filtered run is never the acceptance check.** `--filter` matching nothing
  still prints `Failed: 0` and **exits 0**. Read the `TRUST` line's
  `ran=` count, not the absence of red.
