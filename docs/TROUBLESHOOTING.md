# Troubleshooting SageFs

Quick fixes for common issues. If your problem isn't listed here, check the
[GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues) or run the
health check in your editor. If it's genuinely broken, file it. I'd
rather hear about it than have you quietly work around it.

## Editor Health Checks (Start Here)

| Editor | Command |
|:-------|:--------|
| **VS Code** | `Ctrl+Shift+P` → "SageFs: Check Health" |
| **Neovim** | `:checkhealth sagefs` |
| **CLI / dashboard** | `sagefs status` and `http://localhost:37750/dashboard` |

---

## Common First-Run Issues

### "SageFs daemon not found" / CLI not installed

```bash
dotnet tool install --global SageFs
```

Verify: `sagefs --version` should print the version. If the command isn't
found, make sure `~/.dotnet/tools` is on your `PATH`.

**Requires**: .NET 10 SDK to install SageFs itself. Check with
`dotnet --version`. Sessions can be on .NET 10 or .NET 11. Each
session's host builds with whichever SDK `dotnet --version` reports in your
project's folder, and runs on that SDK's runtime. That's your `global.json`
pin if you have one, otherwise the newest SDK installed. So if an 11 preview
is installed and you want a project on 10, pin it.

### Daemon won't start / times out

1. **Check if another instance is running**: `sagefs status`. If it shows a
   running daemon, stop it with `sagefs stop` or use the existing one.
2. **Port in use**: Default is 37749. Use `--mcp-port 8080` to pick a different
   port. In VS Code, set `sagefs.mcpPort` in settings.
3. **Check the SageFs console window**: it logs startup errors to its
   own terminal window. Look for .NET SDK errors, missing project files, or
   compilation failures.
4. **First-time JIT warmup**: The very first launch after install takes longer
   (NuGet restore + JIT compilation). Give it 30–60 seconds.

### "No .fsproj or .sln found"

SageFs needs a project file. Either:
- Open a folder containing a `.fsproj` or `.sln` / `.slnx` file
- Set the project path explicitly:
  - **VS Code**: `sagefs.projectPath` in settings, or use "SageFs: Switch Project"
  - **Neovim**: `:SageFsSwitchProject` or set `vim.g.sagefs_project_path`
  - **CLI / dashboard**: start `sagefs`, then create or switch to a session for `path/to/MyApp.fsproj`

### Wrong project selected (multi-project workspace)

When a workspace has multiple `.fsproj` files, SageFs picks one. If it chose
the wrong one:
- **VS Code**: Run "SageFs: Switch Project" from the command palette
- **Neovim**: `:SageFsSwitchProject`
- **CLI / dashboard**: create or switch to a session for `path/to/CorrectProject.fsproj`

The active project is shown in the status bar.

---

## Runtime Issues

### Evaluation hangs / no result appears

- **Check the daemon is alive**: Look for the SageFs console window. If it
  crashed, restart via your editor's "Start Daemon" command.
- **SSE connection dropped**: The status bar shows connection state. If
  disconnected, most editors auto-reconnect. You can also trigger reconnect
  manually (VS Code: Command Palette → `SageFs: Reconnect to Daemon`, Neovim:
  `:SageFsReconnect`).
- **Long-running eval**: Some evaluations genuinely take time (large
  compilations, network calls). Check the daemon console for progress.

### Stale REPL after code changes

Use hard reset to pick up source file changes:
- **VS Code**: "SageFs: Hard Reset" from command palette
- **Neovim**: `:SageFsHardReset`
- **MCP**: `hard_reset_fsi_session` tool with `rebuild=true`

**If a hard reset doesn't fix it, read the next section.** A hard reset reloads
the session. It cannot help when the stale thing is the daemon itself.

### Stale daemon — the one that wastes the most time

A daemon that has been running since before your code changed keeps serving the
assemblies it started with. Nothing warns you, and it does not look like a
version problem. It looks like SageFs is broken.

Three ways it shows up:

- `type not found, Version=...`, or a `SageFs.Core` version mismatch.
- `Could not load file or assembly 'System.Runtime, Version=N.0.0.0'` in worker
  stderr, on a project that builds perfectly on its own. This one means the
  daemon's worker is running an older .NET than your project targets: it starts
  fine, then fails the moment it loads your DLLs.
- No error at all. Evals just quietly disagree with the code in front of you.

**Check the daemon before you believe any other diagnosis:**

```bash
sagefs status
```

It prints both `Version` and `Started`. `Started` is usually the faster tell:
if the daemon has been up since before your last build, it is serving code
older than what you're looking at, whatever the version says.

If it's behind the code you're working on, restarting the *session* won't help —
restart the *daemon*:

```bash
sagefs stop
dotnet tool update --global SageFs
sagefs
```

If `dotnet tool update` reports "already installed" when you know a newer
version is published, pass the version explicitly:

```bash
dotnet tool update --global SageFs --version X.Y.Z
```

`dotnet tool update` resolves through NuGet's search/registration index, which
lags the package store by a few minutes after a release. The package can be on
NuGet while the CLI still can't see it.

### Hot reload not working

- Hot reload is auto-injected by default for `.fs` file changes
- Check `SAGEFS_DEVRELOAD` environment variable isn't set to `0`
- Look for `[DevReload]` messages in daemon logs
- Ensure the file is part of the active project (listed in `.fsproj`)

### Live testing not running

- Verify live testing is enabled: check your editor's test status indicator
- Check run policies: some test categories (integration, browser) default to
  `demand` (manual trigger only)
- Check live-test status in your editor: `:SageFsLiveTestStatus` (Neovim), the Tests pane (VS Code), or the web dashboard

### SSE connections dropping

- Set proxy/reverse-proxy timeout ≥ 60 seconds
- SageFs sends a keepalive comment on the dashboard's SSE stream every 5
  seconds by default (`SAGEFS_DASHBOARD_HEARTBEAT_SECONDS`), well inside
  Kestrel's own keep-alive window
- Corporate proxies may need explicit WebSocket/SSE passthrough configuration

### Eval watchdog — detecting daemon crash during eval

Supported editor integrations include an **eval watchdog**. If the daemon becomes unresponsive during an evaluation:

1. A timer starts when you send an eval command
2. If no response arrives within the timeout, the editor shows a notification:
   *"Evaluation interrupted — daemon may have crashed"*
3. The notification offers **Restart Daemon** and **Show Output** actions

The watchdog uses a monotonic generation ID to prevent race conditions. If you
start a new eval before the watchdog fires, the old timer is silently cancelled.

**If you see phantom "interrupted" dialogs**, update to v0.6.50+, which
included the monotonic ID fix. If you're on anything newer than that and
still seeing it, that's a regression. File it.

---

## Environment Variable Overrides

All timeout values can be overridden via environment variables. Set them before
starting SageFs (or in your shell profile).

| Variable | Default | Description |
|:---------|:--------|:------------|
| `SAGEFS_WARMUP_INACTIVITY_SECONDS` | `30` | Max seconds of inactivity during warmup before declaring failure |
| `SAGEFS_WARMUP_MAX_MINUTES` | `10` | Absolute max warmup duration |
| `SAGEFS_PER_TEST_TIMEOUT_SECONDS` | `5` | Per-test timeout |
| `SAGEFS_BUILD_TIMEOUT_MINUTES` | `10` | Max time for `dotnet build` during hard reset |
| `SAGEFS_WORKER_HTTP_READ_SECONDS` | `30` | HTTP read timeout for daemon→worker communication |
| `SAGEFS_WORKER_STARTUP_TIMEOUT_MS` | `120000` | Worker process startup timeout (milliseconds) |
| `SAGEFS_DASHBOARD_HEARTBEAT_SECONDS` | `5` | Dashboard SSE keepalive/heartbeat cadence |
| `SAGEFS_BIND_HOST` | `localhost` | Loopback bind address: `localhost`, `127.0.0.1` or `::1`. Any other value stops the daemon at startup (see [Docker / Remote Containers](#docker--remote-containers)) |
| `SAGEFS_MCP_PORT` | `37749` | MCP server port |

**Example**: slow CI machine with large project:

```bash
export SAGEFS_WARMUP_MAX_MINUTES=20
export SAGEFS_BUILD_TIMEOUT_MINUTES=15
export SAGEFS_PER_TEST_TIMEOUT_SECONDS=15
sagefs
```

Then create a session for `MyBigProject.Tests/MyBigProject.Tests.fsproj`.

The `ValidTimeout` type enforces a 1s–10min range. Values outside this range
are silently ignored and the default is used.

---

## Warmup Progress Phases

During session warmup, SageFs emits `warmup_progress` SSE events. Your editor's
status bar shows which phase is active:

| Phase | Status Bar Text | What's happening |
|:------|:---------------|:-----------------|
| `creating_fsi` | Creating FSI... | Spawning the F# Interactive process |
| `scanning_sources` | Scanning sources... | Reading project source files |
| `loading_assemblies` | Loading assemblies... | Loading referenced NuGet/project assemblies |
| `opening_namespaces` | Opening namespaces (N/M)... | Auto-opening project namespaces in FSI |
| `finalizing` | Finalizing... | Final validation, session ready |

Each event includes `{Step, Total, Progress, Phase, Message}`. The `Progress`
field is a 0.0–1.0 float for progress bars.

**If warmup stalls**: Check the daemon console window for compilation errors or
missing packages. Increase `SAGEFS_WARMUP_INACTIVITY_SECONDS` if your project
has slow NuGet restores.

**Large repos (dozens of projects, a big solution): name a project.**
`create_session` with an empty `projects` list makes the worker auto-discover
and load whatever it finds in the working directory — on a repo with many
`.fsproj` files that can take minutes, since it's trying to resolve all of
them, not the one you actually want. Passing one explicit project
(`projects=["path/to/YourProject.fsproj"]`) is typically Ready in seconds on
the exact same repo. `get_available_projects` lists candidates if you're not
sure which one to name.

**A session that never reaches Ready is bounded, not silent.** Warmup is
governed by two limits: `SAGEFS_WARMUP_INACTIVITY_SECONDS` (default 30) is how
long the worker can go with no progress before it's declared stuck;
`SAGEFS_WARMUP_MAX_MINUTES` (default 10) is the hard ceiling regardless of
progress. A session that's genuinely still working (a big repo resolving many
projects) keeps that inactivity clock reset by its own progress and can run up
to the absolute ceiling; a session that's gone quiet is faulted within the
inactivity window, with a stated reason — never left reading "Starting" with
nothing to show for it. `get_fsi_status` on a warming session reports
`elapsedSeconds`, `boundSeconds`, and the last progress line so you can see
which regime you're in. `stop_session` returns promptly in every case,
including on an already-faulted or still-warming session — it does not wait
for warmup to finish or fail first.

---

## Platform-Specific Issues

### macOS: VS Code "cannot read properties of undefined"

Fixed in v0.5.414+. Update the extension. The extension now degrades gracefully
if the daemon can't start.
See [#18](https://github.com/WillEhrendreich/SageFs/issues/18).

### Docker / Remote Containers

SageFs only listens on loopback. Its HTTP ports evaluate F# as your user and
have no authentication, so binding all interfaces (`SAGEFS_BIND_HOST=0.0.0.0`)
would hand code execution to anyone on the network. The daemon refuses to
start with a non-loopback `SAGEFS_BIND_HOST`, and `sagefs check` reports it.
I'm not going to make this configurable just so someone can trade an
afternoon of convenience for handing out remote code execution. Forward the
ports instead.

To reach a daemon in a container, forward ports 37749 and 37750 to the
container's loopback instead:

- **VS Code Dev Containers**: add `"forwardPorts": [37749, 37750]` to
  `devcontainer.json`. VS Code tunnels to the container's loopback.
- **Docker on Linux**: `docker run --network host ...`
- **Anything with SSH**: `ssh -L 37749:localhost:37749 -L 37750:localhost:37750 <host>`

Browser requests must come from the dashboard itself. A page served from any
other origin, including another `localhost` port, is refused. Request bodies
must be sent as `Content-Type: application/json`.

---

## Diagnostic Tools

| Tool | What it shows |
|:-----|:-------------|
| `sagefs status` | Running daemon info, port, sessions |
| `sagefs stop` | Gracefully stop the daemon |
| Daemon console window | Real-time logs, compilation output, test results |
| OpenTelemetry export | Structured traces and metrics (set `OTEL_EXPORTER_OTLP_ENDPOINT`) |
| Editor output channel | Extension-side logs (VS Code: "SageFs" in Output panel) |

---

## FSI Quirks & Rewrites

- **`;;` is required**: every FSI transaction must end with `;;`
- **"Operation could not be completed due to earlier error"**: a *previous*
  submission had a compile error. Fix that code and resubmit it. The session
  is fine, do NOT reset.
- **Type changes need hard reset**: if you change a type definition (DU, record),
  the old version is cached in FSI. Use hard reset to pick up the new types.
- **Order matters**: FSI evaluates in submission order. Define types before
  functions that use them.

---

## Still Stuck?

1. Check [GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues) for
   known problems
2. Run the health check for your editor (see table at top)
3. File a new issue with: editor name + version, SageFs version (`sagefs --version`),
   OS, and the error message or behavior you're seeing. The more specific,
   the faster I can actually do something about it.
