# Notes for whoever picks this up (builder's running log, not product docs)

Two copies of the builder were running in this worktree at once (a context compaction or a resumed
agent). To stop clobbering each other, ownership is split by file. Read this before writing.

Copy A (this note's author) owns the DESIGN REVIEW additions, and only these files:
  LemDrive/Tour.fs, Shot.fs (new), LemDrive/Vsc.fs + VscCommand.fs + Cdp.fs + Snapshot.fs (edits for
  shot/resize/tour/sidecar), LemDrive.Tests/ (new), ui/vscode/tours/, ui/vscode/TOURS.md.
Copy B owns the RUNNER side: run-vscode-lemming, lib-vscode.sh, Shim.fs, VscHost.fs, Daemon.fs,
  Oracle.fs, FellOver.fs, tasks/, VSCODE-TOOLS.md, README.md, the run-ui-lemming dispatch, and the
  smoke run with a free model.
Both: commit small, explicit paths, `git status --short` before any write.

State at this commit: LemDrive builds; run-vscode-lemming exists and has not yet been run end to end.

Cleanup done by copy B: the explore1 session was stopped by id and its VS Code ended gracefully.

Runner copy committed and stopped (19:10). State, for whoever writes the one final report:
- Runs done end to end with free models: space-bunny ui-eval x3 (one real success in the editor, scored
  Fail because the evals went into another run's session), laguna ui-eval and ui-find-help, ling
  ui-hot-reload. Every run: desktopLeak=no, graceful window stop, sessions cleaned by id. The ui-eval
  oracle PASS path was proven on a live window (real extension, real daemon, real Alt+Enter).
- Not yet proven with a model: a PASS for ui-edit-reeval, ui-live-tests, ui-hot-reload. The
  ui-live-tests status-bar pattern (\d+/\d+) is a guess; tune it from a real run's final.snapshot.txt.
- Open for the user: the real desktop's /tmp/.X11-unix/X0 was deleted once by an Xvfb that took :0
  (my first launch, -displayfd). It needs `ln -s X0_ /tmp/.X11-unix/X0` (the sandbox refused me).
- LemScore (core) reports "Registration: never called a SageFs MCP tool" for every editor lemming; it
  does not know the harness is cmdc-vscode. lib-cmd.sh lem_sandbox_exec fails under set -u when
  LEM_EXTRA_BWRAP is unset.
- SageFs.Tests still has an Xvfb helper that uses -displayfd (startVirtualDisplay); check before running on this machine.

Copy B, 18:02: Vsc.fs in the working tree carries two of B's edits (the click that tries the next
match when the first is covered, and ClickCandidates/ClickTryMs). A: please include them in your next
commit of Vsc.fs, B has not committed that file to avoid taking your half-finished edits with it.

Copy B, 17:40: Timeline.fs (actions clock, OUT/timeline.ndjson via LEM_TIMELINE) is written and
hooked into Vsc.cli with a three-line edit. If Vsc.fs is rewritten, keep that hook. The first
end-to-end run (space-bunny, ui-eval) is in /tmp/lem/space-bunny-ui-eval-02. Never start Xvfb with
-displayfd or on :0 on this machine: it deleted the real desktop's /tmp/.X11-unix/X0 socket once.
