# Understanding SageFs Workflow Modes

SageFs has **two workflow modes** and **one independent feature** that people often confuse for a third mode. This guide clears that up completely.

## The 30-Second Version

```
+----------------------------------------------------------+
|                   YOUR SAGEFS SESSION                    |
|                                                          |
|  Pick ONE workflow:                                      |
|  +------------------+         +------------------+       |
|  |  REPL Mode       |  <swap> |  Live Mode       |       |
|  |  (default)       |         |  (WebLive)       |       |
|  |                  |         |                  |       |
|  |  [Y] Redefine    |         |  [N] No redefine |       |
|  |      types       |         |      (FS0037)    |       |
|  |  [N] No browser  |         |  [Y] Browser hot |       |
|  |      hot reload  |         |      reload      |       |
|  +------------------+         +------------------+       |
|                                                          |
|  Then toggle features independently:                     |
|  [x] Live Testing  <-- works in BOTH modes               |
|  [x] Coverage      <-- works in BOTH modes               |
|  [x] Diagnostics   <-- works in BOTH modes               |
+----------------------------------------------------------+
```

**Live testing is not a mode.** It's a feature you turn on or off regardless of which workflow you're in. Everything in the "features" row works identically in both REPL and Live mode.

---

## The Two Workflows, Explained

### REPL Mode (Interactive) — The Default

**What it feels like:** A supercharged F# Interactive session. You type code, it runs instantly. You change a type definition, re-evaluate, and it just works. You're sketching, exploring, prototyping.

**What you can do:**
- Redefine types (`type Order = ...`) as many times as you want
- Redefine modules, DUs, records, classes — everything
- Full interactive exploration with instant feedback
- All 50 MCP tools work normally

**What you give up:**
- No automatic browser refresh. If you're running a web app, you'll need to refresh the browser manually after editing `.fs` files.

**Who should use it:**
- You're designing domain types and changing their shape frequently
- You're exploring an API or library interactively
- You're writing and iterating on tests
- You're working in `.fsx` scripts
- You're not building a web app (or don't mind manual browser refresh)

### Live Mode (WebLive) — For Web Developers

**What it feels like:** Save a file, and your browser updates instantly. No rebuild, no restart, no manual refresh. You see the change in under a second.

**What you can do:**
- Edit function bodies, let bindings, expressions — browser updates on save via SSE
- Harmony runtime patching injects your changes into the running app
- SageFs auto-injects a dev-reload middleware into your ASP.NET pipeline — zero config
- All 50 MCP tools work normally

**What you give up:**
- You **cannot redefine types**. Trying to redefine a `type` in the REPL produces `FS0037: Duplicate definition of type`. This is a CLR constraint, not a SageFs bug (more on this below).
- You can still change function bodies, add new let bindings, and call functions — you just can't reshape a type definition once it exists in the session.

**Who should use it:**
- You're building a web app with Falco.Datastar, Giraffe, Saturn, or ASP.NET
- You want save-and-see-it browser feedback
- Your types are stable and you're iterating on behavior

---

## Decision Tree

```
Are you building a web app?
├── YES → Do you need instant browser refresh on save?
│         ├── YES → Use LIVE mode
│         └── NO  → Use REPL mode (you can switch later)
└── NO  → Use REPL mode (the default)

Are you frequently changing type definitions?
├── YES → Use REPL mode (Live mode blocks type redefinition)
└── NO  → Either mode works — pick based on browser needs

Not sure?
└── Start with REPL mode. Switch to Live when you want browser hot reload.
```

---

## Why Can't I Have Both?

This is the question everyone asks. The answer is a hard constraint in the .NET runtime itself:

**The chain of constraints:**

1. **Browser hot reload** needs to patch running methods without restarting the app
2. **Method patching** uses [Harmony](https://github.com/pardeike/Harmony), which hooks into the JIT compiler
3. **Harmony's JIT hook** only works when FSI emits all code into a **single assembly** (the `--multiemit-` flag)
4. **Single-assembly mode** means the CLR can't distinguish between "old version of Type X" and "new version of Type X" — they collide → `FS0037`

```
Hot reload needs Harmony → Harmony needs --multiemit- → --multiemit- blocks type redefinition
```

This is a **physical constraint of the CLR**, not a SageFs design choice. If Microsoft changes how FSI emits assemblies, or Harmony finds a way to work with multi-emit, this limitation goes away. Until then, you pick one or the other per session.

**The good news:** Switching is instant. You're never locked in.

---

## Switching Between Modes

| Editor | Command |
|:---|:---|
| **Neovim** | `:SageFsWorkflow live` or `:SageFsWorkflow repl` |
| **VS Code** | Command Palette → `SageFs: Switch Workflow` |
| **Web dashboard** | Use the session workflow controls |
| **MCP tool** | `switch_workflow(target='live')` or `switch_workflow(target='repl')` |

### What happens when you switch

1. SageFs creates a **new session** in the target mode
2. The old session is stopped
3. Any definitions you made in the REPL are **lost** (they lived in the old session's memory)
4. Your `.fs` files on disk are **untouched** — they reload into the new session automatically

**Tip:** If you have important REPL work, persist it to a `.fsx` file before switching. Use the `export_session_transcript` MCP tool to save your session as a runnable script.

### Dry-run preview

Not sure what you'd lose? Preview first:

```
switch_workflow(target='live', dryRun=true)
→ "Preview: switching from REPL to Live would lose 12 definitions and 5 cells"
```

### Auto-detection

When SageFs detects web-oriented packages in your project, it suggests Live mode:

| Package | Suggestion |
|:---|:---|
| Falco.Datastar, Starfederation.Datastar | "Datastar project detected — Live mode enables SSE-driven DOM morphing" |
| Falco, Falco.Htmx, Giraffe, Saturn, Microsoft.AspNetCore | "Web project detected — Live mode enables browser hot reload" |

The suggestion is a one-time prompt. SageFs never auto-switches — you always choose.

---

## Live Testing — A Feature, Not a Mode

This is the most common confusion. **Live testing works in both REPL and Live mode.** It is an independent feature you toggle on or off.

### What live testing does

When enabled, SageFs watches which functions your tests call (via a dependency graph). When you change a function, it automatically re-runs only the tests that cover that function. Results appear inline in your editor — green gutter marks for passing, red for failing, with failure details shown right next to the code.

### How to enable it

```
enable_live_testing     ← MCP tool (any editor via MCP)
```

Or through your editor's UI (Neovim: `:SageFsEnableLiveTesting`, VS Code: Command Palette).

### Why it's not a mode

Live testing doesn't change how FSI works. It doesn't affect type redefinition or hot reload. It's a layer that watches your eval results and runs tests. You can enable it in REPL mode while prototyping types. You can enable it in Live mode while building a web app. Both work identically.

| Feature | REPL Mode | Live Mode |
|:---|:---|:---|
| Live testing | ✅ Works | ✅ Works |
| Coverage tracking | ✅ Works | ✅ Works |
| Failure narratives | ✅ Works | ✅ Works |
| Test source navigation | ✅ Works | ✅ Works |

---

## Real-World Scenarios

### Scenario 1: "I'm designing a domain model"

**Use REPL mode.** You'll be reshaping types constantly:

```fsharp
type Order = { Items: Item list; Status: OrderStatus }  // v1
// ... explore, test, think ...
type Order = { Lines: OrderLine list; Status: OrderStatus; PlacedAt: DateTimeOffset }  // v2
```

REPL mode lets you redefine `Order` as many times as you need. Turn on live testing to get instant feedback as your tests catch up with each design change.

### Scenario 2: "I'm building a Falco web app with Datastar"

**Use Live mode.** Your types are defined in `.fs` files and are stable. You're iterating on handlers, views, and behavior:

```fsharp
let dashboardView model =
  Elem.div [] [
    Elem.h1 [] [ Text.raw (sprintf "Welcome, %s" model.UserName) ]
    // Change this, save, browser updates instantly
  ]
```

Save the file → Harmony patches the method → SSE pushes to the browser → you see the change. Live testing runs your endpoint tests automatically.

### Scenario 3: "I started in REPL mode but now I want browser hot reload"

Switch with `:SageFsWorkflow live` in Neovim, **SageFs: Switch Workflow** in VS Code, the dashboard workflow controls, or the corresponding MCP tool.

Your REPL definitions are gone, but your `.fs` files reload automatically. The new Live session picks up right where your persisted code left off.

### Scenario 4: "I got FS0037: Duplicate definition of type"

You're in **Live mode** and tried to redefine a type in the REPL. You have two options:

1. **Switch to REPL mode** if you need to reshape the type (for example, `:SageFsWorkflow repl` in Neovim)
2. **Edit the `.fs` file instead** — in Live mode, file-level type changes trigger a full reload that handles the redefinition correctly. It's REPL-level redefinition that's blocked.

SageFs already adds a hint to the FS0037 error message when you're in Live mode:
> 🔄 Type redefinition is not available in Live mode (single-assembly FSI). Switch to REPL mode for full type redefinition.

---

## Quick Reference

| | REPL Mode (default) | Live Mode |
|:---|:---|:---|
| **Type redefinition** | ✅ Full | ❌ FS0037 |
| **Browser hot reload** | ❌ Manual refresh | ✅ Automatic |
| **Live testing** | ✅ Full | ✅ Full |
| **Coverage & diagnostics** | ✅ Full | ✅ Full |
| **Best for** | Prototyping, exploration, scripts | Web apps, UI iteration |
| **FSI flag** | (default) | `--multiemit-` |
| **Switch to** | Editor, dashboard, or MCP workflow command | Editor, dashboard, or MCP workflow command |
| **Switch cost** | New session, REPL state lost | New session, REPL state lost |
