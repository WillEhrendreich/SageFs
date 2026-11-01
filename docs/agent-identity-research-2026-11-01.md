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

Reasoning for A3. SageFs is not an OAuth resource server: it publishes no protected-resource metadata and has no authorization server, so a `401` starts a protocol it cannot finish, and measured it also blocks the one behavior that heals the restart case. A `403` with a body in the standard `invalid_token` shape gets the refresh and retry in Claude Code, and the body reaches the model everywhere else. The cost is a departure from "MUST 401", so it goes in one named function with a test that pins the status, and a gate that pins the Claude Code behavior so a client update that changes it fails loudly. A token that is missing under a policy that requires one stays `401`, because there is nothing to refresh and that is exactly what the spec describes.

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
2. The HTTP-level refusal for a presented-but-unusable credential (A3) in the existing `useMemberToken` edge, with a pin test. This fixes #182 without waiting for anything.
3. Slice 0 of the operator credential (D3).
4. The credential-only tool list (B3), which fixes #181 and makes `TokenRequired` usable.
5. The helper that mints on refusal (E2).
6. Measure C7, and the orphaned-claims count that decides E1.

## Decisions for Will

1. `403` or `401` for a presented-but-unusable token. Recommendation: `403` with an `invalid_token` body, behind one function and a pin test, with `401` kept for a missing token under a policy that requires one. The cost is a departure from a MUST. The benefit is the only refresh-and-retry path measured in the client SageFs is built for.
2. Accept B3, that the tool list depends on the presented credential only. It drops the conductor-by-connection shortcut for `TokenRequired`, and it is what the stateless revision requires.
3. If `operator.token` cannot be made owner-only, refuse the route with a message that says how to fix it. No escape hatch until someone asks.
4. Measure C7 before designing it.

## Not measured

How Cursor, Copilot and VS Code react to a `401` or `403` with this body; Claude Code in an interactive session instead of `-p`; whether the real daemon answers `server/discover`; the orchestrator-on-a-narrow-token option; what a restart orphans in practice; the Windows ACL.
