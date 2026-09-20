/// Pure decisions behind "which project does this workspace use?".
///
/// The bug this module exists to kill: `sagefs.projectPath` was written with
/// ConfigurationTarget.Global, so choosing a project once in ANY workspace
/// pinned it for EVERY F# workspace on the machine, and the pick was usually a
/// workspace-RELATIVE path — which, resolved against a different root, points
/// at something unrelated. Symptom, reported by a user: "it attempted to scan
/// some irrelevant projects in other dirs not sure why".
///
/// No Fable dependency; tested under `dotnet fsi`
/// (tests/ProjectPickContractTests.fsx).
module SageFs.Vscode.ProjectPickPure

/// What to do with a persisted `sagefs.projectPath`.
type ConfiguredChoice =
  /// Nothing persisted — discover normally.
  | NotConfigured
  /// Persisted and it belongs to this workspace.
  | UseConfigured of path: string
  /// Persisted but it does not belong to this workspace: ignore it and say why.
  | IgnoreStale of path: string * reason: string

let private normalize (p: string) = p.Replace('\\', '/').TrimEnd('/')

let private isAbsolute (p: string) =
  p.StartsWith "/" || (p.Length > 1 && p.[1] = ':')

/// Is an absolute path inside one of the workspace roots?
let isWithinWorkspace (folders: string array) (path: string) : bool =
  let path = normalize path
  folders
  |> Array.exists (fun f ->
    let f = normalize f
    f <> "" && (path = f || path.StartsWith(f + "/")))

/// Decide whether a persisted project path may be used for THIS workspace.
/// `candidates` are the workspace-relative project paths just discovered.
let chooseConfigured (folders: string array) (candidates: string array) (configured: string) : ConfiguredChoice =
  match configured with
  | null -> NotConfigured
  | c when System.String.IsNullOrWhiteSpace c -> NotConfigured
  | c when isAbsolute c ->
    match isWithinWorkspace folders c with
    | true -> UseConfigured c
    | false ->
      IgnoreStale(
        c,
        "it is outside this workspace (it was saved by another folder — SageFs used to store this setting globally)")
  | c ->
    let target = normalize c
    match candidates |> Array.exists (fun k -> normalize k = target) with
    | true -> UseConfigured c
    | false ->
      IgnoreStale(
        c,
        "no such project exists in this workspace (it was saved by another folder — SageFs used to store this setting globally)")

/// Rank for ordering the quick pick: solutions first (they load everything),
/// then shallower paths (a root project is more likely the one you want),
/// then alphabetical. The pick used to be in raw filesystem order.
let private rank (path: string) =
  let p = normalize path
  let isSolution = p.EndsWith ".sln" || p.EndsWith ".slnx"
  let depth = p.Split('/').Length
  ((if isSolution then 0 else 1), depth, p.ToLowerInvariant())

let sortCandidates (candidates: string array) : string array =
  candidates |> Array.sortBy rank

/// The quick-pick rows: sorted candidates, plus an explicit escape hatch so a
/// project the scan capped out of the list is still reachable.
[<Literal>]
let BrowseRow = "$(folder-opened) Browse for a project or solution…"

let pickRows (candidates: string array) : string array =
  Array.append (sortCandidates candidates) [| BrowseRow |]

/// Whether the scan hit its cap, i.e. whether the list the user is looking at
/// is known to be incomplete. Silent truncation is what made a 30-project repo
/// show 10 projects with no hint that 20 were missing.
let isTruncated (limit: int) (found: int) : bool = found >= limit
