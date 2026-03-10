# Troubleshooting SageFs

Quick fixes for common issues. If your problem isn't listed here, check the
[GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues) or run the
health check in your editor.

## Editor Health Checks (Start Here)

| Editor | Command |
|:-------|:--------|
| **VS Code** | `Ctrl+Shift+P` → "SageFs: Check Health" |
| **Neovim** | `:checkhealth sagefs` |
| **Visual Studio** | Check the SageFs output channel (View → Output → SageFs) |
| **TUI / CLI** | `sagefs status` |

---

## Common First-Run Issues

### "SageFs daemon not found" / CLI not installed

```bash
dotnet tool install --global SageFs
```

Verify: `sagefs --version` should print the version. If the command isn't found,
ensure `~/.dotnet/tools` is on your `PATH`.

**Requires**: .NET 10 SDK. Check with `dotnet --version`.

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
  - **CLI**: `sagefs --proj path/to/MyApp.fsproj`

### Wrong project selected (multi-project workspace)

When a workspace has multiple `.fsproj` files, SageFs picks one. If it chose the
wrong one:
- **VS Code**: Run "SageFs: Switch Project" from the command palette
- **Neovim**: `:SageFsSwitchProject`
- **CLI**: Restart with `sagefs --proj path/to/CorrectProject.fsproj`

The active project is shown in the status bar.

---

## Runtime Issues

### Evaluation hangs / no result appears

- **Check the daemon is alive**: Look for the SageFs console window. If it
  crashed, restart via your editor's "Start Daemon" command.
- **SSE connection dropped**: The status bar shows connection state. If
  disconnected, most editors auto-reconnect. You can also trigger reconnect
  manually (VS Code: restart extension, Neovim: `:SageFsReconnect`).
- **Long-running eval**: Some evaluations genuinely take time (large
  compilations, network calls). Check the daemon console for progress.

### Stale REPL after code changes

Use hard reset to pick up source file changes:
- **VS Code**: "SageFs: Hard Reset" from command palette
- **Neovim**: `:SageFsHardReset`
- **MCP**: `hard_reset_fsi_session` tool with `rebuild=true`
- **TUI REPL**: `#hard-reset` command

### Hot reload not working

- Hot reload is auto-injected by default for `.fs` file changes
- Check `SAGEFS_DEVRELOAD` environment variable isn't set to `0`
- Look for `[DevReload]` messages in daemon logs
- Ensure the file is part of the active project (listed in `.fsproj`)

### Live testing not running

- Verify live testing is enabled: check your editor's test status indicator
- Check run policies: some test categories (integration, browser) default to
  `demand` (manual trigger only)
- Run `:SageFsLiveTestStatus` (Neovim) or check the Tests pane (VS Code/TUI)

### SSE connections dropping

- Set proxy/reverse-proxy timeout ≥ 60 seconds
- SageFs sends keepalive pings every 15 seconds
- Corporate proxies may need explicit WebSocket/SSE passthrough configuration

---

## Platform-Specific Issues

### macOS: "SyntaxHighlight init failed"

Tree-sitter native library not yet bundled for macOS/Linux. Syntax highlighting
falls back gracefully — all other features work normally.
See [#17](https://github.com/WillEhrendreich/SageFs/issues/17).

### macOS: VS Code "cannot read properties of undefined"

Fixed in v0.5.414+. Update the extension. The extension now degrades gracefully
if the daemon can't start.
See [#18](https://github.com/WillEhrendreich/SageFs/issues/18).

### Docker / Remote Containers

Set `SAGEFS_BIND_HOST=0.0.0.0` so the daemon listens on all interfaces, not
just localhost.

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
  submission had a compile error. Fix that code and resubmit it. The session is
  fine — do NOT reset.
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
   OS, and the error message or behavior you're seeing
