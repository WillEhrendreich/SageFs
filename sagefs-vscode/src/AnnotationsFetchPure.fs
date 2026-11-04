/// What a window does with `annotations_available`.
///
/// On connect the daemon used to send one `file_annotations` frame and one `coverage_view` frame per
/// function for EVERY file of the session, because nothing said which files a window had open. It now lists
/// the files that have annotations, and the window asks for the ones it has open:
/// GET /api/live-testing/file-annotations?session=&file= answers a file's annotations with its
/// `CoverageViews`, each exactly the data of a `coverage_view` frame. Which listed file an open editor is, and
/// where to ask, are pure questions; the same rules as sagefs.nvim's annotations_fetch.lua.
///
/// No Fable dependency; tested under `dotnet fsi` (tests/AnnotationsFetchContractTests.fsx).
module SageFs.Vscode.AnnotationsFetchPure

open System

let private normalize (path: string) : string = path.Replace('\\', '/')

/// Is `short` the tail of `long` on a path boundary? (`/repo/A.fs` ends `A.fs`; `/repo/FooA.fs` does not.)
let private endsWithPath (long: string) (short: string) : bool =
  match long = short with
  | true -> true
  | false ->
    short.Length < long.Length
    && long.EndsWith(short, StringComparison.Ordinal)
    && (short.StartsWith "/" || long.[long.Length - short.Length - 1] = '/')

let private sameFile (a: string) (b: string) : bool = endsWithPath a b || endsWithPath b a

/// The daemon's spellings of the listed files that some open editor is, each once.
let filesToFetch (available: string list) (openPaths: string list) : string list =
  let open' = openPaths |> List.filter (fun p -> not (String.IsNullOrWhiteSpace p)) |> List.map normalize
  available
  |> List.filter (fun file ->
    not (String.IsNullOrWhiteSpace file)
    && (open' |> List.exists (sameFile (normalize file))))
  |> List.distinct

/// Where to ask for one file of one session.
let url (origin: string) (sessionId: string) (file: string) : string =
  sprintf "%s/api/live-testing/file-annotations?session=%s&file=%s"
    origin (Uri.EscapeDataString sessionId) (Uri.EscapeDataString file)
