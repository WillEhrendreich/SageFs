# Start on login for Windows

Roadmap item: `start-on-login-windows` (Later, Platform). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S for files and tests, M with a Windows check |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: the task definition parses. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

A Windows user runs one PowerShell block from the Readme, signs out and in, and the SageFs daemon is up: `sagefs status` exits 0, the dashboard answers on 37750, and a session created without a working directory lands in a folder SageFs will watch, never `C:\Windows\System32`. Killing the supervisor brings it back. Running the block a second time picks up a changed `PATH` or a moved .NET folder. Stopping and removing the service each take one documented command.

The outcome gate is a Windows CI leg (the `windows-x64` leg of `hot-reload-on-windows-and-macos`) that installs the task, starts it with `Start-ScheduledTask` (the same action a logon trigger runs), waits for the daemon to answer on a free port pair, reads the daemon's own `workingDirectory` from `/api/daemon-info`, ends the task, checks nothing is left running, and unregisters. Matrix: a global .NET install and a per-user one (`DOTNET_ROOT` set, not on the registry), a `PATH` with a space in it, a `PATH` of more than 8,191 characters, a `PATH` containing `%`. The logon trigger itself cannot fire on a hosted runner, and the standard-user case cannot be tested there because the hosted account is an administrator. Both are checked by hand once (see Measure first) and written down as untested by CI.

## What exists today

The macOS item is built and is the template. I read:

- `contrib/launchd/io.github.willehrendreich.sagefs.plist`. A user agent that runs `@HOME@/.dotnet/tools/sagefs --supervised`, with `RunAtLoad`, `KeepAlive { SuccessfulExit = false }`, `ThrottleInterval 5`, `WorkingDirectory` set to `@HOME@/Library/Application Support/sagefs-daemon`, `DOTNET_ROOT` and `PATH` set as placeholders (`@DOTNET_ROOT@`, `@PATH@`), and stdout and stderr sent to `~/Library/Logs/sagefs/`. Its header comment holds the install block, line for line the one in the Readme.
- `contrib/systemd/sagefs.service`. `Restart=on-failure`, `RestartSec=5`, `StateDirectory=sagefs` with `WorkingDirectory=%S/sagefs`, `DOTNET_ROOT=%h/.dotnet`, and a drop-in `path.conf` the install writes from the install shell's `PATH`.
- `SageFs.Tests/FirstRunMacOSTests.fs` (and `FirstRunLinuxTests.fs`). They parse the shipped file and assert: it exists and its label matches, it starts at login and restarts after a crash, `DOTNET_ROOT` and `PATH` are set, the working directory passes `FileWatcher.classifyWatchRoot` and sits outside the data folder, every placeholder has a matching `sed` in the Readme, the file's comment repeats the Readme block exactly, and the program arguments name a flag the CLI help lists.
- `Readme.md` has `### Run it as a service (Linux)` (line 198) and `### Run it as a service (macOS)` (line 217). The Get Started list links only the Linux anchor (line 162). Nothing mentions Windows. `docs/progress.md` line 278 says login startup is Linux only.

What the pitfalls the Linux and macOS files already learned came from (their comments say so):

| Pitfall | Linux unit | macOS agent |
|---|---|---|
| A service does not read the shell profile, so `PATH` is the OS default and an `init.fsx` that starts `npm` fails | drop-in written from the install shell's `PATH` | `@PATH@` filled by `sed`; the install stops on a `PATH` holding a pipe, ampersand, less-than, backslash or line break |
| The tool's launcher finds the runtime through `DOTNET_ROOT` | `Environment=DOTNET_ROOT=%h/.dotnet` | `@DOTNET_ROOT@` from `realpath $(command -v dotnet)` |
| A daemon started in `$HOME` hands `$HOME` out as a session root and SageFs refuses to watch it | `StateDirectory` plus `WorkingDirectory` | `mkdir` of a sibling of the data folder |
| Rewriting the definition under a loaded one fails | `daemon-reload`, `restart` | `bootout` first, then rewrite, then `bootstrap` |
| Output goes somewhere | journal | `~/Library/Logs/sagefs/` |

The supervisor is `SageFs/WatchdogRunner.fs`. `run` loops on `Watchdog.decide` (`SageFs.Core/Watchdog.fs`), starts the daemon as a child with `ProcessStartInfo(exePath, args, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = false)` where `exePath` is `Process.GetCurrentProcess().MainModule.FileName`, and gives up (the process then exits 0) after repeated crashes. `Program.fs` line 259 reads `--supervised`; the watchdog is handed `Environment.CurrentDirectory`, so the daemon's cwd is the task's working directory.

Windows facts I read in code, relevant here:

- `FileWatcher.classifyWatchRoot` (`SageFs.Core/FileWatcher.fs` line 365) refuses a filesystem root and the home directory or anything above it. It does not refuse `C:\Windows\System32`. A task with a blank "Start in" runs there, so on Windows a wrong working directory is silent, which is worse than Linux where the daemon at least logs a refusal.
- The data folder is `~/.SageFs` (`DataDirChoice.dataDirectory`) and the daemon log sits in `%LOCALAPPDATA%\SageFs` (`DaemonLog.userLogDirectory`, `SpecialFolder.LocalApplicationData`). A working directory inside either would make the daemon watch its own writes, the reason the macOS agent uses a sibling.
- `DaemonState.isProcessAlive` uses `Process.GetProcessById`, which is portable. I did not read the whole `sagefs stop` path on Windows.
- There is no Windows login startup anywhere in the tree: `rg -i "schtasks|ScheduledTask|Startup folder|shell:startup"` over `*.fs`, `*.fsx`, `*.md` and `*.yml` finds nothing outside this plan.

## Design

### The three candidates

| | Task Scheduler (logon task) | Startup folder shortcut | Windows service |
|---|---|---|---|
| Admin needed to install | No for a task of your own (to confirm, see below) | No | Yes |
| Runs as the user, with their profile, their `dotnet` and their project folders | Yes (interactive token) | Yes | Not by default. LocalSystem has another profile; a user account needs a stored password |
| Restart after the supervisor crashes | `RestartOnFailure`, minimum interval one minute | No | Yes (recovery actions) |
| Console window | Shows unless hidden by the launcher | Shows (can start minimized) | None, session 0 |
| Needs SageFs to speak the service protocol | No | No | Yes: the process must answer the Service Control Manager (error 1053 otherwise), which means a hosting package, and AGENTS.md says no new NuGet dependency without discussion |
| Stop and remove | `Stop-ScheduledTask`, `Unregister-ScheduledTask` | delete the shortcut | `sc stop`, `sc delete`, admin |

A Windows service is out: it cannot reach the user's project folders and tools without a password, it needs admin, and it adds a dependency. A Startup shortcut is the zero-effort fallback but has no restart, and the supervisor already covers daemon crashes, so the only loss is a supervisor crash; I would still document it as the no-task option. The recommendation is a per-user logon task, installed from XML.

### Files

- `contrib/windows/sagefs-task.xml`. A Task Scheduler 1.2 definition (`xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"`): `LogonTrigger` for the current user; principal `InteractiveToken`, `LeastPrivilege`; settings `MultipleInstancesPolicy IgnoreNew`, `DisallowStartIfOnBatteries false`, `StopIfGoingOnBatteries false`, `ExecutionTimeLimit PT0S`, `RestartOnFailure` (one minute, many counts), `AllowStartOnDemand true`; one `Exec` whose command is the launcher and whose `WorkingDirectory` is the daemon folder. Two of those settings are Task Scheduler defaults that would silently break the service: the default execution time limit is 72 hours (the supervisor would be killed every three days), and a laptop on battery does not start the task.
- A launcher `%LOCALAPPDATA%\sagefs-daemon\launch.cmd`, written by the install, that reads the captured `PATH` and `DOTNET_ROOT` and runs `%USERPROFILE%\.dotnet\tools\sagefs.exe --supervised`. The captured values go in two small files (`path.txt`, `dotnet-root.txt`) read with `set /p`, so no quoting rule applies to them and the 8,191-character `cmd` line limit does not either. Task Scheduler's action has no environment block, which is why the launcher exists; the Linux drop-in and the macOS placeholders solve the same problem with what their service manager offers.
- A README section `### Run it as a service (Windows)` with a ```powershell block, and the same block in the XML's leading comment, so the existing test shape ("the comment carries the README's install block, line for line") carries over. The Get Started line 162 links all three anchors.

The install block, in order: stop if `$env:PATH` or the dotnet root cannot be captured and say why (the same fail-closed rule as the other two); `Stop-ScheduledTask` and `Unregister-ScheduledTask` any old task (the unload-before-rewrite lesson); create `%LOCALAPPDATA%\sagefs-daemon`; write `path.txt`, `dotnet-root.txt` and `launch.cmd`; read the XML, fill placeholders, `Register-ScheduledTask -Xml`; `Start-ScheduledTask`. Register from a string with `-Xml` so the file's encoding does not matter (`schtasks /Create /XML` is strict about UTF-16).

### Pure core

The task XML and the launcher text are static files with a few placeholders, as in the macOS item, so there is no logic to put in F#. If the placeholder approach grows, a pure `WindowsLoginTask.render : TaskSpec -> string` in `SageFs.Core` with an XML-escaping test is the next step, and it would let one `sagefs service install` subcommand serve all three OSes. That is a bigger change (a CLI verb, tests, help text) and is left as a decision below.

### Wire, clients, format

No daemon wire change, no client change, no persisted format change. No new MCP tool. The commit touches `Readme.md`, `docs/`, `contrib/windows/`, and tests; none is on the wire-surface list, so no `Plugin:` trailer is needed.

## Tests

RED first, all runnable on Linux because they parse files:

1. `FirstRunWindowsTests.fs`, modeled on `FirstRunMacOSTests.fs`. First case: "WHY - contrib/windows/sagefs-task.xml exists and parses as a Task Scheduler definition". Fails today because the file does not exist.
2. "starts at logon and is not killed after 72 hours": asserts a `LogonTrigger`, `ExecutionTimeLimit` equal to `PT0S`, `DisallowStartIfOnBatteries` and `StopIfGoingOnBatteries` false, `RestartOnFailure` present.
3. "WorkingDirectory is a folder SageFs will watch, outside its data and log folders, and one the install creates": strips the placeholder, checks it is not `System32`, not the profile, not `%LOCALAPPDATA%\SageFs` (case-insensitively), and that the README block creates it. The real `classifyWatchRoot` call cannot take a `C:\` path on a Linux runner (`Path.GetFullPath` treats it as relative), so the Windows leg repeats this case against real paths.
4. "every placeholder is one the README install fills in", and "the task's comment carries the README's install block, line for line" (a regex on ```powershell instead of ```bash).
5. "the Exec runs the installed tool with a flag the CLI help lists": the launcher's last line holds `--supervised`, and `Program.main [| "--help" |]` lists it.
6. "the install stops, and says why, when `dotnet` or `PATH` cannot be captured" is a text assertion on the README block (it has a failing branch with a message), the weakest test here. The real check is the Windows leg.

Windows leg (needs the platform job from `hot-reload-on-windows-and-macos`): a case registered only on Windows (as `4609ff06` does for other tests: the case does not exist elsewhere, it does not skip) that runs the README block extracted from the file, so the doc and the test cannot drift. It ends its wait on a state change, not a sleep: it polls `DaemonState.readOnPort` and stops at `DaemonPresence.Running`. The only deadline is a ceiling that turns a dead start into a failure carrying the tail of the daemon log. Then it ends the task, checks the daemon's pid is gone, unregisters, and fails if any `sagefs` process remains.

Simulation: the supervisor is already simulated (`SageFs.Simulation/SupervisorSim.fs`, `SupervisorWatchdogSim.fs`). The Windows item adds no new decision logic, so it adds no new simulation. If `Watchdog` changes to detect "a daemon already holds the port" (see Risks), that change gets a case in `SupervisorWatchdogSim` with a twin that restarts forever.

## Steps

1. Test 1 plus the minimal `contrib/windows/sagefs-task.xml`. Green on Linux.
2. Tests 2 and 3 with the full settings and the working directory placeholder.
3. The launcher and the README block, tests 4 and 5. Add the Windows anchor to Get Started line 162 and fix `docs/progress.md` line 278. The roadmap item gets a landmark in `scripts/roadmap/RoadmapItems.fs`, as the macOS one has, in the commit that ships it.
4. Hand verification on a real Windows machine for the Measure first questions. Results go into the XML's comment.
5. The Windows CI case, after the platform job exists. Steps 1 to 3 touch only `contrib/windows/`, the Readme and the new test file, so one agent owns all of them; step 5 owns the test file's Windows section and the pipeline wiring and cannot start before the platform leg.

## Measure first

These need a Windows machine; the first three decide the design:

- Does Task Scheduler expand `%USERPROFILE%` and `%LOCALAPPDATA%` in `Command`, `Arguments` and `WorkingDirectory`? Experiment: register a task whose working directory is `%LOCALAPPDATA%\sagefs-daemon` and read the process's cwd. If yes, the XML needs no placeholder and the install block shrinks. If no, copy the macOS `@HOME@` approach.
- Is the console window visible, and which launcher hides it? Try, on one machine: plain `cmd.exe`; `powershell -WindowStyle Hidden`; `conhost.exe --headless cmd.exe /c ...` (I recall this flag exists on current Windows 10 and 11; unverified); and `CreateNoWindow = true` for the child in `WatchdogRunner.startDaemon`. Pick the one with no window and no flash, and record the Windows builds tried.
- Does `Register-ScheduledTask` with a `LogonTrigger` work for a standard (non-admin) user for their own task? I believe yes, and I believe `schtasks /Create /SC ONLOGON` may demand elevation; I have not run either.
- What happens to the daemon child when the task is ended (`Stop-ScheduledTask`) or the user signs out? If the daemon survives its supervisor, the next logon starts a second supervisor, whose daemon fails to bind and backs off. Experiment: end the task and look for the child. If it survives, the launcher must put both processes in a job object or the watchdog must kill its child on exit.
- Does `RestartOnFailure` fire for a non-zero exit only? `WatchdogRunner` gives up by returning, and `Program.fs` then returns 0, so a given-up supervisor stays down, as in the launchd item. Confirm the same on Windows.
- How loud is the console output (bytes per hour while idle)? The launcher can redirect to a file or to `NUL`; unbounded growth in a log file the daemon does not roll is not acceptable.
- Does `Process.GetCurrentProcess().MainModule.FileName` name `sagefs.exe` when started through the tool shim? `startDaemon` depends on it.

## Risks and dependencies

- The 8,191-character `cmd` limit and the `%` handling in `PATH` are why the launcher reads files; if `set /p` mishandles a long line or a `%`, fall back to a PowerShell launcher with `-ExecutionPolicy Bypass`.
- A second `sagefs --supervised` while a daemon already holds the port: the child fails to bind, the watchdog backs off, then gives up with exit 0, and the task shows success. Check what the daemon logs and whether `sagefs status` tells the user why. A user who ran `sagefs mcp` first hits this.
- Depends on `hot-reload-on-windows-and-macos` for the Windows leg. Nothing depends on this item.
- Antivirus or Defender may flag a hidden console launcher. I cannot test that from here.
- What would change my mind: if hiding the window needs a native shim, the Startup shortcut plus documented manual restart becomes the honest answer, and the task is offered as an option.

## Size

S for the files, tests and Readme block (days). M with the hand verification on Windows and the CI case, because the Measure first questions need a real machine and the CI case waits on another item.

## Decisions for Will

- Task Scheduler, as recommended, or something simpler for the Advent audience (a Startup shortcut)? I recommend the task, with the shortcut documented as a one-line fallback.
- Keep copy-paste install blocks for a third OS, or build one `sagefs service install|status|stop|uninstall` subcommand for all three in F# with real tests? Your note on logic in projects and not scripts points at the subcommand, and it removes the quoting hazards that three shells give us. I recommend the Windows block first, matching the other two, and the subcommand as a separate item once all three exist.
- Is a given-up supervisor that exits 0 right on Windows too (the macOS file chose it deliberately)? I recommend yes, same behavior on all three.
