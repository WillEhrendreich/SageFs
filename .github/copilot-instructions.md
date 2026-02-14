# SageFs Development Workflow

## SageFs MCP Integration (MANDATORY)

SageFs runs an MCP server on port 37749. You have `SageFs-*` MCP tools available: `sagefs-send_fsharp_code`, `sagefs-hard_reset_fsi_session`, `sagefs-get_recent_fsi_events`, `sagefs-get_fsi_status`, `sagefs-get_startup_info`, `sagefs-get_available_projects`, `sagefs-load_fsharp_script`, `sagefs-reset_fsi_session`.

### CRITICAL RULES

1. **ALWAYS use `SageFs-*` MCP tools** for all F# work — never raw PowerShell, never `dotnet build`, never `dotnet run`, never `Invoke-RestMethod`, never `curl`. If SageFs is available, use it.
2. **PowerShell is ONLY for process management** — starting/stopping SageFs, `dotnet pack`, `dotnet tool install/uninstall`. Everything else goes through SageFs MCP.
3. **NEVER start SageFs invisibly** — no `detach: true`, no `mode: "async"`. SageFs needs a real TTY (PrettyPrompt crashes without one)
4. **ALWAYS start SageFs via `Start-Process`** so it gets its own visible console window
5. **Don't poll or sleep** — the MCP connection is SSE (push-based). When SageFs is ready, the `SageFs-*` tools become available. Just try using them; if the connection isn't up yet, the MCP framework will report it.
6. **NEVER ask the user to do something you can do yourself** — own the full pack/reinstall/restart cycle

### Starting SageFs

```powershell
Start-Process -FilePath "SageFs" -ArgumentList "--proj","SageFs.Tests\SageFs.Tests.fsproj" -WorkingDirectory "C:\Code\Repos\SageFs" -PassThru
```

After starting, just use `SageFs-*` MCP tools — the SSE connection is push-based, so tools become available when SageFs is ready.

### Iterating on SageFs Source Code (pack/reinstall cycle)

When making changes to SageFs's own source files (anything in `SageFs\` or `SageFs.Cli\`), you MUST do the full reinstall cycle. **Testing against stale binaries wastes everyone's time.**

1. Make code changes to `.fs` files
2. Stop SageFs, uninstall, build tests, pack tool, install, start:
   ```powershell
   $p = Get-Process -Name SageFs* -ErrorAction SilentlyContinue; if ($p) { Stop-Process -Id $p.Id -Force }
   dotnet tool uninstall --global SageFs.cli
   cd C:\Code\Repos\SageFs
   dotnet build && dotnet pack SageFs.Cli -o nupkg && dotnet tool install --global SageFs.cli --add-source C:\Code\Repos\SageFs\nupkg --no-cache && Start-Process -FilePath "SageFs" -ArgumentList "--proj","SageFs.Tests\SageFs.Tests.fsproj" -WorkingDirectory "C:\Code\Repos\SageFs" -PassThru
   ```
   **CRITICAL**: Use `&&` to chain build/pack/install/start — if any step fails, the chain stops. Do NOT use `;` which continues blindly after failures.
   **Note**: `dotnet build` compiles Debug (for test DLLs). `dotnet pack` compiles Release (for the tool nupkg). Both are needed.
3. Use `SageFs-*` MCP tools directly — SSE connection pushes when ready, no sleeping
4. Test via `SageFs-*` MCP tools

### After Test Code Changes Only

If only test project code changed (not SageFs itself), use `sagefs-hard_reset_fsi_session` with `rebuild=true` to rebuild and reload assemblies without the full reinstall cycle.

### Running Tests

- **ALWAYS run tests inside SageFs** using the warm FSI session:
  ```fsharp
  Expecto.Tests.runTestsWithCLIArgs [] [||] SageFs.Tests.MyModule.myTests;;
  ```
- Test output (pass/fail details) goes to the SageFs console window. The MCP return value is the exit code (0=pass, non-zero=fail).
- **Do NOT use `dotnet run --project SageFs.Tests`** — use SageFs's FSI session instead. The only exception is CI pipelines.
- Default run excludes `[Integration]` tests; use `--all` or `--integration` for the full suite

### MANDATORY: How to Handle FSI Errors (DO NOT RESET)

When you see **"Operation could not be completed due to earlier error"**, this means YOUR CODE has a bug. It does NOT mean the session is corrupted. **Do NOT reset.**

**The correct response is ALWAYS:**
1. Read the diagnostic message — it tells you exactly what's wrong (e.g., "type X is not defined", "not a function")
2. Fix your code — the error is in what YOU submitted, not in the session
3. Re-submit the corrected code — previous good definitions are still alive in the session

**Decision tree before ANY reset:**
- "Is this an error in code I just submitted?" → YES (99.9% of the time) → Fix your code, do NOT reset
- "Did the warmup itself fail with cascading errors on startup?" → Check `sagefs-get_recent_fsi_events` for warmup errors → Only THEN consider `sagefs-reset_fsi_session` (soft reset)
- "Did a soft reset not fix a genuine warmup failure?" → Only THEN consider `sagefs-hard_reset_fsi_session`

**NEVER do a hard reset because:**
- You got "Operation could not be completed due to earlier error" — that's YOUR code's fault
- You want to "start fresh" or "be safe" — you're throwing away useful state for no reason
- You're not sure what went wrong — read the diagnostics first, they tell you

**`sagefs-reset_fsi_session` (soft reset):** Clears all definitions but keeps the session alive. Use ONLY when the session is genuinely stuck from a warmup failure, not because your code had a typo.

**`sagefs-hard_reset_fsi_session`:** Destroys everything, rebuilds DLLs, restarts FSI. Use ONLY when:
- You changed .fs files and need the rebuilt DLL loaded (with `rebuild=true`)
- A soft reset failed to fix a genuine session corruption
- DLL locks are preventing progress

### Common Mistakes to Avoid

- **RESETTING when your code has a bug** — the #1 mistake. Read the error, fix your code, resubmit. The session is fine.
- Running `dotnet build` in PowerShell when you should be using `sagefs-hard_reset_fsi_session rebuild=true`
- Running `dotnet run --project SageFs.Tests` instead of running Expecto tests inside SageFs via `sagefs-send_fsharp_code`
- Trying to capture Expecto test output via stdout redirection hacks — just read the exit code from MCP and check the visible SageFs window for details
- Forgetting the pack/reinstall cycle after changing SageFs source — you'll be testing stale code and wondering why your fix "didn't work"
- Skipping `dotnet build` before `dotnet pack` — test DLLs won't match the new SageFs types, causing runtime errors
- Chaining commands with `;` instead of `&&` — failures are silently ignored and you end up with a broken install
- Using `Start-Sleep` or polling loops anywhere — the MCP SSE connection is push-based. Just call `SageFs-*` tools directly; if SageFs isn't ready, the MCP framework reports it. Never sleep-then-retry.
- Using `Thread.Sleep` in tests — events are emitted synchronously; if you need to wait for something, poll with short intervals and a timeout, never arbitrary fixed sleeps
- Running commands in PowerShell and waiting with `read_powershell` when SageFs can do the same thing — use SageFs for all F# work including running tests
