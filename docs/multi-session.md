# 🔀 Multi-Session — Isolated Worker Processes

Run multiple F# sessions simultaneously — different projects, different states. Each session is an **isolated worker sub-process** (Erlang-style fault isolation). SSE events are tagged with `SessionId` — no cross-talk between editor windows watching different projects. Create, switch, and stop sessions from any frontend.

## Creating Sessions

Sessions are created on demand by clients (editors, AI agents, the dashboard). The daemon starts bare and waits for session creation requests.

```
POST /api/sessions/create
{
  "projectPath": "path/to/MyProject.fsproj",
  "workingDirectory": "path/to/project"
}
```

## Session Isolation

Each session is a separate OS process with its own:
- FSI instance
- Loaded project assemblies
- File watcher
- Test runner state

Sessions cannot interfere with each other. If one crashes, others continue running.

## Session Management

- **Create**: `POST /api/sessions/create` or MCP tool `create_session`
- **List**: MCP tool `list_sessions`
- **Switch**: MCP tool `switch_session`
- **Stop**: MCP tool `stop_session`

All editor plugins support session management through their respective UIs.

See also: [Session Isolation](session-isolation.md)
