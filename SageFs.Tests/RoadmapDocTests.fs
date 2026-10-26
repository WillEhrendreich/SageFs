/// docs/roadmap.md is generated from scripts/roadmap/RoadmapItems.fs by scripts/gen-roadmap.fsx, and an
/// item's status comes from whether its landmark is in the tree. These tests keep the page from drifting
/// from the code and keep the items inside the voice the rest of the docs use.
module SageFs.Tests.RoadmapDocTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs.Roadmap

let private repoRoot = RepoPaths.repoPathFull [||]

/// The main checkout that owns `repoRoot`: a worktree's `.git` file points into it, a normal checkout is
/// its own. It is the anchor `resolvePath` gives a path that climbs out of the tree.
let private mainRoot = RepoPaths.mainCheckoutRoot repoRoot

let private readFromRepo (relative: string) : FileState =
  let path = resolvePath repoRoot mainRoot relative
  match File.Exists path with
  | true -> Lines(File.ReadAllLines path)
  | false -> Missing

let private items = SageFs.RoadmapItems.items

let private normalize (text: string) = text.Replace("\r\n", "\n")

let private kebab = Regex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled)

let private unionCaseCount<'T> () = Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<'T>).Length

let private itemText (item: Item) = sprintf "%s %s" item.Title item.Summary

let private fakeTree (path: string) : FileState =
  match path with
  | "src/Built.fs" -> Lines [| "module X"; "let TrunkReload = 1" |]
  | "src/CommentOnly.fs" -> Lines [| "// TrunkReload is planned" |]
  | _ -> Missing

let private sample (arrival: Arrival) : Item =
  { Id = "sample"
    Title = "Sample"
    Area = HotReload
    Horizon = Next
    Summary = "A thing."
    Arrival = arrival
    Links = [] }

[<Tests>]
let tests =
  testList "Roadmap page" [

    TestInfrastructure.Ratchet.case TestInfrastructure.Ratchet.Invariant "WHY — docs/roadmap.md is exactly what the generator writes from the items and the tree, so the page cannot go stale in a commit" <| fun _ ->
      let onDisk = File.ReadAllText(Path.Combine(repoRoot, "docs", "roadmap.md")) |> normalize
      onDisk |> Expect.equal "the page matches the render (run `dotnet fsi scripts/gen-roadmap.fsx`)" (render readFromRepo items |> normalize)

    testCase "WHY — every item has its own id, and the id is a slug, so a link or a landmark can name one item" <| fun _ ->
      let ids = items |> List.map (fun item -> item.Id)
      ids |> List.filter (kebab.IsMatch >> not) |> Expect.isEmpty "every id is kebab-case"
      (ids |> List.distinct |> List.length) |> Expect.equal "no id is used twice" (List.length ids)

    testCase "WHY — items are written in the house voice, so the roadmap reads like a person and not a press release" <| fun _ ->
      let problems =
        items
        |> List.collect (fun item -> voiceProblems (itemText item) |> List.map (fun p -> sprintf "%s %s" item.Id p))
      problems |> Expect.isEmpty "no item has an em dash or a banned word"

    TestInfrastructure.Ratchet.case TestInfrastructure.Ratchet.Invariant "WHY — the whole generated page is in the house voice too, intro included" <| fun _ ->
      voiceProblems (render readFromRepo items) |> Expect.isEmpty "the page has no em dash and no banned word"

    testCase "WHY — every item has a title and a summary that ends like a sentence, so the page has no stubs" <| fun _ ->
      let bad =
        items
        |> List.filter (fun item -> item.Title.Trim() = "" || item.Summary.Trim() = "" || not (item.Summary.TrimEnd().EndsWith "."))
        |> List.map (fun item -> item.Id)
      bad |> Expect.isEmpty "every item has a title and a one-sentence-or-two summary ending in a full stop"

    TestInfrastructure.Ratchet.case TestInfrastructure.Ratchet.Invariant "WHY — a link goes to a public doc that exists, never to one of the private working notes at the repo root" <| fun _ ->
      let isPublic (path: string) =
        (path.StartsWith "docs/" && path.EndsWith ".md") || path = "Readme.md" || path = "CONTRIBUTING.md"
      let bad =
        items
        |> List.collect (fun item -> item.Links |> List.map (fun link -> item.Id, link))
        |> List.filter (fun (_, link) -> not (isPublic link) || not (File.Exists(Path.Combine(repoRoot, link))))
        |> List.map (fun (id, link) -> sprintf "%s -> %s" id link)
      bad |> Expect.isEmpty "every link is an existing docs/*.md, Readme.md or CONTRIBUTING.md"

    TestInfrastructure.Ratchet.case TestInfrastructure.Ratchet.Invariant "WHY — a landmark's folder exists, so a typo cannot leave an item open forever without anyone noticing" <| fun _ ->
      let bad =
        items
        |> List.choose (fun item ->
          match item.Arrival with
          | NoLandmarkYet -> None
          | Marked landmark ->
            let folder = Path.GetDirectoryName(resolvePath repoRoot mainRoot landmark.Path)
            match landmark.Symbol.Trim() <> "" && Directory.Exists folder with
            | true -> None
            | false -> Some (sprintf "%s -> %s (%s)" item.Id landmark.Path landmark.Symbol))
      // The offenders travel in the message, not only in Expecto's diff: the ratchet lane prints the
      // assertion message and nothing else, so a red run in CI gave no way to learn WHICH landmark
      // broke without reproducing the whole tier locally. That is what happened on two pull requests.
      bad
      |> Expect.isEmpty (
        sprintf "every landmark names a symbol and a folder that exists; these do not: %A" bad)

    testCase "WHY — a path that climbs out of the checkout resolves against the main checkout even from a nested root, and an in-repo path resolves against this root, so every worktree reads the same sibling file and its own branch" <| fun _ ->
      let nestedRoot = Path.Combine(repoRoot, ".claude", "worktrees", "sample")
      resolvePath nestedRoot mainRoot "../sagefs.nvim/lua/sagefs/dashboard/state.lua"
      |> Expect.equal
        "an out-of-checkout path is anchored at the main checkout, not the nested root"
        (Path.GetFullPath(Path.Combine(mainRoot, "../sagefs.nvim/lua/sagefs/dashboard/state.lua")))
      resolvePath nestedRoot mainRoot "docs/roadmap.md"
      |> Expect.equal
        "an in-repo path stays anchored at this root, so a worktree reads its own branch"
        (Path.GetFullPath(Path.Combine(nestedRoot, "docs/roadmap.md")))

    testCase "WHY — the area and horizon lists name every case, so an item can never land in a section that is not rendered" <| fun _ ->
      (areas |> List.length) |> Expect.equal "every Area is listed" (unionCaseCount<Area> ())
      (horizons |> List.length) |> Expect.equal "every Horizon is listed" (unionCaseCount<Horizon> ())

    testCase "WHY — an item is Built when its landmark is in the file on a real line, and not otherwise" <| fun _ ->
      let landmark (path: string) = Marked { Path = path; Symbol = "TrunkReload" }
      standing fakeTree (sample (landmark "src/Built.fs"))
      |> Expect.equal "symbol present on a code line" (Built { Path = "src/Built.fs"; Symbol = "TrunkReload" })
      standing fakeTree (sample (landmark "src/CommentOnly.fs"))
      |> Expect.equal "a comment that names the symbol does not count" (OpenAt Next)
      standing fakeTree (sample (landmark "src/Nope.fs"))
      |> Expect.equal "a missing file is not built" (OpenAt Next)
      standing fakeTree (sample NoLandmarkYet)
      |> Expect.equal "no landmark stays open" (OpenAt Next)

    testCase "WHY — a Built item leaves its horizon and shows up under Built with a link to the code" <| fun _ ->
      let page =
        render fakeTree [ { sample (Marked { Path = "src/Built.fs"; Symbol = "TrunkReload" }) with Title = "Done thing" } ]
      page |> Expect.stringContains "it is listed under Built" "## Built"
      page |> Expect.stringContains "it links to the code" "blob/master/src/Built.fs"
      (page.Contains "## Next") |> Expect.isFalse "its old horizon section is gone"

    TestInfrastructure.Ratchet.case TestInfrastructure.Ratchet.Invariant "WHY — every roadmap item that is not built has a plan it links to, and every plan belongs to an item, so no item can lose its handoff" <| fun _ ->
      let planIds =
        Directory.GetFiles(Path.Combine(repoRoot, "docs", "plans"), "*.md")
        |> Array.map Path.GetFileNameWithoutExtension
        |> Array.filter (fun name -> name <> "README")
        |> Set.ofArray
      let itemIds = items |> List.map (fun item -> item.Id) |> Set.ofList
      let openItems =
        items
        |> List.filter (fun item ->
          match standing readFromRepo item with
          | OpenAt _ -> true
          | Built _ -> false)
      openItems |> List.map (fun item -> item.Id) |> List.filter (fun id -> not (Set.contains id planIds))
      |> Expect.isEmpty "every item that is not built has docs/plans/<id>.md"
      openItems |> List.filter (fun item -> not (List.contains (sprintf "docs/plans/%s.md" item.Id) item.Links)) |> List.map (fun item -> item.Id)
      |> Expect.isEmpty "and links to it from its roadmap entry"
      planIds |> Set.filter (fun id -> not (Set.contains id itemIds)) |> Set.toList
      |> Expect.isEmpty "and a plan exists only for an item that is on the roadmap"

    TestInfrastructure.Ratchet.case TestInfrastructure.Ratchet.Invariant "WHY — every plan carries a Resume here block with every field and a status from the closed set, so someone with no other context can pick the item up" <| fun _ ->
      let fields = [ "Status"; "Size"; "Branch and worktree"; "Last green commit"; "Next action"; "Decisions made"; "Surprises" ]
      let statuses = [ "Not started"; "In progress"; "Blocked"; "Done" ]
      let problems =
        Directory.GetFiles(Path.Combine(repoRoot, "docs", "plans"), "*.md")
        |> Array.filter (fun path -> Path.GetFileNameWithoutExtension path <> "README")
        |> Array.collect (fun path ->
          let name = Path.GetFileNameWithoutExtension path
          let text = File.ReadAllText path |> normalize
          let missingBlock = if text.Contains "\n## Resume here\n" then [||] else [| sprintf "%s: no '## Resume here' block" name |]
          let missingFields =
            fields
            |> List.filter (fun field -> not (Regex.IsMatch(text, sprintf @"(?m)^\| %s \| .+ \|$" (Regex.Escape field))))
            |> List.map (fun field -> sprintf "%s: no '%s' row" name field)
            |> List.toArray
          let badStatus =
            let m = Regex.Match(text, @"(?m)^\| Status \| (.+) \|$")
            match m.Success && statuses |> List.exists (fun s -> m.Groups.[1].Value.StartsWith s) with
            | true -> [||]
            | false -> [| sprintf "%s: Status must start with one of %s" name (String.concat ", " statuses) |]
          Array.concat [ missingBlock; missingFields; badStatus ])
      problems |> Array.toList |> Expect.isEmpty "every plan has the block, every row, and a valid status"

    TestInfrastructure.Ratchet.case TestInfrastructure.Ratchet.Invariant "WHY — a plan says Done exactly when its roadmap item is Built, so the handoff and the page cannot disagree about whether the work is finished" <| fun _ ->
      let problems =
        items
        |> List.choose (fun item ->
          let path = Path.Combine(repoRoot, "docs", "plans", item.Id + ".md")
          match File.Exists path with
          | false -> None
          | true ->
            let m = Regex.Match(File.ReadAllText path |> normalize, @"(?m)^\| Status \| (.+) \|$")
            let saysDone = m.Success && m.Groups.[1].Value.StartsWith "Done"
            let isBuilt =
              match standing readFromRepo item with
              | Built _ -> true
              | OpenAt _ -> false
            match saysDone = isBuilt with
            | true -> None
            | false ->
              let said = if saysDone then "Done" else "not Done"
              let page = if isBuilt then "Built" else "open"
              Some (sprintf "%s: the plan says %s but the roadmap says %s" item.Id said page))
      problems |> Expect.isEmpty "Done if and only if Built"
  ]
