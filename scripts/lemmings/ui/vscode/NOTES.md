# Notes for whoever picks this up (builder's running log, not product docs)

Two copies of the builder were running in this worktree at once (a context compaction or a resumed
agent). To stop clobbering each other, ownership was split by file. Read this before writing.

Copy A owned the DESIGN REVIEW additions: LemDrive/Tour.fs, Shot.fs, the shot/resize/tour edits in
Vsc.fs, VscCommand.fs, Cdp.fs and Snapshot.fs, LemDrive.Tests/, ui/vscode/tours/, ui/vscode/TOURS.md.
Copy B owned the RUNNER side: run-vscode-lemming, lib-vscode.sh, Shim.fs, VscHost.fs, Daemon.fs,
Oracle.fs, FellOver.fs, tasks/, VSCODE-TOOLS.md, README.md, the run-ui-lemming dispatch, and the smoke
run with a free model. Both: commit small, explicit paths, `git status --short` before any write.

## Where things stand (verifier round, 2026-10-01)

A verifier ran a free model through the runner and found the problems below. All are fixed at the
layer they belong to; the README says how each works.

- **The window bound to someone else's session.** The extension binds to the head of the daemon's
  session list (id order), so a lemming that pressed Alt+Enter evaluated in another agent's session.
  The runner now creates the run's own session (`LemDrive host session`), waits for it, attaches the
  window to it through the extension's own Switch Session picker and proves it from the Sessions view
  (`host bind`, pure checks in `Binding.fs`). No proof, no run (`HarnessError`). The prompts say a
  session is already running. The session is stopped by id with the rest and is not counted as residue
  (`LemScore cleanup --own`).
- **The Output channel read.** It timed out because the harness pressed a chord that the focused
  element ate, and then read only the dozen lines the editor had drawn, with no-break spaces in them.
  `OutputChannel.fs` opens the panel through the palette, picks the channel, reads from the end a page
  at a time until the pattern shows, and normalises the spaces. Proven on a live window, including with
  the panel closed, on Problems, and already on Output.
- **Shell and developer tools in the window.** `Guard.fs` refuses the palette entries, clicks and keys
  that open them; the profile points terminal profiles at `/usr/bin/false` and unbinds the keys; the
  window's sandbox mounts `out/`, `bin/` and the profile's settings and keybindings read-only. A
  denylist, not an allowlist, and not a boundary against a model that goes looking (README, "What is
  not protected").
- **The daemon is stoppable from the lemming's shell.** The sagefs shim is first on the lemming's PATH
  and the global tools directory is masked out of its sandbox. The HTTP routes and
  `dotnet <bridge>/SageFs.dll stop` are still reachable on an unisolated network, so the runner now
  compares the daemon's pid before and after and says so in `teardown.txt` (exit 6 on a change). The
  claim "the daemon is never stopped" is checked, not assumed.
- **NuGet and the credential.** The lemming's restores go to a per-run cache, with `~/.nuget/packages`
  as a read-only fallback. The Command Code credential stays readable inside the sandbox (the model call
  needs it); that is documented, not fixed.
- **Build skew.** The summary's version string names the daemon's build and the bridge's, and ends in
  `VERSION SKEW: ...` when they differ (`LemScore version-skew`).
- **processesLeft false positives.** `Leftovers.fs` counts a process only if it is a pid the runner
  started, a member of the session one led, or working inside the run directory.
- **Display race.** Choosing the display number and starting Xvfb is one step under a flock.
- **Driver rebuilds.** The run uses its own copy of LemDrive for every harness call; the copy is taken
  under the build lock.
- **LemScore Registration noise.** Not reported for editor harnesses (`Classify.forHarness`).
- **A crash in the window reader.** `List.zip` on two reads of a changing page threw and aborted the
  driver (`host ready` died with a core dump on one run). Reads are paired to the shorter list now, and
  `host ready` treats a failed read as another poll.

Still not proven with a model: a PASS for ui-edit-reeval, ui-live-tests and ui-hot-reload. The
ui-live-tests status-bar pattern (`\d+/\d+`) is a guess; tune it from a real run's final.snapshot.txt.
The session the runner now makes loads the tests project, so ui-live-tests' session check passes
without the lemming doing anything; its real check is the status bar. The fellOver `SessionCreate`
entry cannot fire any more for the same reason.

Open for the user: the real desktop's /tmp/.X11-unix/X0 was deleted once by an Xvfb that took :0 (an
early launch with -displayfd). It needs `ln -s X0_ /tmp/.X11-unix/X0` if it has not been restored.
SageFs.Tests still has an Xvfb helper that uses -displayfd (startVirtualDisplay); check before running
it on this machine. `/usr/share/code/code` is the Electron binary, not the `code` wrapper: run by hand, even with
`--version`, it starts VS Code (it did, on the real desktop's Wayland session) instead of printing a
version. Only the harness starts it, inside the sandbox.

Never start Xvfb with -displayfd or on :0 on this machine.
