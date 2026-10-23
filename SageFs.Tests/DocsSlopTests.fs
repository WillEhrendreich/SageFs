module SageFs.Tests.DocsSlopTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

/// The shapes that read as machine-made. The rule they come from is in AGENTS.md
/// ("Writing: no slop"), and the catalogue behind it is the `plain-english` skill.
///
/// Only the shapes with no legitimate use are here. A WORD with a legitimate use ("ecosystem",
/// "leverage", "unpack") is a review list, not a ban: the catalogue says so, and a ratchet that
/// flagged them would fail on good prose. The em dash is here because the guidance says near
/// zero per page, and the exceptions are spelled out below.
///
/// The exclusions matter as much as the patterns. A dash between two digits is a range
/// ("1612-1632"), a dash inside quotes is material the product really prints, and a dash in a
/// code block is a sample a reader may copy. A doc that misquotes the product is worse than a
/// doc with a tell in it.
module Shape =
  /// (name, pattern). The names are the categories in the guidance.
  let structural : (string * string) list =
    [ "a negation frame",
      @"\b(?:isn't|aren't|doesn't|don't|wasn't|weren't|not)\s+(?:just|only|merely|simply|about)\s+[^.\n]{2,70}?[,;—–-]\s?(?:it's|it is|but|they're|they are)"
      "a tailing negation", @"\bno [a-z]{3,12},\s*just [a-z]{3,12}\b"
      "a staged opener",
      @"\b(?:In today's fast-paced world|In today's digital age|In the ever-evolving|In a world where|In an era of|Ever wondered|Have you ever|What if I told you|Did you know|Picture this|Imagine a world where|Let's dive in|Let's delve into|Let's explore)\b"
      "a staged emphasis",
      @"\b(?:the key takeaway|the real question is|at its core|fundamentally speaking|the truth is|in reality|the heart of the matter|let that sink in|read that again|it's important to note|it is important to note|it's worth noting|it bears mentioning|one thing to consider|a key consideration|it cannot be overstated)\b"
      "a closing beat",
      @"\b(?:In conclusion|In summary|To sum up|To wrap up|The key takeaway is|All in all|and that changes everything|Hope this helps|Feel free to reach out|Let me know if you'd like|If you want, I can|I can also help)\b"
      "a weasel attribution",
      @"\b(?:experts? (?:agree|say)|studies show|research suggests|scientists believe|it is widely (?:regarded|believed)|critics (?:note|argue)|observers note|some say)\b"
      "a fake-insight tail",
      @",\s+(?:highlighting|reflecting|symbolizing|emphasizing|underscoring|showcasing|signaling|demonstrating|illustrating|embodying|reinforcing|cultivating|raising questions about|sparking debate|prompting reflection|with broader implications|reshaping our understanding|paving the way for)\b"
      "a faux-insight setup",
      @"\b(?:what nobody tells you|what no one tells you|the part everyone misses|what most people get wrong)\b"
      "a hedge stack", @"\b(?:may|might|could) (?:potentially|possibly|perhaps)\b"
      "a transition crutch",
      @"\b(?:Moreover|Furthermore|Additionally|Consequently|Hence|Nevertheless|Nonetheless|Notably|Ultimately|Generally speaking|Needless to say|It goes without saying)\b"
      "filler grammar",
      @"\b(?:in order to|has the ability to|a large number of|in close proximity to|prior to the start of|during the course of|on a regular basis|in spite of the fact that|for all intents and purposes|the fact of the matter is|as many of you already know|with regard to|conduct an investigation|provide assistance to|make a decision on)\b"
      "template residue",
      @"\[(?:Your Name|insert [a-z]{2,20}|company|date|todo|xx|placeholder|email)\]" ]

  /// The em dash, which the guidance says should be near zero per page.
  let emDash = Regex(" ?[—–]")

/// The spans of a line that sit inside double quotes.
let private quotedSpans (line: string) : (int * int) list =
  let spans = ResizeArray<int * int>()
  let mutable inside = false
  let mutable start = 0
  for i in 0 .. line.Length - 1 do
    if line.[i] = '"' then
      if inside then
        spans.Add(start, i)
        inside <- false
      else
        inside <- true
        start <- i + 1
  if inside then spans.Add(start, line.Length)
  List.ofSeq spans

let private inQuoted (spans: (int * int) list) (index: int) =
  spans |> List.exists (fun (a, b) -> index >= a && index <= b)

/// Every markdown file the repo SHIPS, never build output and never a worktree.
///
/// The tracked files, not every file on disk: the untracked notes at the repo root are working
/// notes, and one of them is a document this rule must not rewrite.
let private markdownFiles () =
  let root = RepoPaths.repoPathFull [||]
  if not (Directory.Exists root) then [||]
  else
    let psi =
      System.Diagnostics.ProcessStartInfo("git", "ls-files \"*.md\"")
    psi.WorkingDirectory <- root
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    try
      use p = System.Diagnostics.Process.Start psi
      let out = p.StandardOutput.ReadToEnd()
      p.WaitForExit()
      if p.ExitCode <> 0 then [||]
      else
        out.Split('\n')
        |> Array.filter (fun l -> l.Trim() <> "")
        |> Array.map (fun l -> l.Trim())
        |> Array.filter (fun rel ->
          let normal = rel.Replace('\\', '/')
          not (normal.Contains "/obj/")
          && not (normal.Contains "/bin/")
          && not (normal.Contains "/node_modules/")
          && not (normal.Contains "/.claude/worktrees/")
          && not (normal.Contains "/coverage-report/"))
        |> Array.map (fun rel -> Path.Combine(root, rel))
    with _ -> [||]

/// The em dashes in one line that are prose: not a range, not quoted material, not in code.
let private proseDashes (line: string) : int =
  let quotes = quotedSpans line
  let mutable count = 0
  for m in Shape.emDash.Matches line do
    let dashIndex = m.Index
    let before = if dashIndex > 0 then line.Substring(0, dashIndex) else ""
    let after = line.Substring(dashIndex + m.Length)
    let beforeChar = if before.Length > 0 then before[before.Length - 1] else ' '
    let afterChar = if after.Length > 0 then after[0] else ' '
    let isRange = Char.IsDigit beforeChar && Char.IsDigit afterChar
    if not isRange && not (inQuoted quotes dashIndex) then count <- count + 1
  count

/// The line of a file, with the code fences and the inline code removed.
let private proseOf (lines: string array) =
  let mutable inFence = false
  let mutable inRule = false
  lines
  |> Array.mapi (fun i line ->
    if line.TrimStart().StartsWith "```" then
      inFence <- not inFence
      ""
    elif inFence then ""
    else
      // The block that NAMES the shapes is allowed to name them. It ends at the next heading,
      // and the flag has to turn off there, or the rest of the file goes unread.
      if inRule && line.TrimStart().StartsWith "#" then inRule <- false
      let isRuleHeading = line.TrimStart('#').Trim() = "Writing: no slop"
      if isRuleHeading then inRule <- true
      if inRule then ""
      // Inline code is a sample or an identifier, not prose.
      else line.Split('`') |> Array.mapi (fun j part -> if j % 2 = 1 then "" else part) |> String.concat "")

/// Every banned shape in the docs, with where it is.
let private findings () =
  let found = ResizeArray<string * int * string * string>()
  for path in markdownFiles () do
    let lines = File.ReadAllText path |> fun t -> t.Split('\n')
    let relative = path.Replace('\\', '/')
    let prose = proseOf lines
    for (name, pattern) in Shape.structural do
      let re = Regex(pattern, RegexOptions.IgnoreCase)
      for i in 0 .. lines.Length - 1 do
        let m = re.Match(prose.[i])
        if m.Success then
          found.Add((relative, i + 1, name, lines.[i].Trim()))
    for i in 0 .. lines.Length - 1 do
      let dashes = proseDashes prose.[i]
      if dashes > 0 then
        found.Add((relative, i + 1, sprintf "%d em dash(es)" dashes, lines.[i].Trim()))
  List.ofSeq found

[<Tests>]
let tests =
  testList "Documentation style" [
    testCase "WHY - the docs hold to the writing rules, because the rules only work if something checks them" <| fun () ->
      let found = findings ()
      let report =
        found
        |> List.truncate 25
        |> List.map (fun (file, line, name, text) -> sprintf "  %s:%d  [%s]  %s" file line name text)
        |> String.concat "\n"
      found
      |> Expect.equal
        (sprintf
           "the docs carry %d banned shape(s). The rule is in AGENTS.md under 'Writing: no slop', and the catalogue behind it is the `plain-english` skill.\n%s"
           found.Length
           report)
        []
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
