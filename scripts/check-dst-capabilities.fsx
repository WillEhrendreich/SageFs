/// Check that `skills/sagefs/dst-capabilities.md` names only simulations and mutation
/// suites that actually exist, and that its counts are current.
///
/// WHY THIS EXISTS. The first draft of that document named nine modules that do not
/// exist (`SessionLifecycleSim`, `RestartPolicySim`, `WatchdogSim`, `CacheSim`,
/// `TestCachePersistenceSim`, `SessionTestOutcomeSim`, `ScrubberSim`, `ToolGateSim`,
/// `OwnerMonitorSim`). A hand-maintained inventory of a generated surface rots, and
/// then lies — and an agent that trusts it will look for evidence in a file that
/// was never written, which is precisely the "claim with no evidence behind it"
/// failure this repo keeps paying for.
///
/// RUN IT after adding or renaming a simulation or a mutation suite:
///   dotnet fsi scripts/check-dst-capabilities.fsx
///
/// It exits non-zero on a name the document claims but the filesystem does not
/// have, or on a count that has drifted, so the document cannot rot unnoticed.

open System
open System.IO
open System.Text.RegularExpressions

// Locate the repo at RUNTIME, walking up from this script's own location until we
// find the solution file. A build-time constant would bake in the directory the
// script was COMPILED, which is not where it RUNS.
let repoRoot =
  let rec walk (dir: string) (depth: int) : string =
    if depth > 24 then "" else
    let full =
      try
        let f = Path.GetFullPath dir
        let r = Path.GetPathRoot f
        if f = r then f else Path.TrimEndingDirectorySeparator f
      with _ -> dir
    if File.Exists(Path.Combine(full, "SageFs.slnx")) then full
    else
      let parent = Path.GetDirectoryName full
      if String.IsNullOrEmpty parent || parent = full then ""
      else walk parent (depth + 1)
  // Start from the directory this script lives in. A build-time constant only names where it was
  // built, so the walk up to SageFs.slnx is what locates the repo where the code actually runs.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
let repo =
  if repoRoot = "" then
    failwith "Could not locate the SageFs repository (no SageFs.slnx found walking up from this script)."
  else repoRoot

let simDir = Path.Combine(repo, "SageFs.Simulation")
let testDir = Path.Combine(repo, "SageFs.Tests")
let docPath = Path.Combine(repo, "skills", "sagefs", "dst-capabilities.md")

if not (Directory.Exists simDir) then
  printfn "FAIL: no SageFs.Simulation at %s" simDir
  exit 2

let sims =
  Directory.GetFiles(simDir, "*Sim.fs")
  |> Array.map (fun f -> Path.GetFileNameWithoutExtension f)
  |> Set.ofArray

let specs =
  Directory.GetFiles(simDir, "*Spec.fs")
  |> Array.map (fun f -> Path.GetFileNameWithoutExtension f)
  |> Set.ofArray

let mutants =
  Directory.GetFiles(testDir, "*MutationTests.fs")
  |> Array.map (fun f -> Path.GetFileNameWithoutExtension f)
  |> Set.ofArray

let doc = File.ReadAllText docPath

// Every `Backticked` identifier that looks like a module name, so a typo is caught rather
// than read past by a hopeful reader.
let named =
  Regex.Matches(doc, @"`([A-Za-z][A-Za-z0-9]*(Sim|Spec|Invariants|Generators|MutationTests|[A-Z][A-Za-z0-9]*))`")
  |> Seq.map (fun m -> m.Groups.[1].Value)
  |> Set.ofSeq

/// The shared harness modules the document is allowed to name without a suffix check.
let shared : Set<string> =
  Set.ofList [ "Scenario"; "Generators"; "Shrink"; "FaultInjection"; "Invariant"; "Oracle"
               "Runner"; "SeedCorpus"; "Linearizability"; "ReferenceModel"; "Coverage"
               "RestartPolicyBoundaryTests"; "RestartPolicyBoundary"
               // Field and type names the prose legitimately mentions (a simulated clock's
               // parts, a `Mutant<'a>` field). Not modules, so they must not be mistaken for
               // phantoms — a check that cries wolf gets ignored, which is worse than none.
               "ClockAdvance"; "StartTime"; "Mutant" ]

let mutable problems = 0

// A name is real if it is a simulation, a spec, a shared module, an invariants module,
// a generators module, or a mutation suite (possibly with or without its suffixes).
let isReal (name: string) =
  shared.Contains name
  || sims.Contains name
  || specs.Contains name
  || mutants.Contains name
  || sims.Contains (name + "Invariants")
  || sims.Contains (name + "Generators")
  || mutants.Contains (name + "MutationTests")

for name in Set.toList named do
  if not (isReal name) then
    printfn "PHANTOM: the document names `%s`, which is not a simulation, spec, invariant, generator or mutation suite" name
    problems <- problems + 1

// The counts, checked against what is on disk right now.
let simCount = (sims |> Set.count) + (specs |> Set.count)
let invCount =
  Directory.GetFiles(simDir, "*Invariants.fs").Length
let genCount =
  Directory.GetFiles(simDir, "*Generators.fs").Length
let mutCount = mutants |> Set.count

let expectedCounts =
  [ "simulations", simCount; "invariant modules", invCount
    "generator modules", genCount; "mutation suites", mutCount ]

for (label, n) in expectedCounts do
  // Only the FIRST count in the sentence carries `**`; the rest follow a comma
  // (`**42 simulations, 39 invariant modules, ...`), and the sentence wraps across lines.
  // So match the number and its label plainly, allowing whitespace, with no bolding anchor —
  // anchoring on `**` made the check unable to match its own document's text.
  let phrase = sprintf @"\b%d\s+%s" n (Regex.Escape label)
  if not (Regex.IsMatch(doc, phrase)) then
    printfn "STALE COUNT: the document does not say '%d %s' (it may still quote an older number)" n label
    problems <- problems + 1

printfn "on disk: %d simulations, %d invariant modules, %d generator modules, %d mutation suites"
  simCount invCount genCount mutCount

if problems = 0 then
  printfn "OK - every module the document names exists, and every count is current."
  exit 0
else
  printfn "%d problem(s) found." problems
  exit 1