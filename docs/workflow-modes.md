# Understanding SageFs Workflow Modes

SageFs has **three workflow modes**, and live testing is *also* an independent feature that works in all of them. That double life is the thing people trip over, so this guide sorts it out.

The set is closed and lives in one place, [`SageFs.Core/WorkflowTypes.fs`](../SageFs.Core/WorkflowTypes.fs):

```fsharp
type SessionWorkflow =
  | Interactive                              // "REPL"
  | LiveTesting                              // "Live Testing"
  | HotReload of BrowserRefreshConfig        // "Hot Reload"
```

> **This page used to say there were two.** It claimed live testing "isn't a third mode" and that SageFs "has two workflow modes." `LiveTesting` has been a real workflow case since commit `90d8721a`; `switch_workflow target='livetesting'` has always selected it. The statement was wrong, and the correction is below.

## The 30-Second Version

```
+----------------------------------------------------------------------+
|                        YOUR SAGEFS SESSION                           |
|                                                                      |
|  Pick ONE workflow:                                                  |
|  +---------------+   +-----------------+   +---------------------+   |
|  | REPL          |   | Live Testing    |   | Hot Reload          |   |
|  | (Interactive, |   | (LiveTesting)   |   | (HotReload; also    |   |
|  |  the default) |   |                 |   |  spelled "live")    |   |
|  |               |   |                 |   |                     |   |
|  | [Y] Redefine  |   | [Y] Redefine    |   | [N] No redefine     |   |
|  |     types     |   |     types       |   |     (FS0037)        |   |
|  | [N] No browser|   | [N] No browser  |   | [Y] Browser hot     |   |
|  |     hot reload|   |     hot reload  |   |     reload          |   |
|  | [ ] Live test |   | [x] Live test   |   | [ ] Live testing    |   |
|  |     off until |   |     ARMED for   |   |     off until you   |   |
|  |     you ask   |   |     as you type |   |     ask             |   |
|  +---------------+   +-----------------+   +---------------------+   |
|                                                                      |
|  Features you toggle independently, in ANY workflow:                 |
|  [x] Live Testing  <-- POST /api/live-testing/enable                 |
|  [x] Coverage                                                        |
|  [x] Diagnostics                                                     |
+----------------------------------------------------------------------+
```

**Live testing is both a workflow and a feature, and that is not a contradiction.** The *feature* is a per-session on/off switch that works in every workflow. The *workflow* is the shortcut: choosing `LiveTesting` makes the daemon arm that switch for you the moment the session reaches Ready ([`SageFs/DaemonMode.fs`](../SageFs/DaemonMode.fs), the `onSessionReadyExtra` hook), and the loop is driven by debounced keystrokes rather than by saves. If you never pick the workflow, you can still turn the feature on by hand and get everything except the automatic arming.

---

## The Three Workflows, Explained

### REPL Mode (Interactive) — The Default

**What it feels like:** A supercharged F# Interactive session. You type code, it runs instantly. You change a type definition, re-evaluate, and it just works. You're sketching, exploring, prototyping.

**What you can do:**
- Redefine types (`type Order = ...`) as many times as you want
- Redefine modules, DUs, records, classes, everything
- Full interactive exploration with instant feedback
- The same MCP tool surface as Live mode (calls are gated by session state, not by workflow; see [MCP Tools](mcp-tools.md))

**What you give up:**
- No automatic browser refresh. If you're running a web app, you'll need to refresh the browser manually after editing `.fs` files.

**Who should use it:**
- You're designing domain types and changing their shape frequently
- You're exploring an API or library interactively
- You're writing and iterating on tests
- You're working in `.fsx` scripts
- You're not building a web app (or don't mind manual browser refresh)

### Live Testing Mode — For Red-Green Loops

**What it feels like:** the same full REPL as above, except the test loop is already running when you arrive. You type; affected tests re-run against the buffer you are typing into.

**What you can do:**
- Everything REPL mode can do: redefine types freely, full interactive exploration. The workflow adds no FSI flags ([`SessionWorkflow.fsiArgs`](../SageFs.Core/WorkflowTypes.fs) returns `[]` for it, exactly as for `Interactive`), because running tests never patches a running app, so the single-assembly constraint below does not apply.
- Live testing comes on automatically when the session is ready. You do not call Enable.
- Reruns are keystroke-driven, not save-driven: editors post the live buffer to `POST /api/sessions/{sid}/buffer-changed`.

**What you give up:**
- Nothing relative to REPL mode. It is REPL mode with the test loop armed.
- Still no browser hot reload. That's the third workflow.

**Who should use it:**
- You're doing TDD and want the red-green loop without arming it every session
- You want test feedback on code that does not compile yet

**Select it with:** `switch_workflow target='livetesting'` (aliases: `live-testing`, `testing`, `test`). Note the trap: plain **`live` means Hot Reload**, not live testing. See the alias table at the bottom.

### Hot Reload Mode (also spelled "Live" / "WebLive") — For Web Developers

**What it feels like:** Save a file and the browser refreshes on its own. No rebuild, no restart, no manual F5.

**What you can do:**
- Edit **function bodies**; saving re-emits the changed functions, Harmony re-points those methods in the running process, and an SSE refresh reaches the browser. This works even when the route table was captured once at startup, because the captured route still dispatches to the handler's method entry point.
- SageFs auto-injects dev-reload middleware into your ASP.NET pipeline, no config
- The same MCP tool surface as REPL mode (calls are gated by session state, not by workflow; see [MCP Tools](mcp-tools.md))

> **Not everything is patchable.** A change to a function *body* reaches the running
> process; anything that takes effect at startup (a value binding the route captured, a
> `let mutable` read compiled to a field load, a changed signature or type) needs a
> restart, and SageFs restarts the app rather than pretending. [Hot Reload](hot-reload.md)
> is the authority. It carries the full shape matrix, each row pinned by an executable
> test. This page deliberately does not restate it.

**What you give up:**
- You **cannot redefine types**. Trying to redefine a `type` in the REPL produces `FS0037: Duplicate definition of type`. This is a CLR constraint, not a SageFs bug (more on this below).
- You can still change function bodies, add new let bindings, and call functions. You just can't reshape a type definition once it exists in the session.

**Who should use it:**
- You're building a web app with Falco.Datastar, Giraffe, Saturn, or ASP.NET
- You want save-and-see-it browser feedback
- Your types are stable and you're iterating on behavior

---

## Decision Tree

```
Do you need the browser to refresh on save?
├── YES → Use HOT RELOAD (target='live')
│         ...and accept that you cannot redefine types (FS0037)
└── NO  → Do you want the test loop running as you type?
          ├── YES → Use LIVE TESTING (target='livetesting')
          └── NO  → Use REPL (the default)

Are you frequently changing type definitions?
├── YES → REPL or Live Testing — both keep full type redefinition.
│         Hot Reload blocks it.
└── NO  → Any of the three works — pick on browser vs. test-loop needs

Not sure?
└── Start with REPL. Switching later costs one session restart.
```

---

## Why Can't I Have Hot Reload *and* Type Redefinition?

Because of a hard constraint in the .NET runtime. (This section is only about Hot Reload. REPL and Live Testing both keep full type redefinition: neither passes `--multiemit-`.)

**The chain of constraints:**

1. **Browser hot reload** needs to patch running methods without restarting the app
2. **Method patching** uses [Harmony](https://github.com/pardeike/Harmony), which hooks into the JIT compiler
3. **Harmony's JIT hook** only works when FSI emits all code into a **single assembly** (the `--multiemit-` flag)
4. **Single-assembly mode** means the CLR can't distinguish between "old version of Type X" and "new version of Type X", so they collide → `FS0037`

```
Hot reload needs Harmony → Harmony needs --multiemit- → --multiemit- blocks type redefinition
```

The CLR forces this; SageFs didn't choose it. If FSI changes how it emits assemblies, or Harmony learns to work with multi-emit, the limitation goes away. Until then you pick one per session. Switching between them is cheap (see below).

---

## Switching Between Modes

| Client | Command |
|:---|:---|
| **Neovim** | `:SageFsWorkflow live` or `:SageFsWorkflow repl` |
| **VS Code** | Command Palette → `SageFs: Switch Workflow` (picker offers all three workflows and marks which one you're in) |
| **Web dashboard** | **No control yet.** The daemon now has a route (`POST /api/sessions/{sid}/workflow`, the same one VS Code uses), but the dashboard UI still only renders the workflow as a read-only badge and has no button that calls it. Use an editor or MCP. |
| **MCP tool** | `switch_workflow(target='repl' \| 'livetesting' \| 'live')` |

### Target spellings

One alias table drives every surface (`SessionWorkflow.tryOfString` in [`WorkflowTypes.fs`](../SageFs.Core/WorkflowTypes.fs)), so the CLI, the HTTP API and MCP accept exactly the same spellings. `switch_workflow` calls it directly and **rejects** an unrecognised target; the convenience wrapper `ofString`, used where there is no one to report an error to (env vars, config), falls back to `Interactive` instead.

| Workflow | Accepted targets |
|:---|:---|
| REPL | `interactive`, `repl`, `normal` |
| Live Testing | `livetesting`, `live-testing`, `testing`, `test` |
| Hot Reload | `hotreload`, `weblive`, `live`, `web` |

⚠️ **`live` means Hot Reload, not Live Testing.** The workflow was once labelled "Live", and the alias is kept for backward compatibility. If you want the test loop, ask for `livetesting`.

### What happens when you switch

The mechanics now differ by client:

- **VS Code and the dashboard's own route** (`POST /api/sessions/{sid}/workflow`) restart the **same session id** into the target workflow, spawn-first. There is no fork and never a moment where two sessions exist for one working directory.
- **The MCP tool** `switch_workflow` still creates a **new session** in the target mode and stops the old one, so the session id changes.

Either way, any definitions you made in the REPL are **lost** (they lived in the stopped process's memory), and your `.fs` files on disk are **untouched**. They reload into the replacement session automatically.

**Tip:** If you have important REPL work, persist it to a `.fsx` file before switching. Use the `export_session_transcript` MCP tool to save your session as a runnable script.

### Dry-run preview

Not sure what you'd lose? Preview first:

```
switch_workflow(target='live', dryRun=true)
→ "Preview: switching from REPL to Live would lose 12 definitions and 5 cells"
```

### Auto-detection

When SageFs detects web-oriented packages in your project, it suggests the Hot Reload workflow:

| Package | Suggestion |
|:---|:---|
| Falco.Datastar, Starfederation.Datastar | "Datastar project detected — Hot Reload enables SSE-driven DOM morphing" |
| Falco, Falco.Htmx, Giraffe, Saturn, Microsoft.AspNetCore | "Web project detected — Hot Reload enables browser hot reload" |

The suggestion arrives as text in a tool response. SageFs never auto-switches. You always choose.

---

## Live Testing — a Workflow *and* a Feature

This is the most common confusion, and the reason is that both things are real.

**The feature** is a per-session on/off switch. It works in every workflow, including Hot Reload. Turning it on does not change how FSI works, does not affect type redefinition, and does not touch hot reload.

**The workflow** (`SessionWorkflow.LiveTesting`) is the shortcut. Choosing it makes the daemon flip that switch for you as soon as the session is ready, and it declares your intent to drive the loop from keystrokes rather than saves.

So: *"is live testing on?"* and *"which workflow am I in?"* are two different questions with two different answers, and you can have live testing on in any of the three.

### What live testing does

When enabled, SageFs watches which functions your tests call (via a dependency graph). When you change a function, it automatically re-runs only the tests that cover that function. Results appear inline in your editor: green gutter marks for passing, red for failing, with failure details shown right next to the code.

### How to turn the feature on

- **VS Code**: Command Palette → `SageFs: Enable Live Testing`
- **Neovim**: `:SageFsEnableTesting` (and `:SageFsDisableTesting`)
- **Web dashboard**: the live-testing control on the session card

All three drive the daemon HTTP API (`POST /api/live-testing/enable`). There is no `enable_live_testing` MCP tool. The MCP testing tools are `list_tests`, `targeted_verify`, and `explain_test_failure`. See [`LIVE_TESTING_GUIDE.md`](LIVE_TESTING_GUIDE.md) for the full list of what is and is not an MCP tool.

### How to pick the workflow instead

`switch_workflow(target='livetesting')`, `:SageFsWorkflow` in Neovim, or `SageFs: Switch Workflow` in VS Code. This restarts the session. See [What happens when you switch](#what-happens-when-you-switch).

| Feature | REPL | Live Testing | Hot Reload |
|:---|:---|:---|:---|
| Live testing available | ✅ Turn it on | ✅ Already on | ✅ Turn it on |
| Coverage tracking | ✅ Works | ✅ Works | ✅ Works |
| Failure narratives | ✅ Works | ✅ Works | ✅ Works |
| Test source navigation | ✅ Works | ✅ Works | ✅ Works |

---

## Real-World Scenarios

### Scenario 1: "I'm designing a domain model"

**Use REPL mode.** You'll be reshaping types constantly:

```fsharp
type Order = { Items: Item list; Status: OrderStatus }  // v1
// ... explore, test, think ...
type Order = { Lines: OrderLine list; Status: OrderStatus; PlacedAt: DateTimeOffset }  // v2
```

REPL mode lets you redefine `Order` as many times as you need. Turn on live testing to get feedback as your tests catch up with each design change, or start the session in the **Live Testing** workflow so it is already on.

### Scenario 2: "I'm building a Falco web app with Datastar"

**Use Hot Reload mode** (`target='live'`). Your types are defined in `.fs` files and are stable. You're iterating on handlers, views, and behavior:

```fsharp
let dashboardView model =
  Elem.div [] [
    Elem.h1 [] [ Text.raw (sprintf "Welcome, %s" model.UserName) ]
    // Change this, save, browser updates instantly
  ]
```

Save the file → Harmony re-points the method → SSE pushes to the browser → you see the change. (`dashboardView` is a function taking a parameter, which is what makes it patchable; see the shape matrix in [Hot Reload](hot-reload.md).) Turn live testing on to have your endpoint tests re-run too.

### Scenario 3: "I started in REPL mode but now I want browser hot reload"

Switch with `:SageFsWorkflow live` in Neovim, **SageFs: Switch Workflow** in VS Code, or `switch_workflow(target='live')` over MCP. The web dashboard cannot do this yet.

Your REPL definitions are gone, but your `.fs` files reload automatically. The new Hot Reload session picks up right where your persisted code left off.

### Scenario 4: "I got FS0037: Duplicate definition of type"

You're in **Hot Reload mode** and tried to redefine a type in the REPL. You have two options:

1. **Switch to REPL or Live Testing** if you need to reshape the type (for example, `:SageFsWorkflow repl` in Neovim). Both keep full type redefinition.
2. **Edit the `.fs` file instead**: file-level type changes trigger a full reload that handles the redefinition correctly. It's REPL-level redefinition that's blocked.

SageFs appends its own explanation to the FS0037 message when the session's REPL is
expression-only ([`SageFs.Core/WorkflowErrorContext.fs`](../SageFs.Core/WorkflowErrorContext.fs)):

> 🔄 Type redefinition is not available in the Hot Reload workflow (single-assembly FSI).
>    Switch to the REPL workflow for full type redefinition: the switch_workflow MCP tool, or 'SageFs: Switch Workflow' in VS Code.

Only the clients that can actually perform the switch are named. The dashboard shows the
workflow but has no button wired to the switch route yet, so it does not appear in the hint.

---

## Quick Reference

| | REPL (default) | Live Testing | Hot Reload |
|:---|:---|:---|:---|
| **`SessionWorkflow` case** | `Interactive` | `LiveTesting` | `HotReload` |
| **Type redefinition** | ✅ Full | ✅ Full | ❌ FS0037 |
| **Browser hot reload** | ❌ Manual refresh | ❌ Manual refresh | ✅ Automatic (see caveat) |
| **Live testing** | ✅ Available, off by default | ✅ Armed automatically | ✅ Available, off by default |
| **Rerun trigger** | n/a | Debounced keystrokes | n/a |
| **Coverage & diagnostics** | ✅ Full | ✅ Full | ✅ Full |
| **Best for** | Prototyping, exploration, scripts | TDD, red-green loops | Web apps, UI iteration |
| **FSI flag** | (default) | (default) | `--multiemit-` |
| **Switch target** | `repl` | `livetesting` | `live` |
| **Switch cost** | REPL state lost; same session id from VS Code/dashboard, a new session id via the MCP tool | REPL state lost; same session id from VS Code/dashboard, a new session id via the MCP tool | REPL state lost; same session id from VS Code/dashboard, a new session id via the MCP tool |
