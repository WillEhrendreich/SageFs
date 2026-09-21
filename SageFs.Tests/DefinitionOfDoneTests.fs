/// The release Definition-of-Done gate.
///
/// WHY THIS FILE IS SHAPED THE WAY IT IS. Until 2026-09-20 a `verified` row
/// needed only a non-empty `evidence` STRING. Any prose passed. Two rows
/// (HR-DASH-E2E, LT-DASH-E2E) therefore sat green for a week citing
/// `dashboard-browser-e2e`, a CI job deleted in d99c2fd4 on 2026-09-13 — the
/// gate structurally could not notice, because it never looked at anything but
/// the string's length.
///
/// Evidence is now a typed object the gate RESOLVES, fail-closed:
///
///   kind = "ci"        every cited test file must exist on disk; a file under
///                      SageFs.Tests/ must also be in SageFs.Tests.fsproj's
///                      compile list (an orphaned file is not a gate); the file
///                      must register itself for the cited runner; the runner
///                      flag must be dispatched by SageFs.Tests/Program.fs AND
///                      invoked by a `run` line in ci-pipeline.fsx; the cited
///                      stage must exist in ci-pipeline.fsx or as a named step
///                      in a workflow; and a run id or commit SHA must make it
///                      resolvable by someone else.
///
///   kind = "external"  only legal for a client the matrix declares this repo
///                      structurally cannot execute (`externalClients`). It is
///                      an ATTESTATION pinned to a full 40-hex foreign commit,
///                      not a verification, and the fence keeps that admission
///                      from spreading to clients whose tests do run here.
///
/// Anything else — a bare string, an unknown kind, an unresolvable field — is
/// an ERROR. Unverifiable evidence never passes.
///
/// WHERE RELEASE BLOCKING LIVES. `validateMatrix true` reports every deferred
/// row as blocking, and that projection is consumed by the two things that
/// actually gate a release: `dotnet run --project SageFs.Tests --
/// --release-readiness` (the `isReleaseReadiness` branch in Program.fs) and the publish workflow's
/// "Enforce release Definition of Done" step. Neither is weakened here. What
/// this suite asserts is that the MATRIX IS HONEST — every claim resolves, every
/// deferral is tracked and unexpired — plus, on synthetic matrices, that the
/// release projection really does block on a deferral. Whether the repo happens
/// to be shippable today is the release gate's verdict to deliver, not a unit
/// test's; encoding it here produced a sentinel that had to be rewritten in
/// both directions every time a row moved.
module SageFs.Tests.DefinitionOfDoneTests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let matrixPath = Path.Combine(repoRoot, "quality", "definition-of-done.json")

let requiredClients = set [ "dashboard"; "vscode"; "neovim" ]
let requiredCapabilities = set [ "hot-reload"; "live-testing"; "friction" ]
let allowedStatuses = set [ "verified"; "deferred"; "not-applicable" ]

/// Everything the gate needs to look up outside the matrix file. Injected so
/// the resolution rules can be proven on synthetic inputs without a checkout,
/// and so the real probe reads each source file exactly once.
type RepoProbe = {
  /// Does this repo-relative path exist on disk?
  FileExists: string -> bool
  /// Is this repo-relative path in SageFs.Tests.fsproj's <Compile> list?
  CompiledInTestProject: string -> bool
  /// Does the test file register itself for this runner flag?
  FileRegistersRunner: string -> string -> bool
  /// Does SageFs.Tests/Program.fs dispatch this runner flag?
  RunnerDispatched: string -> bool
  /// Does a `run` line in ci-pipeline.fsx actually invoke this runner flag?
  RunnerInvokedByCi: string -> bool
  /// Does this stage/step name exist in ci-pipeline.fsx or a workflow?
  StageExists: string -> bool
}

module RepoProbe =

  let private readOrEmpty (path: string) =
    match File.Exists path with
    | true -> File.ReadAllText path
    | false -> ""

  /// The probe that reads this checkout. Every source is read lazily and once.
  let real () =
    let ciPipeline = lazy (readOrEmpty (Path.Combine(repoRoot, "ci-pipeline.fsx")))
    let programFs = lazy (readOrEmpty (Path.Combine(repoRoot, "SageFs.Tests", "Program.fs")))
    let testsProj = lazy (readOrEmpty (Path.Combine(repoRoot, "SageFs.Tests", "SageFs.Tests.fsproj")))
    let workflows =
      lazy (
        let dir = Path.Combine(repoRoot, ".github", "workflows")
        match Directory.Exists dir with
        | false -> ""
        | true ->
          Directory.GetFiles(dir, "*.yml")
          |> Array.map readOrEmpty
          |> String.concat "\n")
    let fileText =
      let cache = Collections.Generic.Dictionary<string, string>()
      fun (relativePath: string) ->
        match cache.TryGetValue relativePath with
        | true, text -> text
        | _ ->
          let text = readOrEmpty (Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))
          cache[relativePath] <- text
          text
    { FileExists = fun relativePath ->
        File.Exists(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))
      CompiledInTestProject = fun relativePath ->
        let name = Path.GetFileName relativePath
        testsProj.Value.Contains(sprintf "<Compile Include=\"%s\"" name)
      FileRegistersRunner = fun relativePath runner ->
        let text = fileText relativePath
        match runner with
        // The host suite is selected by reference through the Integration
        // registry, not by a flag literal, so its registration helpers are the
        // thing to look for.
        | "--integration-host" ->
          text.Contains "Integration.hostList"
          || text.Contains "Integration.hostCase"
          || text.Contains "Integration.Host"
        | flag -> text.Contains(sprintf "Integration.Dedicated \"%s\"" flag)
      RunnerDispatched = fun runner -> programFs.Value.Contains(sprintf "\"%s\"" runner)
      // Every test-assembly invocation in CI is a ledgered `testTier` step
      // (TrustSignalTests enforces that), so ask the same parser the trust
      // report's wiring test uses. The old per-line `run ... <flag>` scan broke
      // the moment a step wrapped onto several lines.
      RunnerInvokedByCi = fun runner ->
        TestInfrastructure.TrustSignal.pipelineTierArgs ciPipeline.Value
        |> List.exists (fun args -> TestInfrastructure.TrustSignal.tierOfArgs args = runner)
      StageExists = fun name ->
        ciPipeline.Value.Contains(sprintf "stage \"%s\"" name)
        || workflows.Value.Contains(sprintf "name: %s" name) }

let stringProperty (name: string) (row: JsonElement) =
  match row.TryGetProperty name with
  | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
  | _ -> ""

let private stringArrayProperty (name: string) (row: JsonElement) =
  match row.TryGetProperty name with
  | true, value when value.ValueKind = JsonValueKind.Array ->
    value.EnumerateArray()
    |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.String)
    |> Seq.map (fun e -> e.GetString())
    |> Seq.toList
  | _ -> []

let private runIdPattern = Regex(@"^\d{8,}$")
let private shortShaPattern = Regex(@"^[0-9a-f]{7,40}$")
let private fullShaPattern = Regex(@"^[0-9a-f]{40}$")

/// Resolve one `kind: "ci"` evidence object. Every branch that cannot prove the
/// claim adds an error — there is no "assume it is fine" path.
let private validateCiEvidence (probe: RepoProbe) (id: string) (evidence: JsonElement) (errors: ResizeArray<string>) =
  let tests = stringArrayProperty "tests" evidence
  let runner = stringProperty "runner" evidence
  let stage = stringProperty "stage" evidence
  let run = stringProperty "run" evidence
  let commit = stringProperty "commit" evidence
  let summary = stringProperty "summary" evidence

  match tests with
  | [] -> errors.Add(sprintf "%s: ci evidence must cite at least one test file" id)
  | paths ->
    for path in paths do
      match probe.FileExists path with
      | false -> errors.Add(sprintf "%s: cited test file does not exist: %s" id path)
      | true ->
        match path.StartsWith "SageFs.Tests/" && not (probe.CompiledInTestProject path) with
        | true -> errors.Add(sprintf "%s: cited test file is not in SageFs.Tests.fsproj's compile list: %s" id path)
        | false -> ()
        match String.IsNullOrWhiteSpace runner || probe.FileRegistersRunner path runner with
        | true -> ()
        | false -> errors.Add(sprintf "%s: %s does not register itself for %s" id path runner)

  match String.IsNullOrWhiteSpace runner with
  | true -> errors.Add(sprintf "%s: ci evidence must name the test runner entry point" id)
  | false ->
    match probe.RunnerDispatched runner with
    | true -> ()
    | false -> errors.Add(sprintf "%s: SageFs.Tests/Program.fs does not dispatch %s" id runner)
    match probe.RunnerInvokedByCi runner with
    | true -> ()
    | false -> errors.Add(sprintf "%s: no ci-pipeline.fsx stage runs %s, so this evidence has never executed in CI" id runner)

  match String.IsNullOrWhiteSpace stage with
  | true -> errors.Add(sprintf "%s: ci evidence must name the pipeline stage that runs it" id)
  | false ->
    match probe.StageExists stage with
    | true -> ()
    | false -> errors.Add(sprintf "%s: cited CI stage does not exist: %s" id stage)

  match runIdPattern.IsMatch run || shortShaPattern.IsMatch commit with
  | true -> ()
  | false -> errors.Add(sprintf "%s: ci evidence needs a run id or commit SHA to resolve" id)

  match summary.Contains("green locally", StringComparison.OrdinalIgnoreCase) with
  | true -> errors.Add(sprintf "%s: 'green locally' is not evidence" id)
  | false -> ()

/// Resolve one `kind: "external"` evidence object. This repo cannot re-run it,
/// so the rules are about the fence and the pin, not about execution.
let private validateExternalEvidence
  (externalClients: Set<string>)
  (today: DateOnly)
  (id: string)
  (client: string)
  (evidence: JsonElement)
  (errors: ResizeArray<string>)
  =
  match externalClients |> Set.contains client with
  | true -> ()
  | false ->
    errors.Add(
      sprintf
        "%s: client '%s' is not declared in externalClients, so its evidence must be kind 'ci' — external attestation is only for clients this repo cannot execute"
        id
        client)

  match String.IsNullOrWhiteSpace(stringProperty "repo" evidence) with
  | true -> errors.Add(sprintf "%s: external evidence must name the repository that ran it" id)
  | false -> ()

  match stringArrayProperty "tests" evidence with
  | [] -> errors.Add(sprintf "%s: external evidence must cite at least one test path" id)
  | _ -> ()

  let commit = stringProperty "commit" evidence
  match fullShaPattern.IsMatch commit with
  | true -> ()
  | false ->
    errors.Add(sprintf "%s: external evidence needs a full 40-character commit SHA to pin the foreign revision" id)

  match DateOnly.TryParse(stringProperty "attestedAt" evidence) with
  | true, date when date <= today -> ()
  | _ -> errors.Add(sprintf "%s: external evidence needs an attestedAt date that is not in the future" id)

  match String.IsNullOrWhiteSpace(stringProperty "summary" evidence) with
  | true -> errors.Add(sprintf "%s: external evidence must summarise what was attested" id)
  | false -> ()

let private validateEvidence
  (probe: RepoProbe)
  (evidenceKinds: Set<string>)
  (externalClients: Set<string>)
  (today: DateOnly)
  (id: string)
  (client: string)
  (row: JsonElement)
  (errors: ResizeArray<string>)
  =
  match row.TryGetProperty "evidence" with
  | false, _ -> errors.Add(sprintf "%s needs executable evidence" id)
  | true, evidence ->
    match evidence.ValueKind with
    | JsonValueKind.Object ->
      let kind = stringProperty "kind" evidence
      match evidenceKinds |> Set.contains kind with
      | false -> errors.Add(sprintf "%s: unknown evidence kind '%s'" id kind)
      | true ->
        match kind with
        | "ci" -> validateCiEvidence probe id evidence errors
        | "external" -> validateExternalEvidence externalClients today id client evidence errors
        | _ -> errors.Add(sprintf "%s: evidence kind '%s' has no resolution rule" id kind)
    | _ ->
      errors.Add(
        sprintf
          "%s: evidence must be a typed object the gate can resolve, not free text (free text is what let a deleted CI job sit green for a week)"
          id)

let validateMatrixWith (probe: RepoProbe) (releaseReady: bool) (today: DateOnly) (json: string) =
  try
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    let rows = root.GetProperty("rows").EnumerateArray() |> Seq.toArray
    let errors = ResizeArray<string>()
    let setFrom (name: string) =
      match root.TryGetProperty name with
      | true, value when value.ValueKind = JsonValueKind.Array ->
        value.EnumerateArray()
        |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.String)
        |> Seq.map (fun e -> e.GetString())
        |> Set.ofSeq
      | _ -> Set.empty
    let evidenceKinds = setFrom "evidenceKinds"
    let externalClients = setFrom "externalClients"
    match evidenceKinds |> Set.isEmpty with
    | true -> errors.Add "matrix must declare evidenceKinds"
    | false -> ()
    let ids = rows |> Array.map (stringProperty "id")
    ids
    |> Array.countBy id
    |> Array.filter (fun (id, count) -> String.IsNullOrWhiteSpace id || count > 1)
    |> Array.iter (fun (id, _) -> errors.Add(sprintf "duplicate or blank id: %s" id))
    let combinations =
      rows
      |> Array.map (fun row -> stringProperty "capability" row, stringProperty "client" row)
      |> Set.ofArray
    for capability in requiredCapabilities do
      for client in requiredClients do
        match combinations |> Set.contains (capability, client) with
        | true -> ()
        | false -> errors.Add(sprintf "missing obligation: %s/%s" capability client)
    for row in rows do
      let id = stringProperty "id" row
      let client = stringProperty "client" row
      let status = stringProperty "status" row
      match allowedStatuses |> Set.contains status with
      | true -> ()
      | false -> errors.Add(sprintf "%s has unknown status %s" id status)
      match status with
      | "verified" -> validateEvidence probe evidenceKinds externalClients today id client row errors
      | "deferred" ->
        let hasIssue =
          match row.TryGetProperty "issue" with
          | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32() > 0
          | _ -> false
        let expiry = stringProperty "expires" row
        match hasIssue with
        | true -> ()
        | false -> errors.Add(sprintf "%s needs a tracking issue" id)
        match DateOnly.TryParse expiry with
        | true, date when date >= today -> ()
        | _ -> errors.Add(sprintf "%s has an invalid or expired deferral" id)
        match String.IsNullOrWhiteSpace(stringProperty "reason" row) with
        | true -> errors.Add(sprintf "%s needs a deferral reason naming what would re-verify it" id)
        | false -> ()
        match releaseReady with
        | true -> errors.Add(sprintf "%s blocks release readiness" id)
        | false -> ()
      | "not-applicable" ->
        match String.IsNullOrWhiteSpace(stringProperty "reason" row) with
        | true -> errors.Add(sprintf "%s needs a not-applicable reason" id)
        | false -> ()
      | _ -> ()
    errors |> Seq.toList
  with ex ->
    [ sprintf "invalid matrix: %s" ex.Message ]

/// The entry point Program.fs's `--release-readiness` and the publish
/// workflow's gate resolve against this checkout.
let validateMatrix (releaseReady: bool) (today: DateOnly) (json: string) =
  validateMatrixWith (RepoProbe.real ()) releaseReady today json

// ── Synthetic fixtures, for proving the rules rather than the repo's mood ──

let private probeThatResolvesEverything = {
  FileExists = fun _ -> true
  CompiledInTestProject = fun _ -> true
  FileRegistersRunner = fun _ _ -> true
  RunnerDispatched = fun _ -> true
  RunnerInvokedByCi = fun _ -> true
  StageExists = fun _ -> true
}

let private syntheticMatrix (rowsJson: string) =
  sprintf
    """{ "schemaVersion": 2, "statuses": ["verified","deferred","not-applicable"],
         "evidenceKinds": ["ci","external"], "externalClients": ["neovim"],
         "rows": [ %s ] }"""
    rowsJson

/// One `verified`/`ci` row per required (capability, client) pair except the
/// one the caller is testing, so structural-completeness errors never mask the
/// rule under test.
let private ciRow (id: string) (capability: string) (client: string) =
  sprintf
    """{ "id": "%s", "capability": "%s", "client": "%s", "status": "verified",
         "evidence": { "kind": "ci", "tests": ["SageFs.Tests/Some.fs"], "runner": "--integration-host",
                       "stage": "integration host", "run": "35523239725", "summary": "ok" } }"""
    id
    capability
    client

let private externalRow (id: string) (capability: string) (client: string) =
  sprintf
    """{ "id": "%s", "capability": "%s", "client": "%s", "status": "verified",
         "evidence": { "kind": "external", "repo": "owner/repo", "tests": ["spec/e2e.lua"],
                       "commit": "ce2f615aef7d1edcb9d6007020aa7f10d26ec341", "attestedAt": "2026-09-04",
                       "summary": "attested" } }"""
    id
    capability
    client

/// A complete matrix whose every row resolves, with `replacement` substituted
/// for the row carrying `replacedId`.
let private matrixReplacing (replacedId: string) (replacement: string) =
  let baseRows =
    [ ciRow "HR-DASH" "hot-reload" "dashboard"
      ciRow "HR-VSC" "hot-reload" "vscode"
      externalRow "HR-NVIM" "hot-reload" "neovim"
      ciRow "LT-DASH" "live-testing" "dashboard"
      ciRow "LT-VSC" "live-testing" "vscode"
      externalRow "LT-NVIM" "live-testing" "neovim"
      ciRow "FR-DASH" "friction" "dashboard"
      ciRow "FR-VSC" "friction" "vscode"
      externalRow "FR-NVIM" "friction" "neovim" ]
  baseRows
  |> List.map (fun row ->
    match replacedId <> "" && row.Contains(sprintf "\"id\": \"%s\"" replacedId) with
    | true -> replacement
    | false -> row)
  |> String.concat ",\n"
  |> syntheticMatrix

/// The synthetic matrix in which every row resolves — the control case for the
/// probe-failure tests below.
let private fullyResolvingMatrix = matrixReplacing "" ""

let private today = DateOnly.FromDateTime DateTime.UtcNow

let private checkSynthetic probe releaseReady json = validateMatrixWith probe releaseReady today json

let private errorsMentioning (fragment: string) (errors: string list) =
  errors |> List.filter (fun e -> e.Contains fragment)

[<Tests>]
let definitionOfDoneTests =
  testList "Definition of Done matrix" [

    testCase "WHY — development matrix is structurally complete because every client and capability needs an owned obligation" <| fun () ->
      File.ReadAllText matrixPath
      |> validateMatrix false today
      |> Expect.isEmpty "development matrix should be structurally valid"

    testCase "WHY — every verified row in the live matrix resolves against this checkout: the cited files exist and compile, the cited runner is dispatched AND invoked by ci-pipeline.fsx, and the cited stage exists" <| fun () ->
      // This is the check that would have caught HR-DASH-E2E/LT-DASH-E2E citing
      // dashboard-browser-e2e for the seven days after d99c2fd4 deleted it.
      let resolutionFailures =
        [ "cited test file does not exist"
          "compile list"
          "does not register itself"
          "does not dispatch"
          "has never executed in CI"
          "cited CI stage does not exist"
          "run id or commit SHA"
          "full 40-character commit SHA"
          "not declared in externalClients"
          "unknown evidence kind"
          "typed object" ]
      let errors = File.ReadAllText matrixPath |> validateMatrix false today
      resolutionFailures
      |> List.collect (fun fragment -> errorsMentioning fragment errors)
      |> Expect.isEmpty "no verified row may cite something this checkout cannot resolve"

    testCase "WHY — a deferred row blocks release readiness, because that projection is what --release-readiness and the publish gate consume" <| fun () ->
      let deferred =
        """{ "id": "LT-DASH", "capability": "live-testing", "client": "dashboard", "status": "deferred",
             "issue": 128, "expires": "2099-01-01", "reason": "runner runs in no pipeline" }"""
      matrixReplacing "LT-DASH" deferred
      |> checkSynthetic probeThatResolvesEverything true
      |> errorsMentioning "blocks release readiness"
      |> Expect.isNonEmpty "a deferred obligation must block the release projection"

    testCase "WHY — a fully verified matrix does not block release readiness, so the projection is a real signal and not a constant" <| fun () ->
      fullyResolvingMatrix
      |> checkSynthetic probeThatResolvesEverything true
      |> Expect.isEmpty "a matrix with nothing deferred must produce no release-readiness errors"

    testCase "WHY — free-text evidence is rejected outright, because a non-empty string is exactly what let a deleted CI job pass for a week" <| fun () ->
      let freeText =
        """{ "id": "HR-DASH", "capability": "hot-reload", "client": "dashboard", "status": "verified",
             "evidence": "HotReloadBrowserTests.fs — CI dashboard-browser-e2e green at d3647cf" }"""
      matrixReplacing "HR-DASH" freeText
      |> checkSynthetic probeThatResolvesEverything false
      |> errorsMentioning "typed object"
      |> Expect.isNonEmpty "prose evidence must fail the gate"

    testCase "WHY — a runner no ci-pipeline.fsx stage invokes fails the gate, even though Program.fs dispatches it (the --integration-hr/--integration-lt class)" <| fun () ->
      let probe = { probeThatResolvesEverything with RunnerInvokedByCi = fun _ -> false }
      fullyResolvingMatrix
      |> checkSynthetic probe false
      |> errorsMentioning "has never executed in CI"
      |> Expect.isNonEmpty "a dark runner must not back a verified row"

    testCase "WHY — a cited stage that no longer exists fails the gate (the dashboard-browser-e2e deletion)" <| fun () ->
      let probe = { probeThatResolvesEverything with StageExists = fun _ -> false }
      fullyResolvingMatrix
      |> checkSynthetic probe false
      |> errorsMentioning "cited CI stage does not exist"
      |> Expect.isNonEmpty "a deleted stage must not back a verified row"

    testCase "WHY — a cited test file that does not exist fails the gate" <| fun () ->
      let probe = { probeThatResolvesEverything with FileExists = fun _ -> false }
      fullyResolvingMatrix
      |> checkSynthetic probe false
      |> errorsMentioning "cited test file does not exist"
      |> Expect.isNonEmpty "a missing test file must not back a verified row"

    testCase "WHY — a cited test file that is not in the compile list fails the gate, because an orphaned file is not a gate" <| fun () ->
      let probe = { probeThatResolvesEverything with CompiledInTestProject = fun _ -> false }
      fullyResolvingMatrix
      |> checkSynthetic probe false
      |> errorsMentioning "compile list"
      |> Expect.isNonEmpty "an uncompiled test file must not back a verified row"

    testCase "WHY — a cited test file that does not register itself for the cited runner fails the gate" <| fun () ->
      let probe = { probeThatResolvesEverything with FileRegistersRunner = fun _ _ -> false }
      fullyResolvingMatrix
      |> checkSynthetic probe false
      |> errorsMentioning "does not register itself"
      |> Expect.isNonEmpty "evidence must tie the file to the runner that selects it"

    testCase "WHY — external attestation is fenced to declared externalClients, so 'we cannot check it' can never spread to a client whose tests run here" <| fun () ->
      let smuggled = externalRow "FR-VSC" "friction" "vscode"
      matrixReplacing "FR-VSC" smuggled
      |> checkSynthetic probeThatResolvesEverything false
      |> errorsMentioning "not declared in externalClients"
      |> Expect.isNonEmpty "only a structurally unrunnable client may use external attestation"

    testCase "WHY — external attestation must pin a FULL commit SHA, because an abbreviation is not a revision anyone can resolve later" <| fun () ->
      let abbreviated =
        """{ "id": "HR-NVIM", "capability": "hot-reload", "client": "neovim", "status": "verified",
             "evidence": { "kind": "external", "repo": "owner/repo", "tests": ["spec/e2e.lua"],
                           "commit": "ce2f615", "attestedAt": "2026-09-04", "summary": "attested" } }"""
      matrixReplacing "HR-NVIM" abbreviated
      |> checkSynthetic probeThatResolvesEverything false
      |> errorsMentioning "full 40-character commit SHA"
      |> Expect.isNonEmpty "an abbreviated foreign SHA must not back a verified row"

    testCase "WHY — an unknown evidence kind fails closed rather than falling through to a pass" <| fun () ->
      let unknown =
        """{ "id": "HR-DASH", "capability": "hot-reload", "client": "dashboard", "status": "verified",
             "evidence": { "kind": "vibes", "summary": "it felt right" } }"""
      matrixReplacing "HR-DASH" unknown
      |> checkSynthetic probeThatResolvesEverything false
      |> errorsMentioning "unknown evidence kind"
      |> Expect.isNonEmpty "an unrecognised evidence kind must be an error, never a pass"

    testCase "WHY — a deferral without a reason fails, because a parked obligation with no re-verification path is an abandonment" <| fun () ->
      let reasonless =
        """{ "id": "LT-DASH", "capability": "live-testing", "client": "dashboard", "status": "deferred",
             "issue": 128, "expires": "2099-01-01" }"""
      matrixReplacing "LT-DASH" reasonless
      |> checkSynthetic probeThatResolvesEverything false
      |> errorsMentioning "deferral reason"
      |> Expect.isNonEmpty "a deferral must say what would re-verify it"

    testCase "WHY — 'green locally' is still never evidence" <| fun () ->
      let local =
        """{ "id": "HR-DASH", "capability": "hot-reload", "client": "dashboard", "status": "verified",
             "evidence": { "kind": "ci", "tests": ["SageFs.Tests/Some.fs"], "runner": "--integration-host",
                           "stage": "integration host", "run": "35523239725", "summary": "green locally" } }"""
      matrixReplacing "HR-DASH" local
      |> checkSynthetic probeThatResolvesEverything false
      |> errorsMentioning "'green locally' is not evidence"
      |> Expect.isNonEmpty "locally-observed greenness is not something anyone else can open"

    testCase "WHY — ci evidence with neither a run id nor a commit SHA fails, because evidence nobody else can open is not evidence (roast-4 #12)" <| fun () ->
      let unresolvable =
        """{ "id": "HR-DASH", "capability": "hot-reload", "client": "dashboard", "status": "verified",
             "evidence": { "kind": "ci", "tests": ["SageFs.Tests/Some.fs"], "runner": "--integration-host",
                           "stage": "integration host", "summary": "it passed" } }"""
      matrixReplacing "HR-DASH" unresolvable
      |> checkSynthetic probeThatResolvesEverything false
      |> errorsMentioning "run id or commit SHA"
      |> Expect.isNonEmpty "a verified row must be resolvable by someone else"

    testCase "WHY — the probe really reads this checkout, so a pipeline rename cannot quietly turn every resolution check into a no-op that passes everything" <| fun () ->
      // Deliberately pins only facts that should hold for as long as CI exists,
      // plus one negative that must never come back. An earlier draft pinned
      // "--integration-hr is invoked by nothing", which was true that morning and
      // false by the afternoon — a gate must not encode today's defects.
      let probe = RepoProbe.real ()
      probe.RunnerInvokedByCi "--integration-host"
      |> Expect.isTrue "ci-pipeline.fsx must be seen to run the integration-host suite"
      probe.RunnerInvokedByCi "--integration-browser"
      |> Expect.isTrue "ci-pipeline.fsx must be seen to run the dashboard browser journeys"
      probe.RunnerInvokedByCi "--no-such-runner-flag"
      |> Expect.isFalse "a runner no pipeline line mentions must not resolve"
      probe.StageExists "integration host"
      |> Expect.isTrue "the integration host stage must exist in ci-pipeline.fsx"
      probe.StageExists "dashboard-browser-e2e"
      |> Expect.isFalse "dashboard-browser-e2e was deleted in d99c2fd4 and must never resolve again"
      probe.FileExists "SageFs.Tests/DefinitionOfDoneTests.fs"
      |> Expect.isTrue "the probe must resolve a file that plainly exists"
      probe.FileExists "SageFs.Tests/ThisFileDoesNotExist.fs"
      |> Expect.isFalse "the probe must not resolve a file that does not exist"
  ]
