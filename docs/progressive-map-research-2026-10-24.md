# Progressive map for SageFs: research, 2026-10-24

The ask, from a user reviewing agent sessions: "I think what would work best is to not offer the AI a
complete map but let it progressively get it. Projects -> Namespaces -> Modules -> Types and
Functions. And then for a given type/function, references." Research question: what would this mean
inside SageFs, where does it apply best, and what prior art (compiler design, data-oriented design,
memoized caching) already points the way.

## 1. What already exists (the ladder is half-built)

| Rung | Existing surface | Evidence |
|:--|:--|:--|
| Projects | Project classification with roles, per solution snapshot | `SageFs.Core/ProjectLoading.fs:104` (`ProjectRole`), `:1117` `classifyProject`, `:1142` `classifyProjects` |
| Namespaces / Modules / Types, lazily expanded | The VS Code Type Explorer tree: icons per kind, `drillableKinds = {Namespace; Module; Class; Struct; Interface; Enum; Union; Type}` as expandable nodes, members as leaves, `getChildren` fetching per node | `sagefs-vscode/src/TypeExplorerProvider.fs:36` (`drillableKinds`), `:91-94` (`exploreAndParse` calls `Client.exploreCompletions`), `:122` (`getChildren`) |
| The daemon-side data for that tree | `exploreCompletions` on the client, backed by the daemon's completion/explore path (the worker already answers `GetCompletions`) | `sagefs-vscode/src/TypeExplorerProvider.fs:94`; `SageFs.Core/HttpWorkerClient.fs:34` (`WorkerMessage.GetCompletions`) |
| Types and functions, ranked rather than dumped | `discover_features` returns a ranked list plus context and counts, never the catalogue | `SageFs.Core/Features/FeatureDiscovery.fs:46` (`DiscoveryReport`: `Suggestions`, `ContextSummary`, `TotalKnownFeatures`), `:68` (`Boosted of relevance * whyNow`) |
| References (usages) | Real FCS symbol-use resolution already exists, but private to hot reload: symbol uses, declaration lines, hidden-use computation | `SageFs.Core/Features/ReloadPlanning.fs:1222` (`symbolUsesOfAsync`), `:1258-1260` |
| Symbol or coverage line -> location | Coverage annotations keyed by file plus definition line | `SageFs.Core/Features/LiveTestingTypes.fs:537` (`DefinitionLine`), `:3183` (lookup by file and line) |
| One symbol's next fact on demand | `nudge_value` resolves a value's address to edit it in place | `SageFs/McpNudge.fs` (route named in `scripts/roadmap/RoadmapItems.fs:112` landmark) |
| Test names, filtered not listed | `list_tests` with `pattern` and `file_path` | tool contract in `SageFs/McpTools.fs` |

The editor has therefore been building this ladder for humans for a while; agents get pieces of it
(`discover_features`, `list_tests`, `get_available_projects`) but no single walking tool.

## 2. What the compilers do (the analogues that matter)

1. **Resolve, do not enumerate.** Name resolution in FCS and Roslyn walks a scope chain; nobody
   materialises the whole symbol table to answer "what is this". The agent equivalent: a query by
   name should resolve through the rungs internally, so "signature and references for `Foo.bar`" is
   one call, while browsing remains available for orientation.
2. **Cache keyed by content or snapshot, invalidate by difference.** Incremental builds keep
   per-unit state and re-do only the dirty unit. SageFs already has this at file level: the parse
   cache is keyed by path plus file text, so an edit invalidates itself by construction
   (`SageFs.Core/Middleware/CompilationContext.fs:58` `FileCache: Map<string, string * FileStructure>`,
   `:180` `parseFileStructureCached`).
3. **Intern names, index symbols.** Compilers store names once and refer to them by handle. The
   agent-facing map should be a flat table of `{parent; kind; name; detail}` per build snapshot with
   integer handles, not a tree of objects that has to be rebuilt and re-serialised per request.
4. **Wire what changed, not what exists.** SageFs's SSE dedup already is this: one key built from a
   monotonic version plus cached counters, appended by hand, no reflection and no per-entry
   formatting (`SageFs/SageFsSseDedup.fs:40-50`, and it reads `RecentOutput.Version` first).

## 3. Data-oriented shape for the map itself

- **Storage:** one flat array per session-solution snapshot. Rows: interned name id, kind (a small
  closed set matching `drillableKinds`), parent handle, and a detail id (signature or summary).
  Siblings of a node are an index range; a "children" call returns one slice of the array, so a
  rung is a view, not a query plan.
- **Chunking:** children fetched as `parent + offset + limit`, each chunk capped at the house byte
  budget, with the sibling count in the reply so the caller can page (the same shape as
  `get_recent_fsi_events` count and `RecentOutput`'s ring capacity of 500 at `TuiEvent.fs:176`).
- **Invalidation:** key the whole index by the build/snapshot version. Hot reload already knows
  which declarations moved (`ReloadPlanning` computes declaration lines for exactly this reason), so
  an edit can dirty the subtree instead of the table.
- **Answers derived, not stored:** a type's "references" are computed by FCS on demand (rung 5) and
  memoised per (symbol, snapshot), because storing them means invalidating them on every keystroke.

## 4. Memoization: what exists, what is worth adding

Have (all measured or in-tree):
- content-keyed parse memo (`CompilationContext.FileCache`);
- id-indexed, byte-budgeted eval history with per-event summaries of about 310 characters and cell
  recall (`SageFs.Core/Features/EvalStore.fs:1`; measured 2026-10-24, see section 7);
- version-keyed SSE dedup (`SageFsSseDedup.fs:40`);
- content hashes for rendered graphs (`SageFs/DocsRender.fs` hash field; and the cohort list content
  hashes decision in `docs/decisions.md`);
- bounded replies with an explicit notice (`SageFs.Core/Features/EvalResultSummary.fs`, capped at
  4,000 characters, pinned by `SageFs.Tests/EvalResultSummaryTests.fs`).

Worth adding (the map's own caches):
- the flat name index per snapshot (build it once per solution/build event, not per request);
- memoised references per (symbol handle, snapshot) with the snapshot in the key, so a patch
  invalidates the memo the same way `FileCache` does;
- a repeat-aware transport for the map itself: if a client asks for the same chunk it already holds,
  send the chunk id and count (the `SageFsSseDedup` idea applied to tool replies).

## 5. Where it applies best (ranked)

1. **An MCP tool family over the existing type-explorer data.** The tree, the drillable set and the
   `exploreCompletions` data source already exist for the editor; agents should get the same ladder
   as tools: list children of a handle (or by name), describe one symbol, then references. Smallest
   delta in the system, one shared implementation for human and agent, and it retires the
   "GetDynamicTools namespace dump" pattern on the harness side by giving agents a bounded door.
2. **Rung 5, references, as its own capped tool.** The engine is `ReloadPlanning.symbolUsesOfAsync`
   (FCS, real usages, declaration lines). Expose it per symbol with a top-N result and a note that
   says how to get the rest. This is the only rung whose engine is not yet reachable from a tool.
3. **`discover_features` stays the orientation rung** (it already ranks by session state and reports
   `TotalKnownFeatures` rather than the catalogue) and should gain links: each suggestion can name
   the handle its feature lives in, so orientation flows into walking.
4. **`list_tests`-style filters anywhere a set is large:** already the house pattern; new list-shaped
   tools should copy it rather than invent paging.
5. **Not the dashboard panels or the eval surfaces.** Those were measured bounded this week
   (4,000-character reply cap with notice, 310-character history lines). Nothing to change there.

## 6. Contracts to write down before building

- Per-rung byte cap in one named home, and a per-tool result-size ratchet in the default tier (the
  same enforcement class as the endpoint registry and timeout ratchets, which caught real
  violations in this very release cycle).
- Skippable levels: every rung accepts a name query, not only a parent handle, so a caller never
  walks five levels to reach one function.
- Sibling context in every children reply (counts and next-page handle) so navigation never has to
  climb up to re-orient.
- Auto-compact: if the whole set fits the cap, return it whole; paginate only past it.
- Every reply carries the snapshot/version it came from, so a client can tell a stale walk from a
  fresh one (the house rule about freshness receipts).

## 7. Measured context (2026-10-24, sizes through the daemon's own MCP endpoint)

A 50,000-character eval returned a 4,290-character result item (4,000 cap plus notice) and a
217-character tail; `get_recent_fsi_events` returned a 310-character line carrying cell id `#0`;
journal and filmstrip replies were 227 and 182 bytes. The reported 125KB namespace dump had zero
matches in this repository and is harness-side. Conclusion: the reply surfaces are already
progressive; the gap is navigation over the symbol map, which is what this document scopes.

## 8. Open questions

- Whether `exploreCompletions`'s server entry point is a tool or a route (confirm at implementation
  time; the client symbol is `SageFsClient.exploreCompletions`).
- Whether the flat index should be built in the worker (where FCS contexts already live) and served
  to the daemon, or in the daemon from worker replies; the worker keeps the FCS cost off the daemon
  loop either way.
- The byte caps themselves (they belong in the named tunables home, measured against the chunk sizes
  the type explorer already requests).

## 9. Revision after the roast and the probes (2026-10-24, supersedes §4-§6 where they differ)

Probed and read before any code. This section is the plan of record; the sections above stay as
evidence.

**Overturned: "the references engine exists".** `ReloadPlanning.symbolUsesOfAsync` takes a source
string, builds a synthetic `reload-planning-check-N.fs`, and resolves through
`GetProjectOptionsFromScript`, under `Timeouts.reloadPlanningCheck` with an explicit fallback to
identifier-name reading. That is file-scoped approximation for patch planning. Project-wide uses are
NEW worker work: a protocol message, a project context, and a reply that states its scope
("file-scoped (n)" versus "project-wide (n)"), because an agent that hears a bare count will trust
it. Rung 5 is a feature, not an exposure.

**Overturned: integer handles as the public key.** The engine that exists is name-keyed:
`exploreQualifiedName` (SageFs/Mcp.fs:2184) sends `<qualifiedName>.` to the worker as
`GetCompletions` and prints members; the extension posts the same shape to `/api/completions` and
parses `{ completions: [{label, kind, insertText, detail?}], count}` (SageFsClient.fs:786-794).
`POST /api/explore` (McpServer.fs:4215) is the same engine through `exploreNamespace`. Decision:
qualified NAME paths are the public key (they survive edits, which integer handles do not), and any
index built later is an internal memo keyed by snapshot, never the reply's identity.

**The history nobody put in the plan.** `explore_namespace`, `explore_type` and `get_completions`
were NEVER registered tools: `discover_features` advertised six names `tools/list` never had, and
the fix sealed the surface (docs/decisions.md:1351). The names sit in
`SageFs.Core/Affordances.fs` `RetiredTool`; `RetiredToolNameTests` scans docs for them;
`ToolSurfaceHonestyTests` fails if discovery, affordances or `docs/mcp-tools.md` disagree with the
registered set or name a retired tool. Consequences: (a) slice 1 must use a FRESH registered name,
not resurrect a retired one; (b) the enforcement this plan needs already exists and is inherited:
docs cannot drift from the registry without a red default-tier test, which is the exact
"correct by construction" answer for the tool surface; (c) a prior audit already flagged Readme
capability rows claiming MCP completion/type-explorer that are not MCP tools
(outcome-gate-sweep.md, row 9a); registering honestly is what closes that row.

**Probe results.** `POST /api/explore {"name": ...}` answers HTTP 200 with the house refusal when no
session is bound ("No active session. Use get_available_projects, then ..."), i.e. the route is
session-routed exactly like the tools. The engine's current output is unbounded plain text (one line
per completion, no count, no paging) (`SageFs/Mcp.fs:2193-2201`), so the bound is genuinely missing
and is the product this plan adds.

### How this stays true (structural enforcement)

1. One builder constructs every ladder reply (caps, counts, snapshot, name path attached by the
   builder; raw lists cannot be sent; module-private constructors), the `EvalResultSummary`
   doctrine pushed into types.
2. One shared `LadderContract` F# file compiled into .NET and into the Fable extension, so kinds and
   caps have one source for both sides.
3. Registration is the gate: a tool joins `SageFsTools` plus its `docs/mcp-tools.md` row in the same
   change or `ToolSurfaceHonestyTests` goes red; `RetiredToolNameTests` forbids the six old names.
   The rest of a tool's declarations (gate domain, name, routing kind, class) are listed in order in
   `docs/mcp-tools.md` under "Adding a tool", so each slice registers once.
4. Every numeric budget lives in `TestTimeouts`/the budget tables and only ratchets down; a
   per-tool result-size row per new tool.
5. Each slice ships its exit test in the default tier as a plain `[<Tests>]` case, verified through
   a SageFs session receipt (TRUST `ran=registered`), never a filter-only gate.
6. The plan itself lives under a "reopen it if" clause in `docs/decisions.md` once adopted, and its
   size claims carry committed probe scripts, so the document cannot outlive its evidence.

### Slices (one surface, one exit test each)

| Slice | Deliverable | Exit test (default tier) |
|:--|:--|:--|
| 1 | `list_members` (fresh name): bounded member listing of a qualified name, count and paging footer, session-routed through the existing engine | registry contains the tool and its docs row; a 500-member fixture reply is capped and carries the count; no retired name appears in its description |
| 2 | Kind grouping and sibling counts matching `drillableKinds`; skill teaches the ladder | reply shape test; skill-mentions-tool guard |
| 3 | References tool: new worker message, project-scoped, scope stated in the reply | scope line asserted both ways; failure states the budget used |
| 4 | Name-path resolution fallback for stale references | stale name resolves, test pins the fallback |
| 5 | Result-size ratchet rows for the family; wire the Readme capability row (closes outcome-gate-sweep 9a) | ratchet table contains the rows; capability table matches the registry |

Status, 2026-10-25: slices 1 and 2 are built. Slice 2 first shipped with two groups (drillable and leaf)
because the worker sent display names only and the daemon guessed a kind from the shape of the name, so
`Cons` and `Empty` under `List` counted as drillable. The worker now carries the compiler's own kind and a
true total (`CompletionTotal`, exact or at least N), so the reply counts classes, modules, methods and so
on. Two limits remain. The completions request still passes an empty word, so a partial identifier gets the
50 shortest names in scope. And a total is only marked "at least" past the wire ceiling of 2,000 members,
which none of the sets measured reached.

Reopen it if: the `GetCompletions` route cannot express true children (fuzzy prefix instead of
members), in which case slice 1 shrinks to `list_members` on namespaces only and the index question
returns to this document with a measured symbol census.

