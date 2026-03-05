# SageFs Hot Reload Status

## ✅ What Works

### 6. Browser Auto-Refresh via DevReload (v0.5.579+)
- **`SageFs.DevReload`** — pure broadcaster in `SageFs.Core` with zero ASP.NET dependency
- **`SageFs.DevReloadMiddleware`** — ASP.NET Core middleware in `SageFs` project
- **`SageFs.DevReloadInjector`** — Harmony auto-injection into `WebApplication.Run/RunAsync`
- Injects a tiny `<script>` before `</body>` in all `text/html` responses
- Script opens SSE connection to `/__sagefs__/reload`
- When Harmony detours fire after a hot reload, `DevReload.broadcastReload()` signals all connected browsers
- Browser auto-refreshes — **no manual F5 needed**
- **Zero configuration** — works automatically for any ASP.NET Core app loaded in SageFs

#### DevReload Event Lifecycle
```
Compiling(fileName) → Reload        (success: hot reload applied, browser refreshes)
Compiling(fileName) → CompilationFailed(error)  (failure: error shown in browser overlay)
```
Three events ensure the browser **never** gets stuck showing "Recompiling...":
- `Compiling` — shows overlay indicator
- `Reload` — green flash, then page refresh
- `CompilationFailed` — red overlay with error text, no reload

#### Safety Features
- **Infinite-reload guard**: sessionStorage counter — if >3 reloads in 5s, pauses with red warning
- **Error overlay**: compilation errors shown directly in the browser (red panel, `#dc2626`)
- **Idempotent injection**: `data-sagefs-injected` attribute prevents double script injection
- **Kill switch**: Set `SAGEFS_DEVRELOAD=false` or `0` to disable entirely
- **SSE retry**: `retry: 1000` header ensures automatic reconnection after network hiccups

### 1. Automatic File Watching (NEW in 0.4.18)
- **Worker processes automatically watch project directories** for `.fs`, `.fsx`, `.fsproj` changes
- On `.fs`/`.fsx` change: debounced `#load` + Harmony method detouring — live-patches running code
- On `.fsproj` change: triggers soft reset to pick up new references
- Configurable via `--no-watch` flag to disable
- 500ms debounce prevents thrashing on rapid saves

### 2. Hot Reload with Harmony Method Detouring
- **PROVEN WORKING** with `test-hot-reload.fsx` example
- Handlers can be updated in real-time — no restart needed
- File-change-triggered `#load` now carries `hotReload=true` in Args
- This ensures the Harmony detouring middleware fires on both:
  - REPL-typed code (interactive)
  - File-change-triggered reloads (automatic)
- Changes appear instantly in browser

### 3. FSI Compatibility Middleware  
- Automatically rewrites `use` → `let` for indented use statements
- Applies to interactively-sent code via MCP
- Handles FSI incompatibilities transparently
- Located in `SageFs/FsiRewrite.fs` and `SageFs/Middleware/FsiCompatibility.fs`

### 4. Multi-line Code Submission
- Fixed in `SageFs/Mcp.fs` sendFsharpCode 
- Splits code by `;;` delimiter
- Executes each statement sequentially
- Returns all results concatenated

### 5. Enhanced Error Reporting
- Shows full exception details including:
  - Exception type
  - Message
  - Stack trace
  - Inner exceptions (recursively)
- Located in `SageFs/Mcp.fs` formatEvalResult

## 🔥 How Hot Reload Works End-to-End

1. **File change detected** → FileWatcher debounces (500ms)
2. **Action decided** → `fileChangeAction` routes `.fs` → Reload, `.fsproj` → SoftReset
3. **DevReload broadcasts** → `broadcastCompiling (Some "Handlers.fs")` → browser shows overlay
4. **Code sent to FSI** → `#load @"path/to/file.fs"` with `hotReload=true` in Args
5. **FSI evaluates** → generates new dynamic assembly with updated method bodies
6. **On success**: Harmony middleware fires → fuzzy-matches methods → detours applied → `broadcastReload()` → browser refreshes
7. **On failure**: `broadcastCompilationFailed "FS0001: ..."` → browser shows error overlay (red)
8. **No restart needed** → next HTTP request uses the new code automatically

## 🏗️ Architecture Decisions (Chesterton's Fences)

These design decisions exist for specific reasons. Before changing them, understand why they're there.

### AppDomain.CurrentDomain for shared state
**Why**: `DevReload.getChannels()` stores the ConcurrentDictionary in `AppDomain.CurrentDomain.GetData()` instead of a static field. This is because Harmony's auto-injection causes SageFs.Core.dll to be loaded multiple times in the same process (host copy + FSI shadow copy). A static field would create two separate dictionaries — the browser's SSE client registers against the shadow-copy DLL, while `broadcastReload()` runs in the host DLL. AppDomain storage is shared across all assembly loads, solving this mismatch.

### Channel-per-client (not shared Channel)
**Why**: Each SSE client gets its own `Channel<DevReloadEvent>`. A shared channel with multiple readers would require fan-out logic and risk one slow reader blocking others. Per-client channels provide natural backpressure isolation — if one browser tab is slow, others aren't affected. The ConcurrentDictionary keyed by connection ID supports this cleanly.

### Harmony auto-injection (not manual middleware registration)
**Why**: `DevReloadInjector.install()` patches `WebApplication.Run/RunAsync` via Harmony prefix. This means DevReload works **without any code changes** to the user's app — they just load their project in SageFs and it works. Manual `app.Use(middleware)` requires users to modify their code, which is worse DX and breaks the "zero-config" principle.

### Pre-allocated SSE byte arrays
**Why**: The SSE endpoint in `WorkerHttpTransport.fs` pre-allocates `heartbeatBytes`, `connectedBytes`, `compilingBytes`, and `reloadBytes` as module-level `ReadOnlyMemory<byte>` values. SSE heartbeats fire every 15s per client — allocating fresh byte arrays each time creates unnecessary GC pressure. Only `Compiling(Some file)` and `CompilationFailed(error)` allocate dynamically because their payloads vary.

### Single cleanup via RequestAborted.Register (not IDisposable)
**Why**: The SSE endpoint previously had both a `use cleanup = { new IDisposable ... }` and a `RequestAborted.Register(fun _ -> unregisterClient id)`. This caused double-unregister on normal disconnect (scope exit fires IDisposable, then abort fires the callback). Since `unregisterClient` decrements a counter, double-calling would make the counter go negative. Now only `RequestAborted.Register` handles cleanup. `unregisterClient` is idempotent via `TryRemove` — second call is a no-op.

### Infinite-reload guard (sessionStorage, not server-side)
**Why**: The guard runs in the browser, not on the server. Server-side rate limiting would need per-connection state management and doesn't protect against the actual failure mode (JavaScript `catch(ex) { reload() }` in a loop). The browser guard uses `sessionStorage` so it persists across reloads but not across tab close — a fresh tab always starts clean.

### Direct ConcurrentDictionary iteration (no snapshot)
**Why**: `broadcast()` iterates `for kvp in channels do ...` directly rather than `channels.Values |> Seq.toList`. ConcurrentDictionary's enumerator is lock-free and supports concurrent modification. The previous `Seq.toList` snapshot allocated a new list on every broadcast — unnecessary since we only call `TryWrite` (which never throws even if the channel is full or the client was just removed).

## ⚠️ Known Limitations

### Project Loading with `--proj`
When using `SageFs --proj MyProject.fsproj`:
- SageFs loads **compiled DLLs**, not source code
- The FSI compatibility rewrite only affects:
  - Files loaded with `--use` flag (`.fsx` scripts)
  - Code sent interactively via MCP
  - Files reloaded via file watcher `#load`
- Already-compiled DLL code is NOT rewritten

### Console I/O — Resolved
- PrettyPrompt has been **removed** from SageFs. The daemon-first architecture runs headless; `SageFs connect` provides the REPL client.

## 🎯 How to Use Hot Reload

### Automatic (File Watcher — Recommended)
```powershell
# Start SageFs with your project — file watching is ON by default
SageFs --proj HarmonyServer/HarmonyServer.fsproj

# Start your web server from the REPL, then just edit .fs files
# Changes are picked up automatically!
# Look for 🔥 or 📄 messages in the SageFs console
```

### Manual (REPL — For Experimentation)
```powershell
cd C:\Code\Repos\SageFs
SageFs --use test-hot-reload.fsx
```

Wait for "Starting web server..." message, then:
1. Open browser to http://localhost:5555
2. In FSI, send updated handler code
3. Refresh browser → see changes instantly!

### Disabling File Watching
```powershell
SageFs --proj MyProject.fsproj --no-watch
```

## 📁 Key Files

| File | Purpose |
|------|---------|
| `SageFs.Core/DevReload.fs` | Pure broadcaster: 3-case DU, Channel-per-client, AppDomain shared state |
| `SageFs/DevReloadMiddleware.fs` | ASP.NET middleware: SSE endpoint + HTML script injection + error overlay |
| `SageFs/DevReloadInjector.fs` | Harmony auto-injection: patches WebApplication.Run/RunAsync |
| `SageFs/WorkerHttpTransport.fs` | SSE endpoint: pre-allocated bytes, hardened exception handling |
| `SageFs/WorkerMain.fs` | Starts file watcher, routes changes to FSI, wires error path |
| `SageFs.Core/FileWatcher.fs` | Pure file watching with debounce |
| `SageFs.Core/Middleware/HotReloading.fs` | Harmony method detouring |
| `SageFs.Core/ActorCreation.fs` | Registers middleware pipeline |
| `SageFs.Tests/DevReloadTests.fs` | 31 tests: 6 FsCheck property + 25 unit (lifecycle, middleware, SSE) |
| `SageFs.Tests/HotReloadTests.fs` | 21 integration tests |
| `SageFs.Tests/FileWatcherTests.fs` | Pure function tests |

## ✨ Summary

**Hot reload is fully wired and working!** The system:
- Watches project directories for `.fs`/`.fsx`/`.fsproj` changes
- Debounces (500ms) to avoid thrashing
- Sends `#load` with `hotReload=true` to FSI
- Harmony library detours method pointers at runtime
- No restart, no manual intervention — just edit and save
