# Multi-Session — Isolated Worker Processes

Run several F# sessions at once, each on a different project and in a
different state. Every session is a separate OS worker process, so one
session can't corrupt or crash another. SSE events carry a `SessionId`, so
editor windows watching different projects never see each other's output.
Create, switch, and stop sessions from any client.

This exists because I kept wanting to work on SageFs itself while also
having a session open on whatever project I was dogfooding it against, and
one shared FSI process for both is a recipe for a bad afternoon.

## Creating Sessions

Clients (editors, AI agents, the dashboard) create sessions on demand. The
daemon starts bare and waits for create requests.

```
POST /api/sessions/create        (on the MCP port, 37749)
{
  "workingDirectory": "path/to/project",
  "projects": ["path/to/MyProject.fsproj"],
  "workflow": "Interactive"
}
```

`workingDirectory` is the session's directory. Leave it out and the daemon
uses its own current directory, which is rarely what you want. `projects` is an
array of `.fsproj` paths (omit it to start bare). `workflow` is optional and
defaults to `Interactive`. Agents use the MCP tools instead:
`create_project_session`, `create_solution_session` or `create_bare_session`.

## Session Isolation

Each session is its own worker process plus its own FSI host process (so two), with its own:

- FSI instance
- Loaded project assemblies
- File watcher
- Test-runner state

If one session crashes, the others keep running. See
[Session Isolation](session-isolation.md) for the routing design behind
this.

### A session's working directory is its file watcher's root

The daemon gives each session a recursive file watcher rooted at the session's
working directory. That costs one inotify watch per directory under it, so a
working directory that is your home directory, a directory that holds it, or a
filesystem root is refused. The session still starts. What you see is an entry in
`/health`'s `componentFailures` named `file-watcher:<directory>`, with the reason
(`refused to watch <directory>: ... is the home directory or contains it`, or
`... is a filesystem root`) and this hint: open the session in a project
directory, not the home directory or a filesystem root, and restart it. Per that
hint, hot reload and live testing get no file events for the session until then.
A dotfiles repo at `$HOME` is refused too, because the cost is the number of
directories, not whether `.git` is there. See
[file watching](hot-reload.md#file-watching) for the rest.

## Session Management

| Action | HTTP | MCP tool |
|:-------|:-----|:---------|
| Create | `POST /api/sessions/create` | `create_project_session`, `create_solution_session`, `create_bare_session` |
| List | - | `list_sessions` |
| Switch | - | `switch_session` |
| Stop | - | `stop_session` |

Each client keeps its own active session. Switching in one client doesn't
move any other client. An editor forcing every other window and every
other agent onto whatever session it just switched to was the actual bug
this whole design exists to not have. VS Code, Neovim, and the dashboard all
expose these actions in their UIs.
