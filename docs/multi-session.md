# Multi-Session — Isolated Worker Processes

Run several F# sessions at once, each on a different project and in a different state. Every session is a separate OS worker process, so one session cannot corrupt or crash another. SSE events carry a `SessionId`, so editor windows watching different projects never see each other's output. Create, switch, and stop sessions from any client.

## Creating Sessions

Clients (editors, AI agents, the dashboard) create sessions on demand. The daemon starts bare and waits for create requests.

```
POST /api/sessions/create        (on the MCP port, 37749)
{
  "workingDirectory": "path/to/project",
  "projects": ["path/to/MyProject.fsproj"],
  "workflow": "Interactive"
}
```

`workingDirectory` is required. `projects` is an array of `.fsproj` paths (omit it to start bare). `workflow` is optional and defaults to `Interactive`. Agents can use the `create_session` MCP tool instead.

## Session Isolation

Each session is a separate OS process with its own:

- FSI instance
- Loaded project assemblies
- File watcher
- Test-runner state

If one session crashes, the others keep running. See [Session Isolation](session-isolation.md) for the routing design.

## Session Management

| Action | HTTP | MCP tool |
|:-------|:-----|:---------|
| Create | `POST /api/sessions/create` | `create_session` |
| List | — | `list_sessions` |
| Switch | — | `switch_session` |
| Stop | — | `stop_session` |

Each client keeps its own active session. Switching in one client does not move any other client. VS Code, Neovim, and the dashboard all expose these actions in their UIs.
