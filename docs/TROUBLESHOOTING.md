# Troubleshooting SageFs

Quick fixes for common issues. If your problem isn't listed here, check the
[GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues) or run the
health check in your editor — and if it's genuinely broken, file it. I'd
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
`dotnet --version`. Individual sessions can target either .NET 10 or .NET
11 — each session's host builds with that project's own SDK and runs on
its own runtime, independent of what SageFs itself is installed with.

### Daemon won't start / times out

1. **Check if another instance is running**: `sagefs status` — if it shows a
   running daemon, stop it with `sagefs stop` or use the existing one.
2. **Port in use**: Default is 37749. Use `--mcp-port 8080` to pick a different
   port. In VS Code, set `sagefs.mcpPort` in settings.
3. **Check the SageFs console window** — the daemon logs startup errors to its
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

### Deprecated frontend commands

The built-in SageTUI client, legacy TUI, and `SageFs.Gui` Raylib frontend
are deprecated. Use a supported editor integration, the web dashboard, or
MCP instead. Raylib application and game projects remain fully supported;
the deprecation is only about the old SageFs product frontend, not about
you shipping a Raylib game.

### Stale REPL after code changes

Use hard reset to pick up source file changes:
- **VS Code**: "SageFs: Hard Reset" from command palette
- **Neovim**: `:SageFsHardReset`
- **MCP**: `hard_reset_fsi_session` tool with `rebuild=true`

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

The watchdog uses a monotonic generation ID to prevent race conditions — if you
start a new eval before the watchdog fires, the old timer is silently cancelled.

**If you see phantom "interrupted" dialogs**, update to v0.6.50+, which
included the monotonic ID fix. If you're on anything newer than that and
still seeing it, that's a regression — file it.

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

**Example** — slow CI machine with large project:

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
afternoon of convenience for handing out remote code execution — forward the
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

- **`;;` is required** — every FSI transaction must end with `;;`
- **"Operation could not be completed due to earlier error"** — a *previous*
  submission had a compile error. Fix that code and resubmit it. The session
  is fine — do NOT reset.
- **Type changes need hard reset** — if you change a type definition (DU, record),
  the old version is cached in FSI. Use hard reset to pick up the new types.
- **Order matters** — FSI evaluates in submission order. Define types before
  functions that use them.

---

## Still Stuck?

1. Check [GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues) for
   known problems
2. Run the health check for your editor (see table at top)
3. File a new issue with: editor name + version, SageFs version (`sagefs --version`),
   OS, and the error message or behavior you're seeing. The more specific,
   the faster I can actually do something about it.
