# Notes for whoever picks this up (builder's running log, not product docs)

Done and committed or in the tree:
- LemDrive (F#): Outcome, Chord, Guard, VscCommand, Snapshot, Cdp, Vsc, Daemon, FellOver, Oracle, Program.
- lib-vscode.sh: Xvfb (:80+, never :0), VS Code in bwrap, extension copy, profile, tool wrappers.
- run-vscode-lemming: the runner. tasks/*.md: the five lemming tasks. VSCODE-TOOLS.md: the lemming's README.

Next, in order:
1. Smoke the runner with a free model (done when out/summary.json exists and hyprctl shows no leak).
2. Design-review additions: `vsc shot <name> [--region R]`, `vsc resize W H`, `vsc tour <file>` and Tour.fs
   (parser tests in LemDrive.Tests), sidecar with status bar, tree rows, notifications, activation time.
3. Example tours under tours/ for the demoenv fixture, each at two sizes.
4. run-ui-lemming dispatch (separate function), README.md, merge the Neovim branch (worktree-wf_790b15e9-824-2).

Cleanup owed: sessions under /tmp/lem/explore1 on the shared daemon, and the VS Code started from
scratchpad/explore-start.sh (touch /tmp/lem/explore1/out/stop ends it).
