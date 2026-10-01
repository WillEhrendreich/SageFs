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

Cleanup owed (copy B or whoever sees it first): sessions under /tmp/lem/explore1 on the shared daemon,
and the VS Code started from scratchpad/explore-start.sh (touch /tmp/lem/explore1/out/stop ends it).
