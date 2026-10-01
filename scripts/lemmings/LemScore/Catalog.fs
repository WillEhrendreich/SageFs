/// Command Code's live model catalog (`cmdc --list-models`), and the one rule the
/// lemming harness holds to: a model runs only if the catalog itself marks it FREE.
module LemScore.Catalog

open System
open System.IO

type CatalogEntry =
  { Id: string
    Description: string }

/// The word the catalog puts at the start of a free model's description.
let freeMarker = "FREE"

/// Section headings and the "Available models" banner have no two-column shape.
/// A model line is `<id><spaces><description>` where the id has no spaces and
/// holds a slash or a dash (every id in the catalog does).
let parseLine (line: string) : CatalogEntry option =
  let trimmed = line.Trim()
  match trimmed.IndexOfAny [| ' '; '\t' |] with
  | -1 -> None
  | cut ->
    let id = trimmed.Substring(0, cut)
    let description = trimmed.Substring(cut).Trim()
    match id.Contains '/' || id.Contains '-' with
    | true -> Some { Id = id; Description = description }
    | false -> None

let parse (text: string) : CatalogEntry list =
  text.Split('\n')
  |> Array.toList
  |> List.choose parseLine

/// How long the live catalog may take to list.
let catalogTimeout = TimeSpan.FromSeconds 30.0

/// `cmdc --list-models`, as text. No colour, so the lines parse.
let liveText () : Result<string, string> =
  try
    let psi = System.Diagnostics.ProcessStartInfo("cmdc", "--list-models", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    psi.Environment["NO_COLOR"] <- "1"
    use p = System.Diagnostics.Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    match p.WaitForExit catalogTimeout with
    | true -> Ok (out.GetAwaiter().GetResult())
    | false ->
      p.Kill true
      Error "cmdc --list-models did not answer in time"
  with ex -> Error (sprintf "could not run cmdc --list-models: %s" ex.Message)

/// FREE has to be the first word of the description. A paid model whose blurb merely
/// says "everything else free" does not count.
let isMarkedFree (entry: CatalogEntry) : bool =
  entry.Description = freeMarker
  || entry.Description.StartsWith(freeMarker + " ", StringComparison.Ordinal)

/// Ok when the catalog lists the model and marks it FREE; otherwise the reason.
let checkFree (catalog: CatalogEntry list) (model: string) : Result<CatalogEntry, string> =
  match catalog with
  | [] -> Error "the live model catalog came back empty, so no model can be confirmed FREE"
  | _ ->
    match catalog |> List.tryFind (fun e -> e.Id = model) with
    | None ->
      let free = catalog |> List.filter isMarkedFree |> List.map _.Id |> String.concat ", "
      Error (sprintf "'%s' is not in the live catalog. FREE models right now: %s" model free)
    | Some entry when isMarkedFree entry -> Ok entry
    | Some entry ->
      Error (sprintf "'%s' is not marked FREE in the live catalog (it says: %s). Lemmings run on free models only." model entry.Description)

let freeModels (catalog: CatalogEntry list) : string list =
  catalog |> List.filter isMarkedFree |> List.map _.Id

/// Suffixes that say "free" or "preview" and add nothing to a directory name.
let private shortNameSuffixes = [ ":free"; "-free"; "-alpha" ]

/// `stealth/space-bunny-alpha` becomes `space-bunny`, the model part of the run directory
/// name, so a lemming can be told apart in the dashboard by its working directory.
let shortName (model: string) : string =
  let afterSlash = model.Substring(model.LastIndexOf '/' + 1)
  let stripped =
    shortNameSuffixes
    |> List.fold (fun (name: string) suffix -> if name.EndsWith(suffix, StringComparison.Ordinal) then name.Substring(0, name.Length - suffix.Length) else name) afterSlash
  stripped.ToLowerInvariant()
  |> String.map (fun c -> if Char.IsLetterOrDigit c || c = '.' || c = '-' then c else '-')

/// Width of the repetition number in a run id: space-bunny-parse-seed-01.
let runNumberWidth = 2

let private takenIds (root: string) : Set<string> =
  match Directory.Exists root with
  | true -> Directory.GetDirectories root |> Array.map Path.GetFileName |> Set.ofArray
  | false -> Set.empty

/// `count` unused ids `<model-short>-<task>-<nn>`, numbered upward from the first free one and
/// never colliding with a directory under `root` or with `alsoTaken` (ids a caller has already
/// handed out in this plan but not yet created on disk).
let runIdsAvoiding (root: string) (alsoTaken: Set<string>) (model: string) (task: string) (count: int) : string list =
  let prefix = sprintf "%s-%s-" (shortName model) task
  let taken = Set.union (takenIds root) alsoTaken
  let rec pick n acc =
    match List.length acc = count with
    | true -> List.rev acc
    | false ->
      let id = prefix + n.ToString().PadLeft(runNumberWidth, '0')
      match taken.Contains id with
      | true -> pick (n + 1) acc
      | false -> pick (n + 1) (id :: acc)
  pick 1 []

/// The next unused `<model-short>-<task>-<nn>` under `root`.
let nextRunId (root: string) (model: string) (task: string) : string =
  runIdsAvoiding root Set.empty model task 1 |> List.head
