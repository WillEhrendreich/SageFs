# A token per sub-agent in Claude Code

Roadmap item: `a-token-per-subagent-in-claude-code` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress. Phase 0 ran on 2026-11-01 and the mechanism works (see "Measure first, results"). |
| Size | S for phase 0, M for phase 2 |
| Branch and worktree | none, work on master |
| Last green commit | none yet (the commit that carries this edit is docs only) |
| Next action | Step 2: the failing test that a string-referenced server is reported as `SharesParent`, then `SubagentTokenPlan.fs`, then the `sagefs check` warning described under "Measure first, results". |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | Four, all under "Measure first, results": `--strict-mcp-config` disables the wiring, the trust rule drops it silently in a headless run, a `tools:` allow-list cannot name an inline server's tools, and a dropped server falls back to the parent's. |

## What done looks like

Two Claude Code sub-agents spawned from one session show up in `get_cohort_status` as two members (`cap:<id>` each), hold separate claims and build leases, and are refused separately by the role gate. The model never sees a token: nothing in a transcript, a prompt or a tool argument carries one. The outcome gate is a real Claude Code run (scripted, `claude -p`) over the matrix: sub-agent mechanism (shared connection today; the chosen mechanism) by token source (minted by hand; minted by a tool outside the model). The shared-connection cell must show one member, the chosen-mechanism cells two. Until that run exists nothing here is proven, and the first phase of this plan is the experiment that decides whether the mechanism works at all.

## What exists today

Verified in the repo:

- A token outranks its connection. `SageFs/McpCapability.fs` `presentationFrom` reads `_meta["sagefs/memberToken"]` first (per call), then the hash claim the HTTP edge built from the `X-SageFs-Member-Token` header (per connection). `CapabilityTransport.presentationOf` in `SageFs.Core/Capability.fs` holds that rule.
- `useMemberToken` (`SageFs/McpServer.fs:833`) turns the header into a principal. The call filter (`createServerCaptureFilter`, around `McpServer.fs:575`) calls `presentToken` and binds `currentCapability`.
- The stdio bridge sends the header from `SAGEFS_MEMBER_TOKEN` (`SageFs/McpStdioBridge.fs:592`, `CapabilityTransport.headerFromEnvironment`). So a process started with that variable is a distinct member today.
- `docs/decisions.md` (around lines 1428 to 1538) records finding F8: sub-agents of one Claude Code session share one connection, so one member. "Only a per-call `_meta` can" tell them apart without a second connection. The roadmap item repeats that.
- `mint_member` (`SageFs/McpCapability.fs` `mintMember`) is conductor only and returns the raw token once, in the tool result. A tool result is part of the transcript.

Verified in Claude Code's own docs on 2026-10-26 (code.claude.com, fetched, first 100,000 to 200,000 characters of the hooks page and the first 100,000 of the sub-agents and MCP pages; the tails were not read):

| Claim | What the docs say |
|:--|:--|
| Hooks run in sub-agents | PreToolUse input has `agent_id` and `agent_type` "inside subagents". The sub-agents page documents `agent_type` only, so `agent_id` rests on the hooks page. |
| A hook can change a tool's arguments | `updatedInput` under `hookSpecificOutput` "replaces the entire input object". The visible text never says it applies to MCP tools and never mentions `_meta`, headers or request metadata. |
| A hook can match SageFs tools | Matcher `mcp__sagefs__.*`. A bare `mcp__sagefs` matches nothing. |
| A sub-agent can have its own MCP server | Sub-agent frontmatter `mcpServers`: an inline definition "scoped to this subagent only", full `.mcp.json` schema, `stdio`, `http`, `sse`, `ws`. "Inline servers ... are connected when the subagent starts ... and disconnected when it finishes. String references share the parent session's connection." |
| `.mcp.json` HTTP servers take headers | `headers` object with `${VAR}` expansion, and `headersHelper` (a command printing a JSON object of headers, 10 second limit, run "fresh on each connection", not per request; re-run once on a 401 or 403). |
| `SubagentStart` and `SubagentStop` hook events exist | Listed in the events section. What a `SubagentStart` hook can return was not in the text I read. |

Not verified, and each could change the design: that an inline sub-agent server accepts `headers` (the page says "same schema as `.mcp.json`" without saying so for headers); that `${VAR}` expands inside sub-agent frontmatter; whether `headersHelper` receives anything that names the sub-agent (environment variables, arguments); whether `updatedInput` is applied to MCP calls; whether Claude Code puts anything of its own in `_meta` on a `tools/call` beyond the capability block the MCP page mentions for revision 2026-07-28.

Conclusion for the roadmap sentence "a harness hook that sets `_meta` would be the way in": the documented hook outputs do not include `_meta`, and putting a token into `updatedInput` would put it in a tool argument, which the design forbids because transcripts keep arguments. The way in that the docs do support is a connection per sub-agent.

## Design

The mechanism is one inline MCP server per sub-agent, each with its own connection and its own token header. No daemon wire change is needed for the first version, because the header path exists.

```yaml
# .claude/agents/sagefs-analyst.md  (frontmatter)
name: sagefs-analyst
mcpServers:
  - sagefs:
      type: http
      url: http://localhost:37749/
      headers:
        X-SageFs-Member-Token: ${SAGEFS_ANALYST_TOKEN}
```

A string reference (`- sagefs`) shares the parent's connection and defeats the point; the plan's docs and a test must say so.

Where the token comes from is the real design question, and the answer cannot be the model:

1. By hand, for the experiment: the person runs `mint_member` once, exports the variable, starts Claude Code.
2. From outside the model (the version that scales): a small command, `sagefs member mint --role Analysis --scope src/Foo/ --ttl 60 --print-env`, which calls a daemon HTTP route and prints `export SAGEFS_ANALYST_TOKEN=...` for a launcher script. It needs the daemon to authenticate a human or script as the conductor without the MCP connection that is the conductor today. That is the operator credential in `a-token-for-every-daemon-call`. This plan depends on that item for its second phase.
3. From `headersHelper`: run at each connection, so at sub-agent start. Measured on 2026-11-01: it learns the name of the server it is serving (`CLAUDE_CODE_MCP_SERVER_NAME`), which is unique per sub-agent when the planner generates the names, and that is enough to mint or look up a token for it and print the header. Nothing is exported and nothing is in a transcript. It needs the operator credential too.

Pure core, new file `SageFs.Core/SubagentTokenPlan.fs`: given a list of sub-agent definitions (name, role, scope, ttl) it produces the list of mint requests and the frontmatter snippet, so a launcher is a thin shell. Types:

```fsharp
[<RequireQualifiedAccess>]
type TokenSource =
  | MintedByHand of envVar: string
  | MintedByLauncher of mintRequest: MintRequest
  | MintedByHeadersHelper of mintRequest: MintRequest

type SubagentWiring =
  | OwnConnection of agent: string * token: TokenSource
  | SharesParent of agent: string        // reported by the doctor check, never generated
```

Nothing in the daemon moves in phase 1. Phase 2 adds: `sagefs member mint` (new CLI verb in `SageFs/Program.fs`, the same place `sagefs status` and `sagefs hygiene` live) and one HTTP route `POST /api/members/mint` that wraps `McpCapability.mintMember`. A new HTTP route is a wire change: `EndpointContracts.fs` gains an entry, `apiVersion` moves only if an existing shape breaks (it does not, so it stays 4), `sagefs.nvim` needs nothing, `sagefs-vscode` needs nothing, and the commit carries `Plugin: n/a, an agent launcher route that no editor calls`. Token cost: zero, no MCP tool is added. The `mint_member` tool description gains one sentence saying the token travels in a transcript and a launcher is the safer path.

Persistence: none. Tokens stay in memory (see `tokens-survive-a-restart`).

A doctor check, `sagefs check`, can warn when `.claude/agents/*.md` references `- sagefs` by name, because that shares the parent connection.

## Tests

RED first.

1. Default tier, `SageFs.Tests/SubagentTokenPlanTests.fs`: `WHY - a sub-agent whose frontmatter names the server by string shares the parent connection, so it is reported as SharesParent`. Parses frontmatter text; fails to compile today (module missing).
2. Property: for any set of N sub-agent definitions with distinct names, the plan has N distinct env var names and N mint requests, and never a mint request wider than its declared role (uses `Grant` widening from `Capability.fs`).
3. Integration tier (`--integration-host`), extending `CapabilityHttpIntegrationTests.fs`: two real `HttpClientTransport` clients with different `X-SageFs-Member-Token` headers on two connections are two members in `get_cohort_status`; a third client sharing the first client's connection and passing the second token only in `_meta` is the second member (this proves the `_meta` path the roadmap item describes, which already works). This pins that the daemon half is finished.
4. Outcome gate (`--integration-claude`, a new tier that needs `claude` on the path and fails loudly when it is absent, as the VS Code journey runner does): a scripted `claude -p` run with an orchestrator prompt that spawns two sub-agents defined as above, each calling `get_cohort_status` once. Assert two distinct `cap:` ids and that no `sfm_` string appears in the transcript file under `~/.claude/projects/`. Matrix: sub-agent kind (string reference, inline http, inline stdio via `sagefs mcp`) by token source (hand, launcher). The string-reference row asserts ONE member, so the test also documents the failure it exists to prevent.
5. No wall-clock waits: the run ends when `claude -p` exits; the assertions read files after exit.

Simulation: this item adds no new state machine in the daemon, so no DST. The sim for the mint route belongs to `a-token-for-every-daemon-call`. The plan generator in test 2 is the property substitute.

## Steps

1. Phase 0. Done on 2026-11-01; the results are under "Measure first, results".
2. Who am I. The `get_cohort_status` reply ends with the caller's member and role (`You are cap:ecd9a0572ceebbed [Observer].`), and `join_cohort` says it too. It goes last, never first: the nvim parser (`cohort.parse_status`) anchors on `^Cohort ledger head: v`, and it ignores an unknown line after the `Integration session:` line. RED first: a test that the reply names the caller, for a token member and for a plain connection, and a busted spec in `sagefs.nvim` that `parse_status` still reads the reply with the line present (`Plugin: done`). `sagefs-vscode` does not parse this text. Nothing else in the plan can be verified from inside a sub-agent without it.
3. Test 1 and `SubagentTokenPlan.fs` (pure), listed in `SageFs.Core/SageFs.Core.fsproj`, and the `sagefs check` warnings: an agent file that references a server by name, a run under `--strict-mcp-config`, a `tools:` list that names an inline server's tools, and a project-scope agent whose exact folder has no `hasTrustDialogAccepted: true` entry in `~/.claude.json` (trust is not inherited from a parent folder). Owns those files and `SubagentTokenPlanTests.fs`.
4. The fallback hook: a `PreToolUse` hook that denies a SageFs call when the input carries an `agent_id` and its `mcp_server` is not the sub-agent's own (the planner names each own server after the agent), with a message that says the sub-agent must use its own server. Both fields are measured. A deny reaches the sub-agent with its reason. It extends `tools/agent-hooks` (a compiled program, because it runs before every call).
5. Test 3 in `CapabilityHttpIntegrationTests.fs`. File-disjoint from steps 2 to 4, can run in parallel.
6. Docs: `docs/agents.md` section on sub-agents, `skills/sagefs/agents.md` (the brief an orchestrator gives, including "your first call is get_cohort_status; if it does not name a `cap:` member, stop"), `docs/mcp-tools.md` member-token section. Carries a `Plugin: n/a` trailer.
7. Slice 0 of the operator credential (`a-token-for-every-daemon-call`): then the mint route, the CLI verb, the launcher and test 4. Owns `SageFs/Program.fs` (verb only), `SageFs/McpMintRoute.fs` (new), `EndpointContracts.fs` (one line).
8. Re-count the need for `tokens-survive-a-restart`. A minted token lives in daemon memory, and this repo redeploys the daemon on purpose, so a long orchestrated run loses its tokens at the next install.

## Measure first

The experiment, run on this machine with a throwaway repo and a test daemon on its own port pair and data dir:

| Experiment | What to record | What each result means |
|:--|:--|:--|
| Sub-agent file with inline `type: http`, `headers` using `${VAR}`; two such sub-agents with different tokens; each calls `get_cohort_status` | Number of distinct members in the reply; the `Mcp-Session-Id` values in the daemon log | Two members: mechanism works, go to step 2. One member or a connection error: headers or expansion are not honoured in sub-agent frontmatter; try the inline stdio form with `env`, and if that fails too the item stays Exploring and the harness needs a feature request. |
| Same with a string reference `- sagefs` | Number of members | One member confirms the docs and the F8 finding on the current Claude Code version. |
| `headersHelper` on an inline sub-agent server that dumps its environment and arguments to a file | Anything that names the sub-agent (name, `agent_id`, session id) | If yes, mechanism 3 is possible. If no, mechanism 3 is dead and only 1 and 2 remain. |
| PreToolUse hook on `mcp__sagefs__.*` that returns `updatedInput` with an extra key, and a daemon log of the incoming arguments and `_meta` | Whether the arguments arrive changed; whether `_meta` changed | Confirms or removes the last doubt about the hook route. It would still be a tool argument, so even a yes does not change the design. |
| Cost: tools loaded into the sub-agent | Tokens in the sub-agent's first turn with and without tool search | An inline server adds the SageFs catalog (about 28,000 tokens undeferred) to the sub-agent's context only; tool search is on by default per the harness research, which should cut it. |

### Measure first, results

Run on 2026-11-01 with Claude Code 2.1.316 and the 0.6.929 local build. A throwaway daemon on port 41749 with its own data dir. A headless conductor (`claude -p`) joined the cohort first and minted two Observer tokens. A headless parent then spawned sub-agents, and each called `leave_cohort` once, because the daemon's reply names the caller's member id.

| Experiment | Result |
|:--|:--|
| Inline `type: http` server with `headers` and `${VAR}`, two agents, two tokens | Works. The two calls were attributed to `cap:ecd9a0572ceebbed` and `cap:e1372696da61845a`, two members, each refused by the role gate as Observer. The Claude Code debug log shows `X-SageFs-Member-Token` sent on each inline connection. |
| The parent's inherited server (what a sub-agent without its own server uses) | One member. Three sub-agents all reported `mcp:m-753c5dc5b2657088`, the parent's connection. Finding F8 holds on 2.1.316. |
| String reference `- name` | A server added with `--mcp-config` was reported "MCP server not found" for a string reference, so a string reference resolves among the servers the settings define. It shares the parent's connection. |
| `headersHelper`, `updatedInput`, token cost | Not run. The first row works, so these no longer decide the item. |

Three things the experiment found that the docs do not say:

1. `--strict-mcp-config` disables sub-agent `mcpServers`. The debug log says "Skipping frontmatter MCP servers: blocked by --strict-mcp-config".
2. A project-scope agent (`.claude/agents/` in a repo) loses its `mcpServers` in a headless run unless the folder was trusted in an interactive session: "the folder its definition file came from is not trusted". A user-scope agent (`~/.claude/agents/`) connects.
3. A `tools:` allow-list that names an inline server's tool is rejected at spawn ("unrecognized"), because the name is checked before the server connects. Leave `tools:` out and deny what you do not want.

One finding changes the design. When an inline server is dropped (1 or 2), the sub-agent still runs. It falls back to whatever server its parent has and becomes the parent's member, which is the collapse this item exists to prevent. The daemon cannot see it: the call arrives on the parent's connection with no token, exactly like the parent's own calls.

`IdentityPolicy.TokenRequired` does not catch it either. The first version of this paragraph said it would, and that was wrong. `admitTokenless` (`SageFs.Core/Capability.fs:933`) admits a token-less call from the conductor, the orchestrator is normally the conductor, and a fallen-back sub-agent shares its connection, so it is admitted as the conductor. This is read from the code and was not run. The fallback has to be caught at the agent's end and by letting anyone ask who they are (see "Decisions").

One run reached the maintainer's real daemon through the inherited server: a `leave_cohort` scoped to a scratch path, refused, nothing changed. An experiment needs `--disallowedTools mcp__sagefs` to keep that from happening.

### Measure first, second pass

Later the same day, on throwaway daemons (default policy, and `TokenRequired` started in the repo under test and in another one), through `claude -p` and the stdio bridge. One sample each unless a row says otherwise.

| Question | Result |
|:--|:--|
| Does a sub-agent's `PreToolUse` hook input carry `agent_id`? | Yes. A sub-agent's calls carry `agent_id` and `agent_type`. The parent's `Agent` call carries neither. MCP calls also carry `mcp_server`. So a hook can tell a sub-agent from its parent and name the server the call went to. |
| Does a hook `deny` reach the sub-agent? | Yes, with the reason text verbatim. The sub-agent stopped and did not retry. |
| What does `headersHelper` do on an inline sub-agent server? | It runs, and its header is used: the call was attributed to the token the helper printed. It gets no arguments and runs in `~/.claude`. Its environment has `CLAUDE_CODE_MCP_SERVER_NAME` (the server's name in the agent file), `CLAUDE_CODE_MCP_SERVER_URL` and the parent's `CLAUDE_CODE_SESSION_ID`, and no `agent_id`. A unique server name per sub-agent is enough to choose or mint that sub-agent's token. |
| Is folder trust readable, and is it inherited? | Readable: `~/.claude.json`, `projects[<path>].hasTrustDialogAccepted`. Not inherited: an agent folder under the trusted `/home/will/Work` was still "not trusted". The doctor check looks up the exact folder. |
| What does an inline server cost a sub-agent that does not call it? | About 300 cached tokens out of roughly 103,000 for the whole session, with the session cost inside the noise. The MCP tools stay deferred behind `ToolSearch`, so the earlier estimate of about 28,000 undeferred tokens does not apply. One run each, so indicative. |
| Does a token survive a daemon restart? | No. After a restart the old token gets 0 tools from `tools/list`, and a call is refused with a message that names the restart. Claude Code only sees the empty list, so the sub-agent reported "NO SFX TOOL" and the message never reached the model. Changed after this run (#182): a bad token now lists `credential_status`, whose description is the refusal, and every call answers `403` with an `invalid_token` body. Checked against a throwaway daemon with curl; not yet re-run through Claude Code, Copilot CLI or opencode. |
| `TokenRequired`: does the call gate admit a token-less conductor? | Yes. `mint_member` called by name succeeded for the conductor in two repos. |
| `TokenRequired`: does the conductor see its tools? | Conductor of a repo other than the daemon's own: 3 tools before and after joining. Conductor of the daemon's own repo: 3 tools before joining, 72 after. The list filter reads the daemon's own cohort (`cohortOwnerFor mcpCtx None` in `createToolListFilter`), not the caller's. Claude Code fetches the list once at connect, before the join, so a Claude Code conductor had 3 tools in both cases. |
| Do servers added with `--mcp-config` reach sub-agents? | No. A sub-agent without its own server had no tool from one. Only servers from the settings are inherited, as the maintainer's own `sagefs` is. |

What this changes:

- `TokenRequired` cannot be the setting for an orchestrated Claude Code run today. The call gate would admit the conductor, but the conductor never sees the tools. Two defects, each a candidate issue: the list filter judges the conductor by the daemon's own repo, and a bad or stale token hides every tool, so the refusal that names the cause is never shown. Until both are fixed it stays off, and the fallback is caught by the who-am-I line, the hook and the doctor check.
- The who-am-I line must be the last line of the `get_cohort_status` reply. The nvim parser anchors on `^Cohort ledger head: v`, so a leading line makes `:SageFsCohort` read nothing.
- Mechanism 3 works. A launcher exporting variables is not the only way to deliver a token: a `headersHelper` can do it per sub-agent server, with nothing exported.

Still not measured: how Claude Code's HTTP client behaves when the daemon restarts under a live sub-agent (issue #164's report says it stays "not connected"); whether `headersHelper` re-runs on a 401 as the MCP page says; the same wiring under Cursor or Copilot; and `updatedInput` on MCP calls, which no longer decides anything.

## Risks and dependencies

- The sub-agents page says inline servers are connected "subject to the trust rule for the agent file's folder", and project-level frontmatter needs the workspace trust dialog accepted. An untrusted folder silently loses the server. The doctor check should say so.
- Plugin sub-agents ignore `mcpServers` ("Ignored for plugin subagents"), so a SageFs plugin cannot ship this wiring; the person's own `.claude/agents/` must.
- The Claude Code docs change; every fact above is dated 2026-10-26.
- Depends on: `a-token-for-every-daemon-call` (operator credential, phase 2), `each-agent-its-own-member` (done). Feeds: `drive-real-agents-as-members`, `watch-your-agents-from-your-phone` (nothing direct).

## Size

S for phase 0 and the pure planner (days). M for phase 2, because it waits on the operator credential and adds a CLI verb, a route and a new test tier.

## Decisions for Will

Proposed on 2026-11-01 after the experiment, waiting for an answer.

1. Make `TokenRequired` the daemon's default? Recommendation: no, keep `ConnectionsAllowed` and make `TokenRequired` the documented setting for an orchestrated run.
   - The policy is daemon-wide (`SAGEFS_IDENTITY_POLICY`). In a repo where someone else is the conductor, every other token-less MCP connection, a second window for instance, would lose everything but status.
   - It does not catch the fallback (see "Measure first, results"), so flipping it buys no protection against the failure this item is about.
   - Measured: it is unusable with Claude Code today, because the conductor never sees its tools (see "Measure first, second pass"). It stays off until the list filter and the bad-token behaviour are fixed.
   - What does catch it: the who-am-I line (step 2), the hook (step 4), the `sagefs check` warnings (step 3), and the outcome gate (test 4).
   - A policy that belongs to one cohort scope and is set by its conductor would fit an orchestrated run better than a daemon-wide switch. It would live in the cohort ledger and survive a restart. That is a separate item, worth writing only if the opt-in turns out to be awkward.
2. How do sub-agents get tokens without the model seeing them? Recommendation: neither an unauthenticated mint route nor waiting for all of `a-token-for-every-daemon-call`. Build slice 0 of the operator credential first (see that plan). Until then, tokens are minted by hand or by a conductor run, as in the experiment.
   - An unauthenticated `POST /api/members/mint` lets any local process mint an Implementer token, and an Implementer can run code. It would also let an agent mint itself a second identity, which defeats the accountability tokens exist for.
   - Waiting for the whole item delays the one piece this plan needs by weeks.
3. Ask Anthropic for a per-call `_meta` hook output? Recommendation: no. Per-sub-agent connections work, so there is nothing to ask for.

Research and the weighed options for all of this: `docs/agent-identity-research-2026-11-01.md`. Where it differs from the text above, the note wins. The main differences: a presented-but-unusable token gets an HTTP `403` with an `invalid_token` body (issue #182), the tool list depends on the presented credential only (issue #181), and a helper that mints on refusal covers a restart without persisting tokens.
