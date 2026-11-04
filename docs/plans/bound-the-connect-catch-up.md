# Bound what a connection is sent on connect

Roadmap item: `bound-the-connect-catch-up` (Next, Live testing). Plan written 2026-11-03 against master `105d4aac`.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run "Measure first" (one client, a settled `SageFs.Tests` session, read until the `retry:` hint and count frames and bytes by type). Then write the first failing test: a catch-up for a session with N files sends at most a fixed number of frames, whatever N is. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | The pinned `handleTestSummaryChange` thread has a likely cause that is now fixed but not yet re-measured: model changes ran as one pool work item each, so a second pass over every file started beside the first (`Coalescer`, roast finding 4). Re-measure on a settled `SageFs.Tests` session before step 7 is sized. |

Already fixed and shipped on the way here (`ba0b59be`): a catch-up stops at the next file or view once its client has left. That removed the worst symptom, a daemon pinned at three cores by catch-ups nobody was reading. It did not shrink what a client that stays is sent.

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

- `replayCachedTestState` (`SageFs/McpServer.fs`) projects every source-mapped file of the session: one `file_annotations` frame and one `coverage_view` frame per function, each written and flushed in turn. The cost grows with the number of files times the number of functions.
- Nothing about a client's open files reaches the daemon, so it sends everything.
- Before `ba0b59be`, a client that left did not stop this. Kestrel turns a write to a closed connection into a no-op that does not throw, so five catch-ups were still running at about three cores minutes after the last client had gone, and a new session on the same daemon would not finish warming up in four minutes.
- The live path has the same shape. `handleTestSummaryChange` projects every file again on each summary change. On the fixed daemon, with no client attached, one thread stayed in `handleTestSummaryChange` and `projectWithCoverage` at 102% of a core for over 40 seconds. Why it runs that long is not explained yet. One cause is fixed since: handlers ran once per model change, on separate pool threads, so passes overlapped (`Coalescer`, `SageFs.Core/ChangeCoalescer.fs`). A single pass over 1,629 files may still take that long, and a re-measure says.
- Live frames queue behind a long catch-up, because the catch-up writes straight to the socket and the queue is drained after it. If they pass the cap (1,024 frames or 32 MiB) the connection ends as soon as the catch-up is done, and the reconnect writes the same catch-up again. A short catch-up ends that loop, so it can repeat on a busy daemon until this item lands.
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

Let one client read `/events` on a settled `SageFs.Tests` session until the `retry:` hint arrives, and record the time, the frame count by type and the bytes by type. Also record how long `projectWithCoverage` takes for one file and how long `writeSseFrame` takes for its frames, so it is known whether the projection or the writing is the cost. If the projection dominates, the route needs a cache keyed by the run generation. If the writing dominates, one buffered write per file would help by itself.

Then find out why `handleTestSummaryChange` held a core for over 40 seconds with no client, with a thread dump (`dotnet-stack report -p <pid>`) taken twice a few seconds apart. If it is one long pass, step 7 is the fix. If it is a queue of passes, the handler needs to coalesce changes.

## Risks and dependencies

- The wire changes for both editors. The wire contract is ours and can change, so no compatibility shim is needed. Each in-repo client changes in the same stretch of work, and the plugin lands first.
- An editor that fetches on connect adds requests where it used to receive frames. The route must be cheap for a file with no annotations.
- `LiveTestingListener.fs` and `annotations.lua` both treat a connect as "clear and repopulate". The fetch has to repopulate the same way, or an editor shows stale marks after a reconnect.

## Size

M. Steps 1 to 6 are a week of work with two clients. Step 7 may be another.

## Decisions for Will

- Is the list of files in `annotations_available` the right size to put in a frame, or should the editor ask for the list too? For `SageFs.Tests` it is 1,629 paths, which I estimate at about 100 KB (not measured).
- Should the live path (step 7) land with this item or be its own?
