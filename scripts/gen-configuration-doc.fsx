// Regenerates the tables in docs/configuration.md from SageFs.Core/Timeouts.fs.
//
//   dotnet fsi scripts/gen-configuration-doc.fsx <commit-sha>
//
// The tables sit between the GENERATED markers in the doc. Everything outside
// them is written by hand and this script never touches it. <commit-sha> is the
// commit the source links point at; the script refuses to run if Timeouts.fs in
// the working tree differs from that commit, so a link always lands on the line
// it names.
//
// A description is the constant's own doc comment with its line breaks joined
// and " — " written as ", " (this repo's docs do not use em dashes). Four
// constants have no doc comment of their own. Each of those carries a note
// below that says where its words come from.
//
// Every count is asserted. A change to Timeouts.fs that this parser does not
// understand fails here instead of quietly dropping a row.

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

let sha =
  match fsi.CommandLineArgs |> Array.tryItem 1 with
  | Some s when Regex.IsMatch(s, "^[0-9a-f]{40}$") -> s
  | _ -> failwith "pass the full 40-character commit sha the links should point at"

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let timeoutsPath = Path.Combine(repoRoot, "SageFs.Core", "Timeouts.fs")
let docPath = Path.Combine(repoRoot, "docs", "configuration.md")

let git (args: string) =
  let psi = ProcessStartInfo("git", args, WorkingDirectory = repoRoot, RedirectStandardOutput = true, RedirectStandardError = true)
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEnd()
  p.WaitForExit()
  p.ExitCode, out

let diffCode, _ = git (sprintf "diff --quiet %s -- SageFs.Core/Timeouts.fs" sha)
if diffCode <> 0 then
  failwithf "SageFs.Core/Timeouts.fs differs from %s, so line links to it would be wrong. Commit first, or pass a sha that has this file." sha

let lines = File.ReadAllLines timeoutsPath

// ---- parse ------------------------------------------------------------

type Unit' =
  | Seconds
  | Minutes
  | Days
  | Count

type Row =
  { Var: string
    Name: string
    Area: string
    Default: float
    Unit: Unit'
    Comment: string option
    Line: int }

let sectionRx = Regex(@"^  // -- (.+) --\s*$")
let docRx = Regex(@"^\s*///\s?(.*)$")
let envRx = Regex(@"(envOrDefaultMinutes|envOrDefault)\s+""(SAGEFS_[A-Z0-9_]+)""\s+(.+?)\s*$")
let letRx = Regex(@"^\s*let\s+(?:mutable\s+)?(?:private\s+)?(\w+)\b")

/// Numeric literals the defaults are written as, plus the two shapes this file
/// uses beyond a bare number: `(6.0 * 60.0)` and `someFallback.TotalSeconds`.
let fallbackSeconds =
  lines
  |> Array.choose (fun l ->
    let m = Regex.Match(l, @"^\s*let\s+(\w+)\s*=\s*TimeSpan\.FromSeconds\(([0-9.]+)\)\s*$")
    match m.Success with
    | true -> Some (m.Groups.[1].Value, float m.Groups.[2].Value)
    | false -> None)
  |> Map.ofArray

let resolveDefault (expr: string) : float =
  let bare = Regex.Match(expr, @"^([0-9_.]+)$")
  let product = Regex.Match(expr, @"^\(([0-9.]+)\s*\*\s*([0-9.]+)\)$")
  let viaFallback = Regex.Match(expr, @"^(\w+)\.TotalSeconds$")
  match bare.Success, product.Success, viaFallback.Success with
  | true, _, _ -> float (bare.Groups.[1].Value.Replace("_", ""))
  | _, true, _ -> float product.Groups.[1].Value * float product.Groups.[2].Value
  | _, _, true ->
    match Map.tryFind viaFallback.Groups.[1].Value fallbackSeconds with
    | Some v -> v
    | None -> failwithf "default %s names a fallback this script cannot find" expr
  | _ -> failwithf "cannot read the default %s" expr

let docAbove (letIndex: int) : string option =
  let rec up i acc =
    match i >= 0 && docRx.IsMatch lines.[i] with
    | true -> up (i - 1) (docRx.Match(lines.[i]).Groups.[1].Value :: acc)
    | false -> acc
  match up (letIndex - 1) [] with
  | [] -> None
  | parts ->
    // Join the lines with a space, except after a "/" (a path split across lines).
    parts
    |> List.map (fun s -> s.Trim())
    |> List.filter (fun s -> s <> "")
    |> List.fold (fun (acc: string) s -> match acc with "" -> s | _ when acc.EndsWith "/" -> acc + s | _ -> acc + " " + s) ""
    |> Some

let mutable area = ""

let timeoutRows =
  [ for i in 0 .. lines.Length - 1 do
      let line = lines.[i]
      let sec = sectionRx.Match line
      if sec.Success then area <- sec.Groups.[1].Value
      let env = envRx.Match line
      if env.Success then
        // The value is either on the `let` line or on the line after a bare `let x =`.
        let letIndex = if letRx.IsMatch line then i else i - 1
        let name = letRx.Match(lines.[letIndex]).Groups.[1].Value
        let unit', seconds = (match env.Groups.[1].Value with "envOrDefaultMinutes" -> Minutes | _ -> Seconds), resolveDefault env.Groups.[3].Value
        yield
          { Var = env.Groups.[2].Value
            Name = name
            Area = area
            Default = seconds
            Unit = unit'
            Comment = docAbove letIndex
            Line = letIndex + 1 } ]

// DataRetention: the env var names are string constants, the readers take the constant.
let retentionStart = lines |> Array.findIndex (fun l -> l.StartsWith "module DataRetention")
let retentionVars =
  lines
  |> Array.mapi (fun i l -> i, Regex.Match(l, @"^\s*let\s+(\w+EnvVar)\s*=\s*""(SAGEFS_[A-Z0-9_]+)""\s*$"))
  |> Array.filter (fun (_, m) -> m.Success)
  |> Array.map (fun (_, m) -> m.Groups.[1].Value, m.Groups.[2].Value)
  |> Map.ofArray

let retentionRows =
  [ for i in retentionStart .. lines.Length - 1 do
      let m = Regex.Match(lines.[i], @"^\s*let\s+(\w+)\s*=\s*envOrDefault(Days|Int)\s+(\w+EnvVar)\s+([0-9_.]+)\s*$")
      if m.Success then
        let var = Map.find m.Groups.[3].Value retentionVars
        yield
          { Var = var
            Name = m.Groups.[1].Value
            Area = "Local data retention"
            Default = float (m.Groups.[4].Value.Replace("_", ""))
            Unit = (match m.Groups.[2].Value with "Days" -> Days | _ -> Count)
            Comment = docAbove i
            Line = i + 1 } ]

// pruneInterval is written out by hand in the source (an env value in minutes, a one hour default).
let pruneIndex = lines |> Array.findIndex (fun l -> Regex.IsMatch(l, @"^\s*let pruneInterval\s*="))
let pruneBody = String.Join("\n", lines.[pruneIndex .. pruneIndex + 8])
if not (pruneBody.Contains "pruneIntervalEnvVar" && pruneBody.Contains "TimeSpan.FromHours(1.0)" && pruneBody.Contains "TimeSpan.FromMinutes(v)") then
  failwith "pruneInterval no longer has the shape this script reads (env value in minutes, default 1 hour)"
let pruneRow =
  { Var = Map.find "pruneIntervalEnvVar" retentionVars
    Name = "pruneInterval"
    Area = "Local data retention"
    Default = 60.0
    Unit = Minutes
    Comment = docAbove pruneIndex
    Line = pruneIndex + 1 }

let allRows = timeoutRows @ retentionRows @ [ pruneRow ]

// ---- the four with no doc comment of their own -------------------------

let borrowed : Map<string, string> =
  Map.ofList
    [ "SAGEFS_WARMUP_MAX_MINUTES",
        "The hard ceiling on a worker's warmup, which neither progress nor silence can argue past. The constant has no doc comment; these words are from the one on `awaitWorkerPort` in `SessionManager.fs`."
      "SAGEFS_WARMUP_INACTIVITY_SECONDS",
        "How long a starting worker may go without reporting progress before it is declared stuck. The clock resets on every `WARMUP_PROGRESS=` line, so silence, not slowness, is what trips it. The constant has no doc comment; these words are from the one on `awaitWorkerPort` in `SessionManager.fs`."
      "SAGEFS_WORKER_HTTP_READ_SECONDS",
        "The read timeout the daemon gives the worker's streaming test proxy (`DaemonMode.fs`, `HttpWorkerClient.streamingTestProxyWithCoverage`). The constant has no doc comment; the one on `daemonSessionsProbe` says that probe is shorter than this."
      "SAGEFS_PER_TEST_TIMEOUT_SECONDS",
        "The per-test timeout when neither the environment nor a setting says otherwise. The constant has no doc comment; these words are from the one on `perTestTimeoutFallback`, whose default is 5 seconds. The section is headed \"configurable at runtime via MCP\", so this is the starting value." ]

let expectedBorrowed =
  allRows |> List.filter (fun r -> r.Comment.IsNone) |> List.map (fun r -> r.Var) |> Set.ofList
if expectedBorrowed <> (borrowed |> Map.toList |> List.map fst |> Set.ofList) then
  failwithf "the constants without a doc comment are now %A; update the borrowed notes in this script" (Set.toList expectedBorrowed)

// ---- asserted counts --------------------------------------------------

let envLiteralCount =
  lines |> Array.sumBy (fun l -> Regex.Matches(l, @"""SAGEFS_[A-Z0-9_]+""").Count)
// Each var appears once as a string literal in this file.
if envLiteralCount <> allRows.Length then
  failwithf "Timeouts.fs has %d SAGEFS_ literals but %d rows were parsed" envLiteralCount allRows.Length
let distinct = allRows |> List.map (fun r -> r.Var) |> List.distinct
if distinct.Length <> allRows.Length then failwith "a variable appears in two rows"
printfn "parsed %d variables (%d timeouts, %d retention)" allRows.Length (timeoutRows.Length) (retentionRows.Length + 1)

// ---- render -----------------------------------------------------------

let fmtNumber (v: float) = if v = Math.Floor v then string (int64 v) else string v
let unitName (r: Row) =
  match r.Unit with
  | Seconds -> "seconds"
  | Minutes -> "minutes"
  | Days -> "days"
  | Count -> (if r.Var.EndsWith "ROWS" then "rows" else "versions")

let tidy (s: string) = s.Replace(" — ", ", ").Replace("|", "\\|")

let describe (r: Row) =
  match r.Comment with
  | Some c -> tidy c
  | None -> tidy (Map.find r.Var borrowed)

let link (r: Row) =
  sprintf "[L%d](https://github.com/WillEhrendreich/SageFs/blob/%s/SageFs.Core/Timeouts.fs#L%d)" r.Line sha r.Line

let areas =
  allRows |> List.map (fun r -> r.Area) |> List.distinct

let render () =
  let sb = Text.StringBuilder()
  for a in areas do
    let rows = allRows |> List.filter (fun r -> r.Area = a)
    sb.AppendLine(sprintf "### %s" a).AppendLine() |> ignore
    sb.AppendLine("| Variable | Default | Unit | What it is for | Source |").AppendLine("|:---|---:|:---|:---|:---|") |> ignore
    for r in rows do
      sb.AppendLine(sprintf "| `%s` | %s | %s | %s | %s |" r.Var (fmtNumber r.Default) (unitName r) (describe r) (link r)) |> ignore
    sb.AppendLine() |> ignore
  sb.ToString().TrimEnd() + "\n"

// ---- splice into the doc ----------------------------------------------

let beginMarker = "<!-- BEGIN GENERATED: timeouts (scripts/gen-configuration-doc.fsx) -->"
let endMarker = "<!-- END GENERATED: timeouts -->"
let doc = File.ReadAllText docPath
let b = doc.IndexOf beginMarker
let e = doc.IndexOf endMarker
if b < 0 || e < b then failwithf "%s must hold both generated markers" docPath
let spliced = doc.Substring(0, b + beginMarker.Length) + "\n\n" + render () + "\n" + doc.Substring e
File.WriteAllText(docPath, spliced)
printfn "wrote %d areas to %s" areas.Length docPath
