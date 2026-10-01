/// The roadmap page (docs/roadmap.md) is generated from a list of items. This file is the model and
/// the renderer, and both are pure so the test and the generator script run the same code.
///
/// An item is never marked done by hand. It may name a landmark: a tracked file and a symbol that
/// will be in that file once the work exists. When the landmark is in the tree the item is Built and
/// moves to "Built" on its own. Until then it stays at the horizon it was filed under. A typo in a
/// landmark would leave an item open forever, so the test requires the landmark's folder to exist.
module SageFs.Roadmap

open System
open System.Text

type Area =
  | LiveTesting
  | HotReload
  | Repl
  | Isolation
  | Agents
  | Editors
  | Dashboard
  | Platform
  | Docs

/// How far away the work is. A guess, and the page says so.
type Horizon =
  | Now
  | Next
  | Later
  | Exploring

/// A tracked file and a symbol that appears on a non-comment line of it once the work exists.
type Landmark = { Path: string; Symbol: string }

type Arrival =
  | NoLandmarkYet
  | Marked of Landmark

type Item =
  { Id: string
    Title: string
    Area: Area
    Horizon: Horizon
    Summary: string
    Arrival: Arrival
    /// Repo-relative paths of tracked public docs: docs/*.md, Readme.md or CONTRIBUTING.md.
    Links: string list }

type Standing =
  | OpenAt of Horizon
  | Built of Landmark

type FileState =
  | Missing
  | Lines of string[]

let areas : Area list =
  [ LiveTesting; HotReload; Repl; Isolation; Agents; Editors; Dashboard; Platform; Docs ]

let horizons : Horizon list = [ Now; Next; Later; Exploring ]

let areaName (area: Area) : string =
  match area with
  | LiveTesting -> "Live testing"
  | HotReload -> "Hot reload"
  | Repl -> "The REPL"
  | Isolation -> "Isolation"
  | Agents -> "Agents and cohorts"
  | Editors -> "Editors"
  | Dashboard -> "Dashboard"
  | Platform -> "Platform and install"
  | Docs -> "Docs and onboarding"

let horizonName (horizon: Horizon) : string =
  match horizon with
  | Now -> "Now"
  | Next -> "Next"
  | Later -> "Later"
  | Exploring -> "Exploring"

/// What the horizon means, in the words the page uses for it.
let horizonBlurb (horizon: Horizon) : string =
  match horizon with
  | Now -> "Being built right now. Days to a few weeks."
  | Next -> "Designed, or close to it, and queued behind Now. Weeks to a couple of months."
  | Later -> "I want it and I roughly know how. Months to a year, and the order will move."
  | Exploring -> "An idea I'm turning over. No promise, and some of these will die."

let private isComment (line: string) = line.TrimStart().StartsWith "//"

/// The landmark is present when its file exists and a line that is not a comment contains the symbol.
let landmarkPresent (read: string -> FileState) (landmark: Landmark) : bool =
  match read landmark.Path with
  | Missing -> false
  | Lines lines ->
    lines
    |> Array.exists (fun line -> not (isComment line) && line.Contains(landmark.Symbol, StringComparison.Ordinal))

let standing (read: string -> FileState) (item: Item) : Standing =
  match item.Arrival with
  | NoLandmarkYet -> OpenAt item.Horizon
  | Marked landmark ->
    match landmarkPresent read landmark with
    | true -> Built landmark
    | false -> OpenAt item.Horizon

// ---- voice -------------------------------------------------------------

/// Words this repo's docs do not use. The page is written to read like a person, so the test
/// refuses these in every item.
let bannedWords : string list =
  [ "honest"; "honestly"; "genuinely"; "powerful"; "seamless"; "seamlessly"; "robust"
    "leverage"; "unlock"; "game-changer"; "cutting-edge"; "delightful"; "elevate" ]

let private emDash = "—"

/// Every problem the text has with the house voice, one message each. Empty means clean.
let voiceProblems (text: string) : string list =
  let lower = text.ToLowerInvariant()
  [ match text.Contains emDash with
    | true -> yield "contains an em dash"
    | false -> ()
    for word in bannedWords do
      let found =
        System.Text.RegularExpressions.Regex.IsMatch(lower, "\\b" + System.Text.RegularExpressions.Regex.Escape word + "\\b")
      match found with
      | true -> yield sprintf "uses the word '%s'" word
      | false -> () ]

// ---- render ------------------------------------------------------------

let repoUrl = "https://github.com/WillEhrendreich/SageFs"

/// A link written from docs/roadmap.md: docs pages are siblings, root files are one level up.
let linkTarget (path: string) : string =
  match path.StartsWith "docs/" with
  | true -> path.Substring "docs/".Length
  | false -> "../" + path

let private linkLabel (path: string) : string =
  let name = path.Substring(path.LastIndexOf '/' + 1)
  name

let private itemLine (item: Item) : string =
  let links =
    item.Links
    |> List.map (fun path -> sprintf "[%s](%s)" (linkLabel path) (linkTarget path))
    |> String.concat ", "
  let tail =
    match item.Links with
    | [] -> ""
    | _ -> sprintf " (%s)" links
  sprintf "- **%s.** %s%s" item.Title item.Summary tail

let private builtLine (item: Item) (landmark: Landmark) : string =
  sprintf "- **%s.** %s Code: [`%s`](%s/blob/master/%s)" item.Title item.Summary landmark.Path repoUrl landmark.Path

let intro : string list =
  [ "# Roadmap"
    ""
    "This is where SageFs is headed, as far as I can see it. Some of it is being built today and some of it is an idea I haven't worked out yet. I'd rather you see all of it than guess."
    ""
    "## How this page stays true"
    ""
    "I don't edit status by hand. Each item can name a landmark, a file and a symbol that will exist in the code once the work does. When it shows up in the tree, the item moves to Built on its own and links to the code. A test fails if this page and the code disagree, so a stale page can't be committed."
    ""
    "The horizons are guesses about distance and I'm not promising dates. Things move, and the order below is my best current read. If something here matters to you and it's far away, tell me. That moves things more than anything else does."
    "" ]

/// The page, from the items and whatever the tree has. `read` answers file questions for landmarks.
let render (read: string -> FileState) (items: Item list) : string =
  let resolved = items |> List.map (fun item -> item, standing read item)
  let builtItems =
    resolved
    |> List.choose (fun (item, s) ->
      match s with
      | Built landmark -> Some (item, landmark)
      | OpenAt _ -> None)
  let openAt (horizon: Horizon) : Item list =
    resolved
    |> List.choose (fun (item, s) ->
      match s with
      | OpenAt h when h = horizon -> Some item
      | _ -> None)
  let sb = StringBuilder()
  let line (text: string) = sb.Append(text).Append('\n') |> ignore
  line "<!-- GENERATED by scripts/gen-roadmap.fsx from scripts/roadmap/RoadmapItems.fs. Edit the items, then run the script. -->"
  for text in intro do
    line text
  let counts =
    horizons
    |> List.map (fun h -> sprintf "%s %d" (horizonName h) (openAt h |> List.length))
    |> String.concat ", "
  line (sprintf "On the page today: %s. Already built: %d." counts (List.length builtItems))
  line ""
  for horizon in horizons do
    let inHorizon = openAt horizon
    match inHorizon with
    | [] -> ()
    | _ ->
      line (sprintf "## %s" (horizonName horizon))
      line ""
      line (sprintf "_%s_" (horizonBlurb horizon))
      line ""
      for area in areas do
        match inHorizon |> List.filter (fun item -> item.Area = area) with
        | [] -> ()
        | inArea ->
          line (sprintf "### %s" (areaName area))
          line ""
          for item in inArea do
            line (itemLine item)
          line ""
  match builtItems with
  | [] -> ()
  | _ ->
    line "## Built"
    line ""
    line "_These were on this page and are in the code now. Whether a build has shipped is in [the release notes](https://github.com/WillEhrendreich/SageFs/releases) and [what SageFs has become](progress.md)._"
    line ""
    for area in areas do
      match builtItems |> List.filter (fun (item, _) -> item.Area = area) with
      | [] -> ()
      | inArea ->
        line (sprintf "### %s" (areaName area))
        line ""
        for (item, landmark) in inArea do
          line (builtLine item landmark)
        line ""
  line "---"
  line ""
  line "Something missing, or something here you'd bump up? Open an [issue](https://github.com/WillEhrendreich/SageFs/issues) or find me in the F# Discord."
  sb.ToString()
