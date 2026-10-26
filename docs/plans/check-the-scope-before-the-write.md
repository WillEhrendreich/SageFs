# Check the scope before the write, not after

Roadmap item: `check-the-scope-before-the-write` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M, L with four hosts |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a write onto another member's claim is refused naming the holder. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

When an agent in a supported host uses its own file tool (Write, Edit, apply_patch and the like) on a path, the write is checked before it happens against four things: the checkout the agent works in, the scope prefix of its member token, the claims other members hold, and a short list of extra roots the repo allows (a scratch directory). A write outside the checkout, outside the token's scope, or onto a file another member has claimed is refused with the reason, the holder where there is one, and what to do (`acquire_claim`, ask the conductor for a wider scope, or write elsewhere). A write onto an unclaimed file inside scope is allowed, or allowed with a reminder to claim it, by a mode setting. The boundary covers the host's file tools. It does not cover a shell command that writes, and the docs say so in that sentence, because the sandbox is the boundary for shell. The outcome gate drives each installed host for real over the matrix: write target (inside scope and claimed by me, inside scope and unclaimed, claimed by another, outside scope, outside the checkout, through a symlink out of the checkout, a new file in a new directory) by identity (token present, token absent) by mode (enforce, advise), and asserts the file on disk afterwards is or is not changed and that the refusal text reached the agent.

## What exists today

- A claim is policy. `docs/mcp-tools.md` ("Scope"): "This is policy, not a sandbox: it refuses claims, and it does not stop a process from writing a file." `Capability.fs:33` says the same. The roadmap summary quotes it.
- The pure pieces for a path check exist:
  - `Cohort.ClaimPath.tryCanonical` (`SageFs.Core/Cohort.fs:~118`): one canonical spelling of a repo-relative path, `..` resolved lexically, a path that climbs out is `PathRefusal.EscapesRoot`, a rooted path is `PathRefusal.Absolute`.
  - `Cohort.ClaimScope` (`File of path | Project of fsprojPath`) with `ClaimScope.overlaps` (path arithmetic: a `File` overlaps a `Project` iff the file is under the project's directory).
  - `Capability.ScopePrefix` with `covers`, `isWithin`, and `Capability.admitClaim : Grant -> ClaimScope -> Result<unit, ScopeRefusal>` (`:875`).
  - `PathContainment.isWithinCanonical` (`SageFs.Core/PathContainment.fs`), case sensitivity chosen per platform (`PathCase`).
  - `CheckoutRoot` and `RouteBinding` (`Capability.fs:281`, `:313`): where a token may act.
- Claims live in the cohort state: `CohortState.Claims: Map<ClaimId, Claim<'m>>`, read wait-free through `CohortOwner.Handle.ReadFrame()` (`SageFs.Core/Features/CohortOwner.fs:370`). Each owner is per repository (`McpTools.cohortOwnerFor ctx workingDirectory`).
- The only check that happens after a write is not built either. `LandingBlocker` in `Cohort.fs:~357` has a comment that `OutsideClaims` and `ClaimOverlap` "need a git diff's file list" and were left out until a command can construct them. I could not find a landing-time check for a file written outside its author's claims. So "after" today means nothing: a stray write is found by a human.
- Symlinks are a known hazard in this repo: `SafeDirectoryWalk.walkFiles` exists because a symlink cycle ate the daemon's memory (decisions and `RatchetBudgets` comment). A lexical check on `src/link/x.fs` cannot see that `link` points outside the repo.
- Host facts (docs read 2026-10-26; summaries, confirm by running):
  - Claude Code: `PreToolUse` for file tools carries `tool_input.file_path` (the `Edit`/`Write` rules in `permissions` use the same tool names, and "Read and Edit deny rules apply to Claude's built-in file tools, to file commands Claude Code recognizes in Bash, such as `cat`, `head`, `tail`, `sed`, and `tee`, and to the targets of Bash redirections"). Deny and ask rules take path patterns: `Edit(//tmp/scratch.txt)`, `Edit(src/**)`. A deny rule cannot carve an exception out of an allow, and there is no negation, so "everything except `src/Foo/`" cannot be written as static rules. The docs point to sandboxing for enforcement independent of command text.
  - Codex: `PreToolUse` supports `apply_patch` and matchers `Edit|Write`; deny works, ask does not.
  - Cursor: no dedicated before-write hook is documented (`afterFileEdit` is observational, `beforeReadFile` is read-only); `preToolUse` with a `Write` matcher is the closest, inferred, not stated.
  - OpenCode: `tool.execute.before` shows `output.args.filePath` for the read tool in its own example; the write tool's argument names were not read.
  - Copilot CLI: `preToolUse` with `toolName`/`toolArgs`; the edit tool's argument names were not read.
- The hook project and adapter layer (`tools/agent-hooks/`, `HostAdapter` from `repl-guard-for-other-agents`) is where the shells live. Latency matters: a hook runs on every edit.

## Design

One pure decision in `SageFs.Core`, a daemon route that answers it, and thin per-host shells, the same shape as `confirmation-gates-in-every-agent-host`.

```fsharp
// SageFs.Core/WriteScope.fs  (pure, no IO)
[<RequireQualifiedAccess>]
type WriteVerdict =
  | Allowed
  | AllowedUnclaimed of path: string                 // inside scope, nobody holds it
  | OutsideCheckout of path: string * checkout: string
  | OutsideScope of path: string * prefix: ScopePrefix
  | ClaimedByAnother of path: string * holder: MemberId * claim: ClaimId
  | PathUnresolvable of path: string * why: PathRefusal   // escapes root, rooted in a bad way
  | IdentityUnknown of boundaryOnly: WriteVerdict         // no token: only the boundary was checked
```

`WriteScope.decide : WriteContext -> string -> WriteVerdict`, where `WriteContext` carries the checkout root, the extra allowed roots, the caller's `Grant` option as a closed type (`Bound of Grant | NoToken`), the caller's `MemberId`, and the claims from a frame. It reuses `ClaimPath.tryCanonical`, `ScopePrefix.covers`, `ClaimScope.overlaps` and `PathContainment`; it adds no new path arithmetic.

Symlinks and new files live at the edge, not in the pure function. The shell (or the route, for a path it was handed) resolves the real path of the nearest existing ancestor with the OS (`realpath` semantics: `Directory.ResolveLinkTarget(path, true)` on each existing segment), appends the remaining segments, and only then passes the repo-relative result to `decide`. A new file in a new directory resolves through the first ancestor that exists. A path whose resolution fails is `PathUnresolvable`, which refuses. Resolution is a function parameter of the edge so a test injects a fake filesystem with links.

Route: `POST /api/scope/check { cwd, path, token? }` on the MCP port. It resolves the cohort by `cwd` the way `McpTools.cohortOwnerFor` does, reads the frame, finds the token's grant through `CapabilityStore.Present`, and replies with the verdict as JSON and the sentence an agent reads (what failed, why, what to do). A new HTTP route in `EndpointContracts.fs`; `apiVersion` does not move for an added route. Once `a-token-for-every-daemon-call` ships the route requires the operator credential, because it reveals who holds which file.

Identity is the weak point and the plan says so. The host's hook sees a file path. It does not see which MCP connection the agent is. With a token in the hook's environment (`SAGEFS_MEMBER_TOKEN`, as the stdio bridge already reads) the daemon knows the member; without it the answer is `IdentityUnknown` with only the checkout boundary and a note naming the claim holder if there is one. A sub-agent that has its own token through `a-token-per-subagent-in-claude-code` has that token in its own MCP server config, not in the hook's environment, and the hook input carries `agent_id` and `agent_type`; mapping `agent_id` to a token needs a small launcher-written table (`<data dir>/agent-tokens/<agent_id>` readable only by the user). That is item 4's phase 2 and this item's identity-aware cells depend on it. Until then the guaranteed part is the boundary.

Modes, a closed DU like `GuardMode`: `Enforcing` (refuse), `Advising` (allow with the reminder as context). Default `Advising` for the unclaimed-in-scope case and `Enforcing` for outside-checkout and claimed-by-another; one setting `SAGEFS_WRITE_GUARD` and a repo file `.SageFs/write-scope.json` (extra roots, mode), parsed by a pure function with a typed problem list as `ReplGuard.parseConfig` does.

For Claude Code only, a second, static layer: `sagefs hooks install --host claude --write-scope` also prints `permissions.deny` entries for the paths that are fixed and known up front (the data dir, `~/.ssh`, `.git/hooks/**` of the checkout), because a static deny covers Bash redirections and `sed -i` on those paths as well, which the hook route cannot. It does not try to express a token scope.

What this does not stop, in the words the docs get: a shell command that writes, a subprocess a tool starts, `send_fsharp_code` (which runs F# as the user), and any host with no hook. The roadmap's own phrase "the difference between a claim and a boundary" is true only for the host's own file tools.

Wire and client impact: one HTTP route and the hook shells. `sagefs.nvim` and `sagefs-vscode` unchanged (`Plugin: n/a, a route for agent hooks`). Persistence: none. No MCP tool, no catalog cost. The `acquire_claim` tool description gains a sentence saying a claim is now checked at write time in supported hosts.

## Tests

RED first.

1. `SageFs.Tests/WriteScopeTests.fs` (new): `WHY - a write onto a file another member claimed is refused and names the holder`. Builds a `CohortFrame` with one claim by member B, calls `decide` as member A. Fails to compile today.
2. `WHY - a write outside the checkout is refused with or without a token`.
3. Property (FsCheck): for any generated repo-relative path, scope prefix and set of claims, `decide` returns `Allowed` only if the canonical path is within the checkout, within the prefix, and overlaps no claim held by another member. Draw paths with `..`, `.`, empty segments, mixed separators and case variants, and use `ClaimScope.overlaps` as an independent oracle in the property.
4. `WHY - a symlink inside the checkout that points outside it is OutsideCheckout`: the edge resolver with a fake filesystem (link `src/out -> /etc`) and a real-filesystem variant on Linux in a temp directory.
5. `WHY - a new file under a new directory resolves through its first existing ancestor`.
6. Fail-closed: a resolver that throws gives `PathUnresolvable`, never `Allowed`; the daemon not answering gives the shell's `Advising` or `Enforcing` reply for the boundary cells only (a local path check against the checkout root the shell can do itself without the daemon), and never allows an outside-checkout path.
7. Adapter fixtures per host: recorded file-tool inputs (from the capture step in `repl-guard-for-other-agents`) parse to the target path set. Codex's `apply_patch` carries several paths in one patch text; the adapter returns a list and the verdict is the worst of them. A property: for any patch with N paths, one refused path refuses the call and names that path.
8. Simulation `SageFs.Simulation/WriteScopeSim.fs`, in the style of `CohortSpec.fs`: seeded ops `Claim`, `Release`, `Reassign`, `Depart`, `Write`, `Rename`, `Restart` by three members over a small tree, folded through the real cohort `decide` and `WriteScope.decide`. Invariants: a write is never allowed onto a file held by another present member; a departed member's orphaned claim (`reassign_claim` pending) is reported as such, not as free; a write is never allowed after the claim holder's fence moved on without the writer re-claiming. Twins: `LexicalOnlyTwin` (skips resolution, so the symlink case passes), `IgnoresOrphanedTwin` (treats an orphaned claim as free), `PrefixStartsWithTwin` (the `StartsWith` check the claim-path work removed), `TrustsClaimerNameTwin` (uses the `agentName` the call passes instead of the bound identity). Each must trip an invariant.
9. Outcome gate, local `--integration-hosts` tier (shared with the other two host items): the matrix above, per installed host, with the file's bytes and mtime read after the run. A cell that cannot be exercised on a host (it has no file-tool hook) is reported as `NotEnforceable` in a printed table and fails the tier unless the host is on an explicit documented-uncovered list in the repo, so uncoverage is a decision made in a file and not a skip.

No wall-clock waits.

## Steps

1. Test 1 to 3 and `WriteScope.fs` (pure). Owns `SageFs.Core/WriteScope.fs`, its test file, `SageFs.Core.fsproj`.
2. Tests 4 to 6 and the edge resolver (`SageFs/WriteScopeEdge.fs`) with an injected filesystem. Parallel with 1 after the types exist.
3. The simulation (test 8). Owns `SageFs.Simulation/WriteScopeSim.fs`. Parallel with 2.
4. The route and `EndpointContracts.fs` entry; the daemon-side wiring that reads the frame and grant. Owns the route region in `McpServer.fs`.
5. The Claude Code shell via the `HostAdapter` layer, then Codex (`apply_patch` list case), then the others as the capture step allows. File-disjoint per host.
6. `.SageFs/write-scope.json`, the mode setting, install flags, docs (`docs/agents.md`, `docs/mcp-tools.md` Scope section: the "policy, not a sandbox" sentence gets its new exact limit).
7. Test 9.

## Measure first

- Does a PreToolUse hook run for every file tool in each host, including MultiEdit and NotebookEdit, and for sub-agents (the Claude docs say hooks run inside sub-agents, with `agent_id`)? The capture step records it.
- Hook cost on a burst of edits (an agent can write 50 files in a minute). One local HTTP call per edit should be about a millisecond; confirm, and if not, have the shell cache the frame for a few hundred milliseconds keyed by the frame's own version.
- How often an agent legitimately writes outside the checkout (a scratch dir, `/tmp`, a sibling worktree). Count in a day of real sessions from the host transcripts. The extra-roots list has to cover them or the guard trains people to turn it off.
- How many writes today would have been refused (replay a week of transcripts' file tool calls through `decide`). That number is the real argument for or against the feature; it can be measured with no product code once the pure function exists.

## Risks and dependencies

- False refusals are costly: a guard that blocks legitimate writes gets disabled. `Advising` for the unclaimed case and a replay measurement before enforcement are the mitigation.
- The identity gap above. Without per-sub-agent tokens most multi-agent runs get only the boundary and the claim-holder note.
- Hosts that fail open on a hook timeout (Copilot's command hooks do) make this advisory there.
- Depends on `repl-guard-for-other-agents` (adapters, capture), `a-token-per-subagent-in-claude-code` and `a-token-for-every-daemon-call` (identity and route authentication). Feeds `claim-an-interface` (a `.fsi` claim is checked by the same path function) and `drive-real-agents-as-members`, whose driver could answer the host's permission request with this verdict.

## Size

M for the pure decision, resolver, route and Claude shell. L with four hosts and the simulation.

## Decisions for Will

- Ship the boundary (outside the checkout) first and the claim check later? Recommendation: yes. The boundary needs no identity, and the replay number tells you whether the claim check is worth turning on.
- Default mode for a write onto an unclaimed in-scope file: advise or refuse? Recommendation: advise, with a one-line reminder to `acquire_claim`.
- Static Claude `permissions.deny` for the data dir and `.git/hooks`: include in the installer? Recommendation: yes, opt-in flag, because it also covers shell redirections.
