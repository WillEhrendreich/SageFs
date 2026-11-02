# Agent identity and credentials: what the standards say, what clients do, and what fits SageFs

Written 2026-11-01 against master `7f25f3e9` and the 0.6.929 local build. Claude Code 2.1.316. It backs issues #181 and #182 and the plans `a-token-per-subagent-in-claude-code`, `a-token-for-every-daemon-call` and `tokens-survive-a-restart`.

Every number below is either quoted from a page whose URL is given, or measured on a throwaway daemon or stand-in server and marked "measured". Anything neither is marked "not measured".

## The five problems

| | Problem | Where it shows |
|:--|:--|:--|
| A | A token the daemon cannot use (unknown, expired, revoked, lapsed) empties `tools/list`, so the agent never sees the refusal | #182 |
| B | `TokenRequired` builds the tool list from the connection's conductor seat in the daemon's own repo, so a conductor never sees its tools | #181 |
| C | A sub-agent whose own server is dropped falls back to the parent's connection and becomes the parent's member, and nothing says so | the sub-agent plan |
| D | Minting a token needs the conductor's MCP connection, so the token passes through a model | the sub-agent plan |
| E | A daemon restart ends every token | `tokens-survive-a-restart` |

## What the standards say

MCP revision 2026-07-28 is the latest dated revision (`llms.txt` lists 2024-11-05, 2025-03-26, 2025-06-18, 2025-11-25, 2026-07-28, draft).

- Invalid credentials. Authorization, "Token Handling": "Invalid or expired tokens **MUST** receive a HTTP 401 response." Insufficient scope is `403` with `WWW-Authenticate: Bearer error="insufficient_scope"`. https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization
- Per request. "authorization **MUST** be included in every HTTP request from client to server." Same page.
- No sessions. Streamable HTTP lists "Removal of the GET stream endpoint. Removal of protocol-level sessions." and says to ignore an `Mcp-Session-Id` header. https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http SEP-2567 is the proposal, summarised at https://dev.to/rabinarayanpatra/why-mcp-2026-07-28-spec-drops-sessions-and-goes-stateless-1gd
- The tool list. Tools page: the server "**MUST** respond to `tools/list` requests with the set of tools currently available to the requesting client. This set **MAY** be empty and **MAY** change over time ..., but **MUST NOT** vary per-connection or as a side effect of other requests on the connection. The set **MAY** vary by the authorization presented on the request ... since credentials are per-request input, not connection state." https://modelcontextprotocol.io/specification/2026-07-28/server/tools
- The spec says nothing about what `tools/list` returns for a bad token, and nothing about mapping a token to a user or agent.

Industry practice, from vendor guides and issue threads (a synthesis, not a standard): 401 for a missing or invalid token, a filtered or empty list only for a valid caller with no grants, the same rule enforced again at call time, and a warning that a 200 with an empty list can hide an auth failure (https://docs.litellm.ai/docs/mcp_control, https://openfga.dev/docs/modeling/agents/mcp-authorization, https://github.com/redpanda-data/connect/issues/4916).

Two consequences for SageFs. Problem A breaks the 401 MUST. Problem B breaks the "MUST NOT vary per-connection or as a side effect of other requests" rule, because the list changes after `join_cohort`. The connection-bound identity SageFs uses for token-less callers (`mcp:<session>`) is the thing the new revision removes.

## What the clients do

Claude Code 2.1.316 already sends `server/discover` first, the 2026-07-28 handshake, and falls back to `initialize` (measured, in the stand-in server's log). Whether the real daemon answers `server/discover` is not measured.

Documented (https://code.claude.com/docs/en/mcp, https://code.claude.com/docs/en/sub-agents):

- `headersHelper` runs "fresh on each connection", with a 10 second limit, and receives `CLAUDE_CODE_MCP_SERVER_NAME` and `CLAUDE_CODE_MCP_SERVER_URL`. Measured: it also gets no arguments, runs in `~/.claude`, and has no `agent_id`.
- `list_changed`: an interactive session re-fetches the list. `-p` mode "refreshes only the tool list". A failed refresh keeps the previous tools.
- The docs say a 401 or 403 on a tool call re-runs the helper and retries once. That section was read through a summary, so it was not verified verbatim.

Measured with a stand-in MCP server that rotates the token it accepts after the first call, one sub-agent per row, a descriptive JSON body on every rejection:

| Server response to the stale token | Helper runs | What the sub-agent saw |
|:--|:--|:--|
| `401` with `WWW-Authenticate: Bearer` | 1 | Claude Code started OAuth dynamic client registration against the server and failed. No re-run, no retry. The model got `Dynamic Client Registration rejected (HTTP 401): <body>`. |
| `401` without the header | 1 | The same. The status alone starts the OAuth path. |
| `403`, with or without `WWW-Authenticate` | 2 | The helper re-ran, Claude Code reconnected with the fresh token and retried the call once. The call succeeded. |
| `403`, helper that returns the same stale token | 2 | After the retry the model got `MCP server "rotsrv" rejected the credential from its headersHelper (check the helper and run /mcp to reconnect ...)`. |
| `403` or `401`, static `headers` | 0 | The model got the server's response body verbatim (`Error POSTing to endpoint: {"error":"invalid_token","error_description":"..."}`). |
| `200` with an empty tool list (what SageFs does today) | 1 | `NO SFX TOOL`. No cause reaches the model. |

Two things follow. The text of a rejection body reaches the model in every HTTP-error case, so the daemon's existing sentence ("the daemon restarted ... ask the conductor to mint a new one") can be delivered. And in Claude Code the status code decides the behavior: only `403` refreshes and retries.

Hook facts (measured): a sub-agent's `PreToolUse` input carries `agent_id`, `agent_type` and, for MCP calls, `mcp_server`. A hook `deny` reaches the sub-agent with its reason verbatim.

### The other clients

Measured on 2026-11-01 against the same stand-in server (`server2.mjs`: switchable mode, token rotated after the first good call, or stale from the first request), one small prompt per cell, static `X-Test-Token` header. Copilot CLI 1.0.92 ran on `claude-haiku-4.5`, opencode 2.1.2 on `glm-5.3-flash-free`. Command Code 1.88.2 could not run a model (the account hit its weekly limit), so only its connection behavior is measured. Cursor's CLI is not logged in, and VS Code is a GUI, so those two are read from documentation and source.

| Cell | Claude Code 2.1.316 | Copilot CLI | opencode | Command Code | VS Code (source) |
|:--|:--|:--|:--|:--|:--|
| `403` on a call, static header | body reaches the model | body reaches the model, no retry | body plus `(HTTP 403)` reaches the model, no retry | not measured | treated like `401`, see below |
| `401` on a call, static header | OAuth registration fails, body reaches the model | OAuth discovery probes, then `Re-authentication required for MCP server "rot". Sign in again through your MCP client's authentication settings.` The body is dropped. | OAuth discovery probes, then `requires a login before it can register a client`. The body is dropped. | not measured | same path as `403` |
| Refused at connect (any status) | not measured (the table above is mid-run; a stale token from the first request with the real daemon gave 0 tools) | the model sees no tool, `NO TOOL` | the model sees no `rot` server | `401` probes four OAuth metadata URLs, `403` probes none; `mcp list` shows the server as enabled either way | `Error` state |
| `200` with an empty list | `NO TOOL` | `NO TOOL` | indistinguishable from a refused connect | `mcp list` shows it as enabled | cached as a normal empty list |
| `200`, in-band refusal on a call | reaches the model | reaches the model | reaches the model | not measured | rethrown |
| Where a human finds the cause | `/mcp` | the log: a `403` body is in a `server/discover failed` warning, a `401` is logged as `Browser-based OAuth required` | not found | `/mcp` per its docs | `403 status sending message to <url>: <body>` |

Other facts from the same runs and sources:

- Copilot CLI sends `server/discover` first, as Claude Code does, and on a `403` logs `server/discover failed; retrying with legacy initialize`, then stops after the second refusal.
- opencode reaches MCP tools through a search-and-run `execute` tool, not as listed tools. A prompt that names tool names finds nothing.
- Command Code's `mcp add` probes the URL once with no headers, which would hit a daemon route that has no token.
- VS Code (`extHostMcp.ts`, `_fetchWithAuthRetry`): `isAuthStatusCode` is `401 || 403`, so both start OAuth discovery from `WWW-Authenticate` and retry up to three fetches. Configured headers come from an object that is never reassigned, so they are fixed for the life of the connection. The final error includes the response body unparsed. Its server set is global in the files read; custom agent files were not read.
- Header interpolation: Copilot, Command Code and Claude Code take `${VAR}`; Cursor takes `${env:NAME}` (docs); opencode's schema shows static `headers` only (not tested). None of the five has a helper command except Claude Code. Copilot's binary contains the string `HTTP2_HEADER_REFRESH`, and its error type is named `HeaderRefreshHttpClient`; the setting that drives it was not found.

Sub-agent identity across clients:

| Client | A sub-agent with its own credential | What happens when it fails |
|:--|:--|:--|
| Claude Code | inline `mcpServers` in agent frontmatter, measured | silently falls back to the parent's server, so the sub-agent becomes the parent's member |
| Copilot CLI | `mcp-servers` in `.github/agents/*.agent.md` with `X-Test-Token: ${ROT_TOKEN}`, run through the `task` tool, measured: the server saw the sub-agent's token | a same-named server in the parent loses to the agent's own entry. A refused sub-agent token leaves it with no tool, and the parent's token was never used for its calls. An unset `ROT_TOKEN` sends the literal text `${ROT_TOKEN}` with no warning. |
| opencode | none: the agent schema has `tools` and `permission` but no `mcp` or `headers` | not applicable |
| Command Code | none: the sub-agent frontmatter lists no MCP key (docs) | not applicable |
| Cursor | none: "Subagents inherit all tools from the parent, including MCP tools" (docs) | not applicable |
| VS Code | none found: the server set is global in the files read | not applicable |

Two clients can give a sub-agent its own identity, and one of them fails closed. In the other four, every sub-agent shares the parent's connection, which is F8 as it stands. Plan C1 therefore covers Claude Code and Copilot CLI, and C4 to C6 are detectors only for Claude Code.

## Precedent for a local daemon credential

- Jupyter Server authenticates every request with a token, accepted as `Authorization: token ...`, on by default, and runs authorization after authentication. https://jupyter-server.readthedocs.io/en/latest/operators/security.html Its runtime file is written through `secure_write`, which checks for mode 0600. That check failed for users on mounted filesystems (CIFS reported 0o677) and `jupyter_core` 4.6.2 added `JUPYTER_ALLOW_INSECURE_WRITES` for them. https://github.com/jupyter-server/jupyter_server/pull/77 The lesson for an `operator.token` file: verify the mode after writing, and decide in advance what happens when the filesystem cannot hold it.
- Attenuating tokens. Macaroons, Biscuit and the IETF draft on attenuating agent tokens (https://datatracker.ietf.org/doc/html/draft-niyikiza-oauth-attenuating-agent-tokens-00) all make the same rule: a derived token can only narrow its parent. SageFs already enforces that at mint time (`Grant.wideningsOf`, `MintRefusal.WouldWiden`). Offline derivation is what those designs add, and a local daemon that is always reachable does not need it. CAPMAS applies macaroons to orchestrators and sub-agents (https://arxiv.org/html/2609.06500v1).

## Options, weighed

Criteria, from how this product is built: C1 follows the standard and its direction; C2 works in Claude Code today (measured) and fails visibly elsewhere; C3 the agent and the human can see the cause; C4 fails closed and makes the wrong state hard to reach; C5 size and risk in this code base (SDK `ModelContextProtocol` 1.0.0-rc.1, 2.3.0 not yet adopted); C6 local first, no external identity provider.

### A. A presented token the daemon cannot use (#182)

| Option | C1 | C2 | C3 | C4 | C5 | Verdict |
|:--|:--|:--|:--|:--|:--|:--|
| A1. Keep `200` with an empty list | breaks a MUST | works, silently | nothing | fails closed but invisible | none | reject |
| A2. `401` with `WWW-Authenticate` and a body | follows the MUST | Claude Code starts OAuth and fails; body reaches the model; no refresh | model: yes. human: a confusing OAuth error | closed | S in the existing `useMemberToken` edge | acceptable, worse today |
| A3. `403` with an `invalid_token` JSON body | departs from the MUST (the spec reserves 403 for insufficient scope) | helper refresh and retry works; static header shows the body | model and human: yes | closed | S, same edge | recommended for a presented-but-unusable token |
| A4. Keep the empty list, add a diagnostic tool | none | the model must know to call it | partial | closed | M | reject |
| A5. Full list, refuse at the call | allowed (list may vary by credential) | works; refusal text reaches the model | yes | opens the list to a bad token | S | acceptable fallback |
| A6. A bad token lists exactly one tool, `credential_status`, whose description says the credential is unusable and to call it. Every call, that one included, answers `403` with the `invalid_token` body | allowed (list may vary by credential) | measured against the daemon (0.6.929 plus the #182 commits, a stale token from the first request, one run each): Claude Code 2.1.316 (`haiku`), Copilot CLI 1.0.92 (`claude-haiku-4.5`) and opencode 2.1.2 (`glm-5.3-flash-free`) all listed `credential_status` and the model quoted its description, which holds the cause and the fix. Before, Copilot and opencode gave the model no tool and no cause. A call to the tool answered `403` with the body (curl) | model: yes in every client measured. human: yes | closed: no tool but the one that refuses | M | built, see below |

Reasoning for A3. SageFs is not an OAuth resource server: it publishes no protected-resource metadata and has no authorization server, so a `401` starts a protocol it cannot finish, and measured it also blocks the one behavior that heals the restart case. A `403` with a body in the standard `invalid_token` shape gets the refresh and retry in Claude Code, and the body reaches the model everywhere else. The cost is a departure from "MUST 401", so it goes in one named function with a test that pins the status, and a gate that pins the Claude Code behavior so a client update that changes it fails loudly. A token that is missing under a policy that requires one stays `401`, because there is nothing to refresh and that is exactly what the spec describes.

The other clients support A3 and expose its limit. A `403` is never worse than a `401` in any client measured. In Copilot and opencode a `401` replaces our sentence with an OAuth sign-in message, and a `403` delivers it; Command Code probes OAuth metadata on a `401` and not on a `403`. VS Code treats the two alike, so the status costs nothing there. The limit is a token that is stale from the first request, for example a sub-agent launched after a restart with an environment variable minted before it. Then Copilot and opencode show the model no tool and no cause whatever the status, and the cause is only in a log. A6 closes that gap, because a list with one tool the model can call turns a connect-time refusal into a call-time one.

A related cheap check: Copilot sends the literal text `${ROT_TOKEN}` when the variable is unset. A presented token that begins with `${` or `$` can get its own refusal that names an unset environment variable.

### B. The tool list depends on connection state (#181)

| Option | C1 | C2 | C3 | C4 | C5 | Verdict |
|:--|:--|:--|:--|:--|:--|:--|
| B1. Look up the caller's seat in every scope instead of the daemon's own | still varies by connection state | Claude Code lists once, before the join, so it still shows 3 tools | no | patches one symptom | S | reject |
| B2. Send `list_changed` when the seat binds | the list still changes as a side effect | depends on the client re-fetching; the stateless revision has no GET stream for it | no | no | M | reject |
| B3. The list depends on the presented credential only. Seat-based authority is enforced at the call | conforms | works with a client that lists once | yes | yes: the state that varies is the credential | M, needs problem D solved first | recommended |
| B4. Remove `TokenRequired` | n/a | n/a | n/a | leaves narrow tokens beside an open door | S | reject |

B3 depends on D: under `TokenRequired` the conductor must present a credential instead of a seat, so something other than "the first joiner" has to be able to mint. Until then the honest statement is that `TokenRequired` works only with a client that calls tools by name (the integration tests do).

### C. The sub-agent fallback

| Option | Measured | Verdict |
|:--|:--|:--|
| C1. One inline server and one token per sub-agent | works | do it |
| C2. A token in `_meta` from a hook | hooks cannot set `_meta`, and a token in `updatedInput` lands in the transcript | reject |
| C3. The daemon detects it | impossible: the call carries the parent's credential | reject |
| C4. A `PreToolUse` hook denies a sub-agent a server that is not its own | `agent_id`, `agent_type`, `mcp_server` present; the deny reaches the model | do it |
| C5. The who-am-I line, so the agent and any test can check | not built | do it, last line only |
| C6. `sagefs check` warns on a string-referenced server, a strict MCP config, a `tools:` list naming inline tools, an untrusted project folder | trust is readable and not inherited | do it |
| C7. The orchestrator holds only a coordination role, so an inherited fallback is refused by the role gate with a message naming the role and the tool | not measured | worth measuring |

C7 would make the fallback structurally loud with machinery that already exists (the role gate), where C4 to C6 are detectors. It needs an orchestrator that can mint while holding a narrow token, which the mint rules (`checkMint`) do allow for "a conductor acting under a token", so it is a measurement and not a design yet.

### D. Minting without a model

| Option | C1 | C2 | C4 | C5 | C6 | Verdict |
|:--|:--|:--|:--|:--|:--|:--|
| D1. An unauthenticated mint route | n/a | works | any local process can mint an Implementer token, which can run code | S | yes | reject |
| D2. Wait for all of `a-token-for-every-daemon-call` | | | | L | | reject, too late |
| D3. Slice 0: an owner-only `operator.token` file, `Authorization: Bearer sfo_...`, one route | follows the Jupyter pattern | works with `headersHelper` and a CLI verb | the credential is a file only the user can read; the plan states the same-user limit | M | yes | recommended |
| D4. A real OAuth authorization server in the daemon | the standard path | Claude Code's native flow | | L, needs a browser consent step that a headless launch does not have | needs a flow and metadata | later; opaque bearer tokens stay compatible with it |
| D5. A Unix domain socket with peer credentials | kernel-attested uid | Claude Code's `http` type needs a TCP URL, so only the stdio bridge could use it | strong on Unix | M | Unix only | not as the primary |

D3 detail from the Jupyter precedent: write to a temp file, set mode 0600, rename into place, verify the mode, and when the filesystem cannot hold it refuse to enable the route with a message that says how to fix it. Add an escape hatch only when someone needs one.

### E. A restart ends every token

| Option | Keeps identity across a restart | Persistent state | Verdict |
|:--|:--|:--|:--|
| E1. Persist the token hashes | yes (`cap:<id>` is the same member, claims stay) | a file of hashes; weakens "holds nothing to steal" | only if identity continuity is shown to matter |
| E2. The helper mints a fresh token when the call is refused (needs A3 and D3) | no: a new token is a new member, and the old member's claims are orphaned until the conductor reassigns them | none | do it first: it restores access with no persistence |
| E3. Self-contained signed tokens (a key in the data dir) | yes | a key plus a revocation list; a new token format | not now |
| E4. Document that a restart ends an orchestrated run | n/a | none | the floor |

Restarts come in bursts: 22 `release v` commits in 21 days, up to five a day from 10-16 to 10-25, none since, and the running daemon has been up 5.5 days. E2 depends on measuring that the refresh and retry really works against the daemon (it does against the stand-in), and on counting how many claims a restart orphans before E1 is worth its privacy cost.

## Recommended order

1. The who-am-I line (last line), the planner, the `sagefs check` warnings and the hook (C4 to C6). No dependencies.
2. The HTTP-level refusal for a presented-but-unusable credential (A3) in the existing `useMemberToken` edge, with a pin test, plus the one-tool list (A6) once it is measured. Built on 2026-11-01 (`edgeAnswer`, `credentialStatusTool` in `SageFs/McpCapability.fs`); #182 is fixed on master and waits for a release.
3. Slice 0 of the operator credential (D3).
4. The credential-only tool list (B3), which fixes #181 and makes `TokenRequired` usable.
5. The helper that mints on refusal (E2).
6. Measure C7, and the orphaned-claims count that decides E1.

## Decisions for Will

1. `403` or `401` for a presented-but-unusable token. Recommendation: `403` with an `invalid_token` body, behind one function and a pin test, with `401` kept for a missing token under a policy that requires one. The cost is a departure from a MUST. The benefit is the only refresh-and-retry path measured in the client SageFs is built for.
2. Accept B3, that the tool list depends on the presented credential only. It drops the conductor-by-connection shortcut for `TokenRequired`, and it is what the stateless revision requires.
3. If `operator.token` cannot be made owner-only, refuse the route with a message that says how to fix it. No escape hatch until someone asks.
4. Measure C7 before designing it.
5. Add A6 beside A3, so a token that is stale from the first request still shows the model a tool and a cause. Measure it against the stand-in in Claude Code, Copilot and opencode first.

## Not measured

- Command Code with a model: the account is at its weekly limit, so its mid-run and model-visible behavior is open, and a `403` or `401` mid-run is untested there.
- Cursor and VS Code were not run. Cursor's CLI is not logged in and approving a server writes to the user's Cursor config. Everything about them comes from documentation and source, and the Cursor docs say nothing about `401` or `403`.
- Copilot's header refresh: the error type is named `HeaderRefreshHttpClient` and the binary has the string `HTTP2_HEADER_REFRESH`, but no configuration key was found.
- opencode with a header that changes: `headers` is static in its schema, and `${VAR}` interpolation was not tested.
- A6 was measured with a stale token and the model asked to say why it could not call a tool. The models read the description in all three clients; no run had the model call `credential_status` itself, so the retry path through Claude Code's `headersHelper` against the real daemon is still only measured on the stand-in. Whether the real daemon answers `server/discover` is open.
- Claude Code in an interactive session instead of `-p`; the orchestrator-on-a-narrow-token option; what a restart orphans in practice; the Windows ACL.
- Each cell above is one run with a small model. They show what each client does with a status code. They do not show how often a model reads the result correctly.
