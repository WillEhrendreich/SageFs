/// COH-1: `sagefs play` read a ledger file that nothing wrote. The `cohort-export` subcommand is the
/// writer: it reads the daemon's SQLite cohort ledger offline and writes the portable JSONL that
/// `CohortPlay.runPlay` replays. These tests drive the REAL SQLite port (a ledger written the way the
/// daemon writes it), the real `run` entry point, and the real `runPlay` on the exported file.
module SageFs.Tests.CohortExportCommandTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.CohortExportCommand
open SageFs.Features.CohortLedgerExport
open SageFs.Features.CohortLedgerSqlite

let private clock0 = DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
let private alice = MemberId.Minted "alice"
let private claimEntropy = [| 9uy; 9uy |]

/// Fold `decide` over real commands, so the ledger is one the reducer actually produced.
let private buildLedger (scope: CohortScope) (who: MemberId) : LedgerEntry<MemberId> list =
  let mutable seq = 0L
  let mutable state = CohortState.forScope scope
  let entries = ResizeArray()
  let apply (clock: DateTime) (entropy: byte[]) (command: CohortCommand<MemberId>) =
    match decide clock entropy state command with
    | Ok(newState, events, _effects) ->
      state <- newState
      entries.Add
        { Seq = LanguagePrimitives.Int64WithMeasure<ledgerSeq> seq
          Clock = clock
          Entropy = entropy
          Command = command
          Events = events }
      seq <- seq + 1L
    | Error e -> failwithf "buildLedger: command was refused: %A" e
  apply clock0 [||] (CohortCommand.Join(who, JoinableRole.Implementer, Some "sess-alice", scope))
  apply (clock0.AddMinutes 1.0) claimEntropy (CohortCommand.AcquireClaim(who, ClaimScope.File "Bar.fs", "implementing Bar", scope))
  List.ofSeq entries

/// A fresh data dir whose ledger holds `ledgers`, written through the SAME port the daemon uses.
let private withDataDir (ledgers: LedgerEntry<MemberId> list list) (body: string -> unit) : unit =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-cohort-export-%s" (Guid.NewGuid().ToString "N"))
  Directory.CreateDirectory dir |> ignore
  try
    match ledgers with
    | [] -> ()
    | _ ->
      let port = Sqlite.create (SageFs.LocalData.cohortLedgerPath dir)
      ledgers |> List.iter (List.iter port.Append)
    body dir
  finally
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
    try Directory.Delete(dir, true) with _ -> ()

let private outPath (dir: string) = Path.Combine(dir, "exported.ledger.jsonl")

/// What replaying `entries` directly says. The exported file must replay to exactly this.
let private expectedSummary (scope: CohortScope) (entries: LedgerEntry<MemberId> list) : string =
  let head = Cohort.replayHeadIn scope entries
  CohortPlay.renderPlaySummary entries.Length head (Cohort.project head [||])

/// Test 1's check, kept as one function so the twin below can aim the SAME check at a broken
/// exporter: replay the file and demand it names the member AND equals the direct replay (which
/// carries the claim id minted from entropy).
let private replayMatches (expected: string) (path: string) : Result<unit, string> =
  match CohortPlay.runPlay path with
  | Error e -> Error(sprintf "runPlay failed: %s" e)
  | Ok summary when not (summary.Contains "alice") -> Error "the replayed summary does not name alice"
  | Ok summary when summary <> expected -> Error "the replayed summary differs from replaying the ledger directly"
  | Ok _ -> Ok()

let private args (dir: string) (extra: string list) : string list =
  [ Flag.text Flag.DataDir; dir ] @ extra @ [ outPath dir ]

[<Tests>]
let cohortExportCommandTests =
  testList "cohort export" [

    test "an exported SQLite ledger replays through runPlay and names the member" {
      let entries = buildLedger CohortScope.Machine alice
      withDataDir [ entries ] (fun dir ->
        match run (args dir []) with
        | Error e -> failtestf "export failed: %s" (describe e)
        | Ok _ ->
          replayMatches (expectedSummary CohortScope.Machine entries) (outPath dir)
          |> Expect.isOk "the exported file replays to the same cohort")
    }

    test "an empty data dir is a typed error that names the ledger path it looked at" {
      withDataDir [] (fun dir ->
        let looked = SageFs.LocalData.cohortLedgerPath dir
        match run (args dir []) with
        | Ok _ -> failtest "expected an error: there is no ledger in an empty data dir"
        | Error(ExportError.NoLedgerFile lookedAt) ->
          lookedAt |> Expect.equal "the error carries the path it looked at" looked
          describe (ExportError.NoLedgerFile lookedAt) |> Expect.stringContains "the message says where it looked" looked
          File.Exists(outPath dir) |> Expect.isFalse "no output file is written when there is nothing to export"
          File.Exists looked |> Expect.isFalse "looking must not create a ledger"
        | Error other -> failtestf "expected NoLedgerFile, got %A" other)
    }

    test "two scopes in one ledger need --scope and the error lists both" {
      let machine = buildLedger CohortScope.Machine alice
      let repo = CohortScope.Repository "/repo/a"
      let repoEntries = buildLedger repo alice
      withDataDir [ machine; repoEntries ] (fun dir ->
        match run (args dir []) with
        | Ok _ -> failtest "expected an error: two scopes and no --scope must not pick one"
        | Error(ExportError.ScopeRequired available as e) ->
          available |> Expect.containsAll "both scopes are offered" [ CohortScope.Machine; repo ]
          let message = describe e
          message |> Expect.stringContains "lists the machine scope" (Scope.label CohortScope.Machine)
          message |> Expect.stringContains "lists the repository scope" (Scope.label repo)
          message |> Expect.stringContains "names the flag that fixes it" (Flag.text Flag.Scope)
          File.Exists(outPath dir) |> Expect.isFalse "nothing is written until the scope is chosen"
        | Error other -> failtestf "expected ScopeRequired, got %A" other

        // And choosing one works, by label and by the repository path.
        for chosen in [ Scope.label repo; "/repo/a" ] do
          match run (args dir [ Flag.text Flag.Scope; chosen ]) with
          | Error e -> failtestf "--scope %s failed: %s" chosen (describe e)
          | Ok _ ->
            importFromFile (outPath dir)
            |> Expect.equal "exactly the chosen scope's rows, entropy and all, and none of the machine scope's" (Ok repoEntries)
            // And the file plays: a repository-scoped cohort is the default a daemon writes, so `play`
            // must seed its replay at THAT scope, not at Machine, or the cohort comes back empty.
            replayMatches (expectedSummary repo repoEntries) (outPath dir)
            |> Expect.isOk "the repository-scoped file replays to the same cohort, member and claim included"
            let summary = CohortPlay.runPlay (outPath dir) |> Result.defaultValue ""
            summary |> Expect.stringContains "the member is named" "alice"
            summary |> Expect.stringContains "the claim is held" "holder=alice  scope=file Bar.fs")
    }

    test "a scope the ledger does not hold is refused with the scopes it does hold" {
      withDataDir [ buildLedger CohortScope.Machine alice ] (fun dir ->
        match run (args dir [ Flag.text Flag.Scope; "/nowhere" ]) with
        | Ok _ -> failtest "expected an error for a scope with no rows"
        | Error(ExportError.UnknownScope(requested, available)) ->
          requested |> Expect.equal "the error quotes what was asked for" "/nowhere"
          available |> Expect.equal "and what the ledger holds" [ CohortScope.Machine ]
        | Error other -> failtestf "expected UnknownScope, got %A" other)
    }

    test "the only scope is exported without --scope" {
      let entries = buildLedger CohortScope.Machine alice
      withDataDir [ entries ] (fun dir ->
        match renderExport { Scope = ScopeRequest.OnlyScope; DataDir = DataDirRequest.Explicit dir; OutputPath = outPath dir } (openLedger (SageFs.LocalData.cohortLedgerPath dir)) with
        | Error e -> failtestf "expected Ok: %s" (describe e)
        | Ok plan ->
          plan.ChosenScope |> Expect.equal "the one scope is chosen" CohortScope.Machine
          plan.Entries.Length |> Expect.equal "every row is in the plan" entries.Length)
    }

    test "TWIN: an exporter that drops entropy fails the replay check, so the check has teeth" {
      let entries = buildLedger CohortScope.Machine alice
      withDataDir [ entries ] (fun dir ->
        let broken = entries |> List.map (fun entry -> { entry with Entropy = [||] })
        let path = outPath dir
        exportToFile path broken
        match replayMatches (expectedSummary CohortScope.Machine entries) path with
        | Ok() -> failtest "a file with its entropy dropped replayed as the same cohort: the check cannot tell"
        | Error reason -> reason |> Expect.stringContains "it fails on the claim id minted from entropy" "differs")
    }

    testList "argument parsing" [
      test "the output path is required" {
        parseArgs [] |> Expect.equal "no output path" (Error(ExportError.Usage UsageProblem.MissingOutputPath))
      }
      test "flags and the output path parse in any order" {
        parseArgs [ Flag.text Flag.Scope; "repo:/r"; "out.jsonl"; Flag.text Flag.DataDir; "/d" ]
        |> Expect.equal "all three read"
             (Ok { Scope = ScopeRequest.Requested "repo:/r"; DataDir = DataDirRequest.Explicit "/d"; OutputPath = "out.jsonl" })
      }
      test "a flag with no value is refused, not swallowed" {
        parseArgs [ "out.jsonl"; Flag.text Flag.Scope ]
        |> Expect.equal "scope has no value" (Error(ExportError.Usage(UsageProblem.FlagWithoutValue Flag.Scope)))
      }
      test "an unknown flag and a second path are refused" {
        parseArgs [ "--wat"; "out.jsonl" ]
        |> Expect.equal "unknown flag" (Error(ExportError.Usage(UsageProblem.UnknownFlag "--wat")))
        parseArgs [ "a.jsonl"; "b.jsonl" ]
        |> Expect.equal "extra path" (Error(ExportError.Usage(UsageProblem.ExtraArgument "b.jsonl")))
      }
    ]
  ]
