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
