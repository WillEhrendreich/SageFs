# Things we looked at and said no to

Every roast round, somebody (human, agent, or an agent channelling a human) suggests the same five
ideas. This page is the bouncer's list. Each entry says what got turned away, why, and where the
evidence lives, so nobody has to re-litigate it from scratch. If you think one of these deserves a
second look, good: the last line of each entry says what would change the answer.

Two ground rules for this page. A decision with no evidence behind it doesn't get a line here. And
"we decided" only appears when something in the repo or in the project's recorded preferences backs
it up. Where a call is still open, it says so.

---

## The dashboard is one big morph, not a pile of little patches

The dashboard pushes the whole `#main` element over SSE and lets Datastar morph it. Every few
roasts someone proposes splitting that into per-panel fragment patches "for incremental updates".
Turned down. One authoritative render path is easier to reason about than a swarm of patchers that
can disagree with each other, and the payload cost belongs to the transport: compress the stream
(Brotli) and diff at the protocol level, leave the renderer alone.

The one render-side thing worth fixing is a timer push that fires when nothing changed, and that's
what the no-change suppression in the stream handler is for.

Evidence: the project's recorded preferences (the "fat morph" entry in `CLAUDE.md`'s taste notes);
`SnapshotRenderGuard` in `SageFs/Dashboard.fs`.
Reopen it if: someone measures a real latency or bandwidth problem that compression can't fix.

## Browser tests live in the F# suite, full stop

Playwright.NET inside Expecto. No separate TypeScript Playwright project humming along beside the
real tests, however tidy its folder looks. A second harness in a second language is a second place
for the truth to hide.

Evidence: `SageFs.Tests/DashboardBrowserTests.fs` and the `--integration-browser` tier;
the recorded preference in `CLAUDE.md`.
Reopen it if: a browser behaviour genuinely can't be driven from Playwright.NET.

## No standby worker pool

There used to be a pool of pre-warmed spare workers waiting to take over. The accounting never
worked out: the spares cost real memory and held ports, the boot contention cost startup time, and
the high-value case they were meant for (restart) was never the one wired to use them. Restart is now
spawn-first: start the replacement, wait for it to be ready, then swap, and the old worker keeps
serving until the new one is proven. Fewer moving parts and the same user-visible result.

Evidence: commits `553c70a3` and `6397cef9` (docs corrected after the removal);
`standby-session-rework.md` (the hand-off that did the removal).
Reopen it if: measured cold-start time, not a hunch, becomes the main complaint.

## Borrow Akka's ideas, don't borrow Akka

Supervision with backoff and child restart is a great idea. The Akka.NET package that comes with it
is a big dependency for a tool whose whole pitch is a small, inspectable closure. So the mechanisms
(restart policy, jitter, single-owner actors) are written in F# here, and nothing from Akka ships in
the daemon.

Same spirit for the supervisor itself: a thin F# one, not a native one in another language. A
second language in the stack has to earn its seat, and F# was enough to reach the isolation goal.

Evidence: the recorded preferences in `CLAUDE.md`; `SageFs.Core/RestartPolicy.fs`,
`ResilientActor.fs`.
Reopen it if: the F# supervisor can't hit a structural guarantee that a native one could.

## Kill the orphaned worker by watching the parent, not with a job object

Windows job objects are a fine way to make child processes die with their parent, on Windows.
SageFs runs on Linux and macOS too, so the worker polls its parent's pid instead. A hard kill of
the daemon (Task Manager, `kill -9`, a crash, a power cut) has to take the workers with it, and
"lookup failed" counts as "parent is dead", so a flaky probe can never orphan a worker.

Evidence: the recorded preferences in `CLAUDE.md`; the parent-death monitor in `SageFs.Core`.
Reopen it if: there's a cross-platform mechanism that's strictly stronger.

## When a project pins a version, adapt to it. Don't refuse.

"Fail loud" is a diagnosis, not a fix. If a user's project pins a different FSharp.Core or
System.Text.Json than the host was built with, the host re-initializes against the project's pins
where the APIs are compatible, swaps in version-matched variants where SageFs's own code is
coupled to the library, and moves feature dependencies out of the host entirely. Refusal is the
last-resort safety net before any eval, never the first answer.

Evidence: the recorded preferences in `CLAUDE.md`; the isolated FSI host design
(`SageFs.FsiHost`).
Reopen it if: an adaptation turns out to be unsound for a specific library.

## Live tweaking edits expressions, not just literals

The plan for phase three of live tweaking was briefly "scrub the numbers in the literals". That was
rejected as too small: an expression such as `gravity * 2.0` or `if hardMode then 80 else 100` is
just as editable as a number, and the source file gets the expression back, not a baked value.

Evidence: `live-tweak-spec.md`.
Reopen it if: the expression path can't be type-checked against the live value safely.

## Code changes land, state stays, nothing happens silently

The rule for hot reload: a redefined immutable gives you the new value; a live mutable keeps what
it has and tells you about it, with a way to reset it. The alternative, restarting on every edit
"to be safe", throws away exactly the state people wanted to keep, and the alternative where state
quietly survives without telling you is worse. Both are out.

Evidence: `hot-reload-state-spec.md`; `docs/hot-reload.md` (the what-reloads / what-restarts table,
each row pinned by a test).
Reopen it if: a class of state turns up that can't be kept or reported honestly.

## Dashboard navigation is a signal, not a URL

There used to be a `?session=` query parameter choosing what the page shows. It's gone. A Datastar
signal points at the session in view (or at nothing, which shows the picker), synced with the
backend. The URL is for things a human might bookmark on purpose, like `?panels=friction`, never for
which session happens to be selected.

Evidence: the recorded preferences in `CLAUDE.md`; `Signals.ViewingSessionId` in
`SageFs/DashboardTypes.fs`.
Reopen it if: people want deep links to a session badly enough to design them properly.

## Use the Datastar builders, not hand-typed attributes

Interactivity goes through the typed API (`Ds.signal`, `Ds.onInit (Ds.get ...)`,
`Response.ssePatchSignal`), not through strings that happen to look like `data-signals:...`. The
builders are the supported surface and the compiler checks them.

Evidence: the recorded preferences in `CLAUDE.md`; `SageFs/Dashboard.fs`.
Reopen it if: a needed attribute has no builder yet. Then add the builder.

---

## Agents get a door into the test engine, not a test runner of their own

`run_tests` existed, then got removed in March 2026 (commit `1961300a`, "MCP is read-only observer of
live testing"). With no verb, an agent that wants to run tests has to run `dotnet test` itself, which
is a second engine whose answers can disagree with `list_tests`, the dashboard and the cohort landing
gate. The sentence in `docs/mcp-tools.md` that called the removal
deliberate came from a voice-polishing pass, not from a recorded decision.

It's back, and the shape is the point: `run_tests` dispatches the same `RunTestsRequested` the
dashboard and editors send, tagged with a request id, and the receipt it returns is a pure read of the
engine's own record of that request (`RunRequests`, `ResultGenerations`, `LastResults`). There is one
source of truth and one more way in. It never counts a pass from an earlier run, it names every test
that didn't pass in this run and why, and `Incomplete` is not green.

Evidence: `SageFs/McpRunTests.fs`, `SageFs.Core/Features/TestRunReceipt.fs`, and
`SageFs.Tests/TestRunReceiptTests.fs`, which pins that the receipt and the cohort landing gate agree
on which tests did not pass.
Reopen it if: the engine can't answer a question an agent needs. Then extend the engine's record,
don't build a runner beside it.

### Still to do under this decision

The live loop picks affected tests by test name, which misses body-only edits, and the cohort landing
gate picks them from coverage bitmaps and fails closed. That's two selection rules over one engine.
They should be one, and the fail-closed one is the only acceptable choice.
