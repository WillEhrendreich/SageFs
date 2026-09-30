# Why the REPL is the loop, and the loop in full

Read this when the loop is unclear, when you are tempted to run `dotnet build` or `dotnet fsi` in the middle of a task, or when you want to know which tool answers which question.

SageFs gives you a live F# REPL that already has the project loaded. An eval
takes milliseconds. A `dotnet build` takes tens of seconds to minutes, and a
test run takes longer. If you iterate by rebuilding, you spend almost all your
time waiting. So:

**The REPL is the inner loop. `dotnet build` / `dotnet test` / `dotnet run` is
the final gate, run once, when you're done.** Not a fallback, not "just to
check quickly", not for probing what an API looks like.

Agents drift back to the slow loop the moment the REPL feels awkward. That drift
is the failure this skill exists to stop. If you catch yourself typing
`dotnet build` in the middle of a task, stop and read troubleshooting.md, "When the REPL fights
you".

## The loop

1. **RED in the REPL.** `send_fsharp_code` with the smallest thing that shows
   the problem: the failing case, the wrong value, the bad parse. Watch it fail.
2. **GREEN in the REPL.** Redefine the function in the session until the case
   passes. Small blocks, each statement ending in `;;`.
3. **Persist.** Write the working code into the `.fs` file.
4. **Reload what you persisted.** `hard_reset_fsi_session` with `rebuild=true`
   rebuilds and reloads, so the session runs the real file. Only do this after
   persisting, or when an `.fsproj` changed (new files, new packages). Don't do
   it after every eval.
5. **Re-verify in the session**, then commit.
6. **Final gate:** the full build and the unfiltered test suite, once, at the
   end.

Useful tools while you're in the loop:
- `check_fsharp_code` type-checks without running anything. It's great for "does
  this compile" questions.
- `cancel_eval` stops a runaway eval. Don't reset the session for it.
- `explain_test_failure` and `targeted_verify` exercise real tests without
  building.
- **Want to know an API's or an AST's shape?** Ask the session: reflect over the
  type, parse a sample and print it. Never guess, and never spin up a
  throwaway `dotnet fsi` script to find out.
