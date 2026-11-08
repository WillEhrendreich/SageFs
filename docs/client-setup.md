# Set SageFs up in your agent client

`sagefs setup` prints what is below for the clients it finds, and `sagefs setup --client <name>` prints one client's part (`claude-code`, `vscode`, `cursor`, `claude-desktop`). The part between the markers is generated from [`SageFs.Core/ClientSetup.fs`](../SageFs.Core/ClientSetup.fs) by `dotnet fsi scripts/gen-client-setup-doc.fsx`, and a test fails when it is stale. Each client's section names the documentation page its file location, key and entry shape were read from, and the date. Cursor's page lists `type: "stdio"` as required in its field table while its example leaves it out, so the Cursor snippet carries it.

<!-- GENERATED: client-setup (scripts/gen-client-setup-doc.fsx) -->
### Register SageFs: stdio, the form that keeps its tools

Run the daemon-starting bridge as a stdio server. The client starts `sagefs mcp`, the bridge starts the daemon if it is down, and the client talks to the bridge over a pipe.

```bash
dotnet tool install --global SageFs
claude mcp add --scope user sagefs -- sagefs mcp
```

`--scope user` registers it for every project. Without it Claude Code registers for the current project only, and a second project opens with no SageFs tools.

#### Claude Code

File: .mcp.json in the project (or ~/.claude.json for every project). Key: `mcpServers`. Read from https://code.claude.com/docs/en/mcp on 2026-11-07.

```json
{
  "mcpServers": {
    "sagefs": {
      "command": "sagefs",
      "args": [ "mcp" ]
    }
  }
}
```

#### VS Code

File: .vscode/mcp.json in the workspace (or `MCP: Open User Configuration` for every workspace). Key: `servers`. Read from https://code.visualstudio.com/docs/copilot/reference/mcp-configuration on 2026-11-07.

```json
{
  "servers": {
    "sagefs": {
      "type": "stdio",
      "command": "sagefs",
      "args": [ "mcp" ]
    }
  }
}
```

#### Cursor

File: .cursor/mcp.json in the project (or ~/.cursor/mcp.json for every project). Key: `mcpServers`. Read from https://cursor.com/docs/context/mcp on 2026-11-07.

```json
{
  "mcpServers": {
    "sagefs": {
      "type": "stdio",
      "command": "sagefs",
      "args": [ "mcp" ]
    }
  }
}
```

#### Claude Desktop

File: claude_desktop_config.json (Settings, Developer, Edit Config). Key: `mcpServers`. Read from https://modelcontextprotocol.io/quickstart/user on 2026-11-07.

```json
{
  "mcpServers": {
    "sagefs": {
      "command": "sagefs",
      "args": [ "mcp" ]
    }
  }
}
```

### Why an HTTP registration loses its tools

`claude mcp add --transport http sagefs http://localhost:37749/` works until the daemon restarts. Two things go wrong. A client that fails its first connect usually caches the failure and does not retry after the daemon comes up. And the daemon keeps the `Mcp-Session-Id` in memory, so after a restart (which `install-local` does on purpose) the client holds an id the daemon never minted and every call answers `404 Session not found`. The protocol cannot recover an id in place. Over stdio the bridge owns the handshake and heals the id itself, and the bridge keeps serving across a daemon stop and start, so the client never sees either problem.
<!-- END GENERATED -->

## After the registration

Install the skill so your agent works in the REPL and does not loop on `dotnet build`: `sagefs install-skill`. `sagefs check` says whether the skill, the daemon and the SDK are in place. A daemon the bridge starts lives as long as your agent session; start `sagefs` yourself (or run it as a service) if you want it to stay up.
