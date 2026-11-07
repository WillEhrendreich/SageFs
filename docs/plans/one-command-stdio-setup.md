# One command to set SageFs up in an agent client

Roadmap item: `one-command-stdio-setup` (Next, Platform). Plan written 2026-11-07 against master 4827ab9a.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress (step 5 of 6) |
| Size | S for the snippets and the docs page, M with the command that writes client config |
| Branch and worktree | cohort member `p3-setup`, branch `cohort/p3-setup`, checkout `/home/will/scratch/cohort/p3-setup` |
| Last green commit | see `git log` on that branch: the commit after `test(setup): session options and one-command client setup, red` |
| Next action | Done so far (2026-11-07): `SageFs.Core/ClientSetup.fs`, the generated page `docs/client-setup.md`, `sagefs setup` (print, or `--write` to merge), tests green (steps 2 to 4). Left: Step 1: fetch each client's own MCP page (VS Code `mcp.json`, Cursor, Claude Desktop, Claude Code scopes) and put the file, key and date in a table here. The snippets use the shapes in FsiMcp's README and what I know of the clients; nothing was fetched on 2026-11-07, and `docs/client-setup.md` says so. Then Step 5 once `sagefs call` is on the page (it is in this tree: the setup output can print `sagefs call get_daemon_status` as the one-liner that proves the registration). |
| Decisions made | `sagefs setup` registers Claude Code for the user (`--scope user`), says so, and the page explains it. Nothing is written without `--write`; with it the command writes only what it printed. A config that is not strict JSON (comments, a trailing comma) is refused and left byte-identical, because merging would drop the comments; the message names the snippet to add by hand. A `sagefs` entry that is not the stdio bridge is refused and shown, never overwritten. The skill is installed only with `--write` (`--skip-skill` leaves it). |
| Surprises | VS Code's `mcp.json` allows comments, so the strict merge will refuse more VS Code files than the others. If that shows up in practice, the answer is a comment-preserving edit, not a looser parse. `sagefs setup` with no `--client` finds clients by the `claude` command on the path and by the config folders existing; a client with neither is skipped. |

## Why this item exists

FsiMcp's setup is two commands, `dotnet tool install -g FsiMcp` and `claude mcp add --scope user fsi -- fsi-mcp`, with a snippet for VS Code (`.vscode/mcp.json`, key `servers`, `"type": "stdio"`), one for Claude Desktop, Cursor and other `mcpServers` clients, and a no-install form (`dnx FsiMcp --yes`) (read from its README and `docs/how-it-works.md`). On the first turn after that, the agent calls `load_project`, then `eval`. A stranger sees a tool that works in a minute of typing.

SageFs's stdio form is as good in one respect and behind in others. `claude mcp add sagefs -- sagefs mcp` is the durable registration and it starts the daemon. Behind: the docs give a Claude Code command and one raw `mcpServers` block, and nothing for VS Code's differently shaped file; the skill that stops an agent looping on `dotnet build` is a separate command; and the agent's first minute is several tool calls long.

## What exists today (checked in the tree)

- `claude mcp add sagefs -- sagefs mcp` is in `Readme.md` (line 247), `docs/mcp-tools.md` (lines 110, 214, 865), `skills/sagefs/troubleshooting.md` (line 47) and the message `sagefs status` prints with no daemon (`SageFs/Program.fs`, around line 563). The raw JSON for clients that spawn a command is `{ "mcpServers": { "sagefs": { "command": "sagefs", "args": [ "mcp" ] } } }`. There is no VS Code block and no Cursor or Claude Desktop paragraph.
- The bridge (`SageFs/McpStdioBridge.fs`) checks the daemon, starts it if it is down with `--owner-pid` set to the bridge, waits for it, and bridges stdio to the daemon's HTTP endpoint. It also heals a stale `Mcp-Session-Id` in place (`isSessionInvalid`, `ForwardError.SessionInvalid`).
- A daemon the bridge starts dies with that agent session, which `docs/mcp-tools.md` says in "How long the daemon lives".
- The reason an HTTP registration loses its tools is already documented, in `docs/mcp-tools.md` ("No SageFs tools in your client" and the section on `404 Session not found`) and `skills/sagefs/troubleshooting.md`. Two causes. An HTTP client that fails its first connect usually caches the failure and does not retry after the daemon comes up. And the MCP session id lives in the daemon's memory, so a restart (which `install-local` does on purpose) leaves the client holding an id the daemon never minted, every call returns 404, and the protocol cannot recover an id in place. Stdio does not have either problem because the bridge owns the handshake and the client never sees a session id. So that part of the item exists. What is missing is that the first thing a stranger reads does not lead with it, and that no command says it at the moment it matters.
- `sagefs install-skill` writes the skill (`SageFs/SkillInstallCommand.fs`). `sagefs check` has a row that says whether it is installed.
- `sagefs call` (a shell front door to the daemon's tools) is on branch `worktree-agent-a9f482942bfb80dd5`, three commits ahead of master, and will land. It is not in this tree. The setup output should mention it once it is.
- The agent's first minute, from `AgentGuidance.firstMinute`: `get_daemon_status`, `list_sessions`, `get_available_projects`, a `create_*_session`, then `get_session_status` until Ready. That is five tool calls before the first `send_fsharp_code`, against FsiMcp's one (`load_project`).

## What a stranger goes through today, and what the item aims at

Counted from the docs, not timed. Seconds are measured by the benchmark harness (`repl-benchmark-against-other-tools`) on a quiet machine. Until then there is no seconds column here, on purpose, because this machine is loaded by other work.

| Step | SageFs today | FsiMcp (from its README) | Aim for SageFs |
|:--|:--|:--|:--|
| Install | `dotnet tool install --global SageFs` | `dotnet tool install -g FsiMcp` | same |
| Register with the client | `claude mcp add sagefs -- sagefs mcp` (project scope; `-s user` for everywhere) | `claude mcp add --scope user fsi -- fsi-mcp` | `sagefs setup` does this and the skill |
| Skill | `sagefs install-skill` | none needed | done by `sagefs setup` |
| Check | `sagefs check` (optional) | none | `sagefs setup` runs it |
| First message | agent makes five calls, then evaluates | agent calls `load_project`, then `eval` | see below |
| Daemon | started by the bridge, dies with the agent | worker started at `initialize` | unchanged, said once |

Shell commands to the first eval: 3 required for SageFs (install, register, install-skill) plus 1 optional (check), 2 for FsiMcp. Tool calls before the first eval: 5 against 1. The aim is two shell commands (install, `sagefs setup`) and a first-minute that an agent can do in fewer calls (see the last section).

## What done looks like

`sagefs setup` with no arguments detects the clients it can (the `claude` command on the path, a `.vscode` folder, the Cursor and Claude Desktop config locations), prints what it found, and for each one either writes the registration (Claude Code through its own `claude mcp add`) or prints the exact snippet and the file it goes in, installs the skill if absent, runs the environment check, and ends with the one sentence for what to say to the agent first. `sagefs setup --client vscode` (and `claude-code`, `cursor`, `claude-desktop`) does one client. Nothing writes to a file the person has not been shown, and nothing overwrites a registration called `sagefs` that points somewhere else without saying so. The docs page and the command print the same text, generated from one function, so neither can drift. The page opens with the stdio registration and gives the HTTP one with its failure mode and the fix underneath. A stranger's steps from nothing to a first eval are listed in the docs, in order, with a measured seconds column from the benchmark harness on a dated line.

## Design

Pure, `SageFs.Core/ClientSetup.fs`: `type Client = ClaudeCode | VsCode | Cursor | ClaudeDesktop` (closed DU, one `toToken`, `parse`, `all`), `snippet : Client -> Snippet` where `Snippet` holds the file, the key (`servers` for VS Code, `mcpServers` for the others) and the text, and `registrationCommand : Client -> string list option` (Claude Code only). `merge : existingJson: string -> Snippet -> MergeResult` adds the `sagefs` entry to a config file that may hold other servers, and refuses (typed reason) a file that is not valid JSON or already names `sagefs` with a different command. Edge, `SageFs/SetupCommand.fs`: detection, reading and writing the file, running `claude`. Every refusal is a DU case with one `describe`. A script generates the docs page from `Client.all` the way `scripts/gen-configuration-doc.fsx` does for the configuration page, and a test fails if the page is stale.

## The failing test to write first

`SageFs.Tests/ClientSetupTests.fs`: `WHY - every client's snippet is valid JSON, names the sagefs command and its mcp argument, and uses the key that client reads`. A property over `Client.all`. Fails to compile today. Then:

2. `WHY - merging into a config that already holds other servers keeps every one of them` (property over generated configs, with a twin that rewrites the file whole).
3. `WHY - a config that is not JSON is refused and left byte-identical`.
4. `WHY - merging twice changes nothing`.
5. `WHY - the setup page is what the generator prints now`, a stale-page ratchet in the style of the configuration page.
6. Outcome gate: in a sandbox with a fake home directory, `sagefs setup --client vscode --dir <temp>` writes a file that VS Code's schema accepts (checked against the documented shape), and a real `claude mcp add` is exercised only if `claude` is on the machine (otherwise the tier records "not run", never a skip that reads as pass).

## Steps

1. Read each client's MCP documentation and fix the facts here: the file location and key for Cursor and Claude Desktop, whether VS Code takes a workspace or a user file, and what each shows when a stdio server fails to start. Put them in this plan as a table with dates. The FsiMcp docs give the VS Code and `mcpServers` shapes, and I take those as a starting point until Step 1 verifies them.
2. Test 1 and `ClientSetup.fs`, then the docs page and its generator (tests 5), so the snippets land first even if the command takes longer.
3. `sagefs setup` (tests 2 to 4, 6), including the `install-skill` and `check` calls.
4. Put the HTTP failure explanation at the top of the setup page, one paragraph, with the link to the longer section.
5. When `sagefs call` lands, have `sagefs setup` print the one-liner that proves the registration worked.
6. A shorter first minute (see below) lands with `small-default-tool-list`.

Wire impact: none on the MCP wire. `Program.fs` gains a command, so the help text and `docs/mcp-tools.md` change; `Plugin: n/a, a new CLI command`.

## Measure first

- Whether `dnx SageFs --yes -- mcp` works the way FsiMcp's `dnx` form does. The bridge starts the daemon by running `sagefs`, so it needs the tool on the path or its own path to spawn. Check before promising a no-install form.
- Steps and seconds from nothing to a first eval on a clean machine for both tools, by the benchmark harness, on a quiet machine, with Will's go. The seconds are not written here.
- What each client shows when the registered command is missing from the path. That decides what `sagefs setup` must verify before it says done.

## Risks and dependencies

- Writing into a user's client config is the risky part. Merge, never rewrite; show the diff first; leave the file alone on any parse doubt.
- Client formats change. The generator and the stale-page ratchet are the guard, and the table of facts is dated.
- A shorter first minute needs a server-side change, which is a separate decision. One session-creation tool that picks the project from the working directory, and a wait for Ready inside it, would take five calls to two. `create_project_session` already accepts a project; whether a no-argument form exists, I did not check. Put it in `small-default-tool-list` or its own plan after the benchmark shows the cost.

## Size

S for snippets and page, M with the command.

## Decisions for Will

- Should `sagefs setup` default to user scope for Claude Code? The docs say local scope is the default of `claude mcp add`, and a stranger who registers it in one project and opens another sees no tools. Recommendation: user scope, said out loud.
- May `sagefs setup` edit a client's config file, or only print? Recommendation: write only after showing the change and getting a yes, and print-only when not interactive.
