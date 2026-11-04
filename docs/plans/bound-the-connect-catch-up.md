# Bound what a connection is sent on connect

Roadmap item: `bound-the-connect-catch-up` (Next, Live testing). Plan written 2026-11-03 against master `105d4aac`.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done, released in 0.6.937. A connect sends the list of files with annotations in one frame, editors ask for the files they have open, and a running suite pushes only the files whose annotations changed. |
| Size | S to M (was M) |
| Branch and worktree | none; it is all on master |
| Last green commit | `6ce1cf81` (the 0.6.937 gate, every tier) |
| Next action | None for this item. Measure the file list on a project with far more annotated files than 868, and decide then whether the editor should ask for the list too. |
| Decisions made | The list of files stays in the `annotations_available` frame: 868 paths were 55 KB on `SageFs.Tests`. The live path (step 7) landed with this item. |
| Surprises | Measuring first changed the size of this item. A connect was slow because `projectWithCoverage` rebuilt, for every file, what does not depend on the file, not because of how many frames it sends. Fixing that took the catch-up on `SageFs.Tests` from "never reaches the `retry:` hint in 100 s" to 1.8 s. The same fault was the pinned `handleTestSummaryChange` thread: one thread, 100% of a core while idle with no client, in `buildRangeLookup` and `Map.add` (three stack samples three seconds apart). Idle CPU went from 30 CPU-seconds in 30 s to 0. |

Already fixed and shipped on the way here: a catch-up stops at the next file or view once its client has left (`ba0b59be`), which removed a daemon pinned at three cores by catch-ups nobody was reading; model changes run one at a time; and the catch-up lets its queue out between files, so a busy daemon cannot cut the connection as the catch-up ends.

## What done looks like

Connecting to `/events` costs a fixed, small amount of work and bytes however large the session is. An editor asks for the annotations of the files it has open and gets them when it asks. The `retry:` hint, which tells a client it is caught up, arrives in seconds on `SageFs.Tests` (1,629 files, 17,883 tests), and a test pins that with a synthetic session of ten thousand files.

## What exists today

Measured on 2026-11-03 against daemon 0.6.936, a live-testing session on `SageFs.Tests`, one `curl` client reading `/events`:

| Time after connect | Bytes read | `retry:` hint seen |
|:--|--:|:--|
| 5 s | 132,211 | no |
| 20 s | 348,049 | no |
| 60 s | 1,307,905 | no |
| 100 s | 2,694,616 | no |

At 100 s the catch-up had sent 30 `file_annotations` frames and 3,486 `coverage_view` frames (about 116 views per file) and was still going. Its end is not known.

Measured again on 2026-11-04, same project and client, with the per-file rebuild fixed (the daemon built from master, a live-testing session on `SageFs.Tests` with the suite running, and then settled):

| What | Before the fix | After the fix |
|:--|:--|:--|
| Catch-up, suite running, 60 s | 229 files, 88 CPU-seconds, no `retry:` hint | 866 files, 3.3 MB, `retry:` reached, 17 CPU-seconds |
| Time to the `retry:` hint, settled | not reached in 100 s | 1.8 s (2,634 lines, 908 files) |
| Idle daemon, no client, settled | 30 CPU-seconds in 30 s | 0 CPU-seconds in 30 s |
| Live frames on a settled stream after the catch-up | not measured | none in 45 s |

That left size: about 3.6 KB and one frame per file with annotations, so a connect to a 10,000-file session would have sent about 36 MB and taken about 20 s. The design below bounds that, and it is built.

Measured on 2026-11-04 against the installed 0.6.937 daemon (`6ce1cf81`), a live-testing session on `SageFs.Tests` with 17,973 tests discovered and none run yet, one `curl` client reading `/events` for 12 s:

| What | Result |
|:--|:--|
| Time to the first byte | 0.018 s |
| Frames on connect | 14 |
| Bytes on connect | 115,913 |
| Of those, `annotations_available` | 1 frame, 868 files, 55,439 bytes |
| Of those, the three `session` frames | 58,422 bytes |
| Of those, the test summary and batch | 296 and 592 bytes |

The list is the largest single frame. It is about 64 bytes a path, so a session with 10,000 annotated files would send a list near 640 KB, which one frame can carry. Before the change, a connect to the same project while its suite ran was 3.3 MB after 60 s.

- `replayCachedTestState` (`SageFs/McpServer.fs`) projects every source-mapped file of the session: one `file_annotations` frame and one `coverage_view` frame per function, each written and flushed in turn. The cost grows with the number of files times the number of functions.
- Nothing about a client's open files reaches the daemon, so it sends everything.
- Before `ba0b59be`, a client that left did not stop this. Kestrel turns a write to a closed connection into a no-op that does not throw, so five catch-ups were still running at about three cores minutes after the last client had gone, and a new session on the same daemon would not finish warming up in four minutes.
- The live path has the same shape. `handleTestSummaryChange` projects every file again when the test or coverage state changes. With no client attached, one thread stayed in `handleTestSummaryChange` and `projectWithCoverage` at 102% of a core for over 40 seconds. That was `projectWithCoverage` rebuilding what does not depend on the file, once per file (fixed: `FileAnnotations.prepare` and `projectFile`). Handlers also ran once per model change on separate pool threads, so passes overlapped (fixed: `Coalescer`, `SageFs.Core/ChangeCoalescer.fs`). The live path still projects every file on a change, now in a fraction of the time; step 7 narrows it to the files a change touched.
- Live frames used to queue behind a long catch-up, because it writes straight to the socket and the queue was drained after it, so a busy daemon filled the queue, the connection was cut as the catch-up ended, and the reconnect wrote the same catch-up again. Fixed: the catch-up lets the queue out between files, without ids (`SseOutbox.drainQueued`). The catch-up is still as long as it was; this item is what shortens it.
- There is a precedent for the fix. `test_results_batch` used to carry every entry and now carries a summary, with the list on `GET /api/live-testing/entries` (`mapEventsRoute`).
- Clients that read these frames: `sagefs-vscode/src/LiveTestingListener.fs` and, in the Neovim plugin, `lua/sagefs/annotations.lua` (`handle_file_annotations`), `coverage.lua` and `coverage_hover.lua`.

## Design

Move per-file annotations from the stream to a request, the way entries moved.

1. A route, `GET /api/live-testing/annotations?session=<id>&file=<path>`, answers with that file's `file_annotations` payload and its `coverage_view` list, built by the same projection `replayCachedTestState` runs today.
2. The connect-time catch-up stops projecting files. It sends the summary, the batch, and a small `annotations_available` frame: the session, the run generation, and the list of files that have annotations (paths only).
3. An editor fetches annotations for the files it has open, on connect and when the generation moves, and again when it opens a file.
4. The live path pushes `file_annotations` and `coverage_view` for the files a change touched, which `handleTestSummaryChange` can know from the edit, and stops re-projecting the whole session.

Step 4 is the larger one and has its own measurement (below). Steps 1 to 3 remove the connect cost without it.

## Tests

- A catch-up for a model with 10,000 source-mapped files writes at most a fixed number of frames. This is the failing test to write first. It uses the model fixture in `SageFs.Tests/EventsCatchUpCancellationTests.fs`.
- The route answers the same payload `replayCachedTestState` wrote for a file, for a file with annotations and for one without.
- Each editor's golden test: given the `annotations_available` frame, it fetches only its open files.
- A simulation in `SageFs.Simulation`: a seeded run of connects, drops and edits, where a client that fetches on connect and on each generation always ends up with the annotations a client connected the whole time has. A twin that skips the generation fetch must fail it.

## Steps

1. Measure first (below).
2. Failing test: the frame bound for a 10,000-file session.
3. The route and its tests.
4. The `annotations_available` frame, and the catch-up change. Commit with `Plugin: done` once the plugin side is ready, because the plugin lands first.
5. `sagefs.nvim`: fetch for open buffers.
6. VS Code: fetch for open editors.
7. The live path: push only the files a change touched. This step has its own measurement and may split off.

## Measure first

Done on 2026-11-04; the numbers are under "What exists today" and the finding is in "Surprises". The text below is how it was done, for the next measurement.

Let one client read `/events` on a settled `SageFs.Tests` session until the `retry:` hint arrives, and record the time, the frame count by type and the bytes by type. Also record how long `projectWithCoverage` takes for one file and how long `writeSseFrame` takes for its frames, so it is known whether the projection or the writing is the cost. If the projection dominates, the route needs a cache keyed by the run generation. If the writing dominates, one buffered write per file would help by itself.

Then find out why `handleTestSummaryChange` held a core for over 40 seconds with no client, with a thread dump (`dotnet-stack report -p <pid>`) taken twice a few seconds apart. If it is one long pass, step 7 is the fix. If it is a queue of passes, the handler needs to coalesce changes.

## Risks and dependencies

- The wire changes for both editors. The wire contract is ours and can change, so no compatibility shim is needed. Each in-repo client changes in the same stretch of work, and the plugin lands first.
- An editor that fetches on connect adds requests where it used to receive frames. The route must be cheap for a file with no annotations.
- `LiveTestingListener.fs` and `annotations.lua` both treat a connect as "clear and repopulate". The fetch has to repopulate the same way, or an editor shows stale marks after a reconnect.

## Size

M. Steps 1 to 6 are a week of work with two clients. Step 7 may be another.

## Decisions for Will

- Answered: the list stays in the frame. For `SageFs.Tests` it is 868 paths and 55,439 bytes, measured on 2026-11-04.
- Answered: the live path (step 7) landed with this item, in `c57aa875`.
