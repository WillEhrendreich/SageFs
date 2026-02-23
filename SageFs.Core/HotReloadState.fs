module SageFs.HotReloadState

/// Per-session state tracking which files are opted-in for hot-reload.
/// Default: no files watched. Users explicitly opt files in.
type T = {
  Watched: Set<string>
}

let private normalize (path: string) =
  path.Replace('\\', '/').ToLowerInvariant()

let empty : T = { Watched = Set.empty }

let watch (path: string) (state: T) : T =
  { state with Watched = state.Watched.Add(normalize path) }

let unwatch (path: string) (state: T) : T =
  { state with Watched = state.Watched.Remove(normalize path) }

let isWatched (path: string) (state: T) : bool =
  state.Watched.Contains(normalize path)

let watchMany (paths: string seq) (state: T) : T =
  { state with Watched = Set.union state.Watched (paths |> Seq.map normalize |> Set.ofSeq) }

let unwatchAll (_state: T) : T = empty

let watchAll (paths: string seq) (_state: T) : T =
  { Watched = paths |> Seq.map normalize |> Set.ofSeq }

let toggle (path: string) (state: T) : T =
  let p = normalize path
  if state.Watched.Contains(p) then
    { state with Watched = state.Watched.Remove(p) }
  else
    { state with Watched = state.Watched.Add(p) }

let watchedCount (state: T) : int =
  state.Watched.Count

let private normalizeDir (dir: string) =
  (dir.Replace('\\', '/').ToLowerInvariant()).TrimEnd('/')

let private dirOf (path: string) =
  System.IO.Path.GetDirectoryName(path).Replace('\\', '/')

let watchByDirectory (dir: string) (allPaths: string seq) (state: T) : T =
  let nd = normalizeDir dir
  let matching =
    allPaths
    |> Seq.map normalize
    |> Seq.filter (fun p ->
      let d = dirOf p
      d = nd || d.StartsWith(nd + "/", System.StringComparison.Ordinal))
  watchMany matching state

let unwatchByDirectory (dir: string) (state: T) : T =
  let nd = normalizeDir dir
  let remaining =
    state.Watched
    |> Set.filter (fun p ->
      let d = dirOf p
      not (d = nd || d.StartsWith(nd + "/", System.StringComparison.Ordinal)))
  { state with Watched = remaining }

let watchedInDirectory (dir: string) (state: T) : string list =
  let nd = normalizeDir dir
  state.Watched
  |> Set.filter (fun p ->
    let d = dirOf p
    d = nd || d.StartsWith(nd + "/", System.StringComparison.Ordinal))
  |> Set.toList

let watchByProject (projectPaths: string seq) (state: T) : T =
  watchMany projectPaths state

let unwatchByProject (projectPaths: string seq) (state: T) : T =
  let normalized = projectPaths |> Seq.map normalize |> Set.ofSeq
  { state with Watched = Set.difference state.Watched normalized }
