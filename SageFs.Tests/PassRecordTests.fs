/// Same-commit tier reuse (build/PassRecord.fs): the key, the decision, the record format, the hash of what a tier
/// loads, and the store. The simulation in PassRecordSimDstTests.fs folds the same functions through seeded histories.
module SageFs.Tests.PassRecordTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Build

let inputs : PassInputs =
  { Sha = "8f4a237c48d94b65493e74aed111d45076ce0765"
    Tier = "--integration-host[2/5]"
    Args = "--integration-host --shard 2/5 --summary"
    Framework = "net11.0"
    Sdk = "11.0.100-rc.1.26425.128"
    TestAssembly = "aa"
    Closure = "bb"
    Partition = "cc" }

let record : PassRecord =
  PassRecord.make inputs "2026-10-04T17:00:00-05:00" 497.3 "Trusted" ("46", "46", "46", "0", "0", "0") "every registered test ran and passed"
    "{\"Tier\":\"--integration-host[2/5]\",\"Verdict\":\"Trusted\"}"

let withTempDirectory (body: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-pass-" + Guid.NewGuid().ToString "N")
  Directory.CreateDirectory dir |> ignore
  try body dir
  finally (try Directory.Delete(dir, true) with _ -> ())

let decisionTests =
  testList "Pass record decision" [
    testCase "an exact, green record on a clean tree is reused" <| fun _ ->
      PassRecord.decide Freshness.Reuse (Store.At "s") TreeState.Clean Eligibility.Eligible inputs (Stored.Present record)
      |> Expect.equal "the record is taken" (Decision.ReuseRecord record)

    testCase "every reason to run is its own answer, and each is checked before the record is trusted" <| fun _ ->
      let decide freshness store tree eligibility stored = PassRecord.decide freshness store tree eligibility inputs stored
      decide Freshness.Fresh (Store.At "s") TreeState.Clean Eligibility.Eligible (Stored.Present record)
      |> Expect.equal "--fresh" (Decision.RunTier RunBecause.ForcedFresh)
      decide Freshness.Reuse Store.Disabled TreeState.Clean Eligibility.Eligible (Stored.Present record)
      |> Expect.equal "no store" (Decision.RunTier RunBecause.NoStore)
      decide Freshness.Reuse (Store.At "s") TreeState.Dirty Eligibility.Eligible (Stored.Present record)
      |> Expect.equal "dirty tree" (Decision.RunTier RunBecause.DirtyTree)
      decide Freshness.Reuse (Store.At "s") TreeState.Clean (Eligibility.Ineligible "its own bar") (Stored.Present record)
      |> Expect.equal "ineligible" (Decision.RunTier(RunBecause.NotEligible "its own bar"))
      decide Freshness.Reuse (Store.At "s") TreeState.Clean Eligibility.Eligible Stored.Absent
      |> Expect.equal "no record" (Decision.RunTier RunBecause.NoRecord)
      decide Freshness.Reuse (Store.At "s") TreeState.Clean Eligibility.Eligible (Stored.Unreadable "cut short")
      |> Expect.equal "unreadable" (Decision.RunTier(RunBecause.UnreadableRecord "cut short"))

    testCase "a record that was not fully green is never reused" <| fun _ ->
      let notGreen = { record with Verdict = "TestsFailed"; Failed = "1" }
      PassRecord.decide Freshness.Reuse (Store.At "s") TreeState.Clean Eligibility.Eligible inputs (Stored.Present notGreen)
      |> Expect.equal "red stays red" (Decision.RunTier(RunBecause.NotGreen "TestsFailed"))
      let errored = { record with Errored = "2" }
      match PassRecord.decide Freshness.Reuse (Store.At "s") TreeState.Clean Eligibility.Eligible inputs (Stored.Present errored) with
      | Decision.ReuseRecord _ -> failtest "a record with errored cases was reused"
      | Decision.RunTier _ -> ()

    testProperty "any one input that differs from the record's reruns the tier, and the answer names it" <|
      fun (index: byte) (suffix: NonEmptyString) ->
        let fields = PassRecord.fieldsOf inputs
        let i = int index % fields.Length
        let changed =
          match fst fields[i] with
          | "sha" -> { inputs with Sha = inputs.Sha + suffix.Get }
          | "tier" -> { inputs with Tier = inputs.Tier + suffix.Get }
          | "args" -> { inputs with Args = inputs.Args + suffix.Get }
          | "framework" -> { inputs with Framework = inputs.Framework + suffix.Get }
          | "sdk" -> { inputs with Sdk = inputs.Sdk + suffix.Get }
          | "testAssembly" -> { inputs with TestAssembly = inputs.TestAssembly + suffix.Get }
          | "closure" -> { inputs with Closure = inputs.Closure + suffix.Get }
          | _ -> { inputs with Partition = inputs.Partition + suffix.Get }
        match PassRecord.decide Freshness.Reuse (Store.At "s") TreeState.Clean Eligibility.Eligible changed (Stored.Present record) with
        | Decision.RunTier(RunBecause.InputsChanged names) -> names = [ fst fields[i] ]
        | _ -> false

    testCase "a record whose key is not the hash of its own inputs is unreadable, not reused" <| fun _ ->
      let forged = { record with Key = PassRecord.key { inputs with Closure = "other" } }
      match PassRecord.decide Freshness.Reuse (Store.At "s") TreeState.Clean Eligibility.Eligible inputs (Stored.Present forged) with
      | Decision.RunTier(RunBecause.UnreadableRecord _) -> ()
      | other -> failtestf "expected an unreadable record, got %A" other

    testCase "the trust table says a reused tier was reused, and when" <| fun _ ->
      PassRecord.reusedVerdict record |> Expect.equal "never silent" "Trusted (reused from 2026-10-04T17:00:00-05:00)"
  ]

let formatTests =
  testList "Pass record format" [
    testCase "the key is a SHA-256 of the canonical inputs and is stable" <| fun _ ->
      PassRecord.key inputs |> Expect.hasLength "64 hex characters" 64
      PassRecord.key inputs |> Expect.equal "deterministic" (PassRecord.key { inputs with Sha = inputs.Sha })

    testProperty "a record round-trips through its text" <|
      fun (a: NonEmptyString) (b: NonEmptyString) (seconds: NormalFloat) ->
        let clean (s: string) = String(s.ToCharArray() |> Array.filter (fun c -> c <> '\n' && c <> '\r'))
        let r =
          PassRecord.make { inputs with Args = clean a.Get; Partition = clean b.Get } "2026-10-04T17:00:00-05:00" (abs seconds.Get) "Trusted"
            ("1", "2", "3", "0", "0", "4") (clean b.Get) (clean a.Get)
        PassRecord.parse (PassRecord.serialize r) = Result.Ok r

    testCase "a record cut at ANY point is unreadable, never a smaller record" <| fun _ ->
      let text = PassRecord.serialize record
      for cut in 0 .. text.Length - 2 do
        match PassRecord.parse (text.Substring(0, cut)) with
        | Result.Error _ -> ()
        | Result.Ok parsed -> failtestf "a cut at %d of %d parsed as a record: %A" cut text.Length parsed

    testCase "an edited record is unreadable: the end line covers every line above it" <| fun _ ->
      let text = PassRecord.serialize record
      let edited = text.Replace("verdict Trusted", "verdict Truste2")
      match PassRecord.parse edited with
      | Result.Error reason -> reason |> Expect.stringContains "the end line is what caught it" "end line"
      | Result.Ok _ -> failtest "an edited record was read"

    testCase "another format version is unreadable" <| fun _ ->
      PassRecord.parse "sagefs-pass-record 2\nkey x\n" |> Expect.isError "a record of another version is not this one"
  ]

let closureTests =
  testList "Pass record closure hash" [
    testCase "it changes with one byte of one assembly, an added or removed assembly, and a rename" <| fun _ ->
      withTempDirectory (fun root ->
        let bin = Path.Combine(root, "bin")
        Directory.CreateDirectory bin |> ignore
        File.WriteAllBytes(Path.Combine(bin, "A.dll"), [| 1uy; 2uy; 3uy |])
        File.WriteAllBytes(Path.Combine(bin, "B.dll"), [| 4uy; 5uy |])
        let hash () = PassRecord.closureHash root [ "bin" ]
        let baseline = hash ()
        hash () |> Expect.equal "stable" baseline
        File.WriteAllBytes(Path.Combine(bin, "B.dll"), [| 4uy; 6uy |])
        hash () |> Expect.notEqual "one byte" baseline
        File.WriteAllBytes(Path.Combine(bin, "B.dll"), [| 4uy; 5uy |])
        hash () |> Expect.equal "restored" baseline
        File.WriteAllBytes(Path.Combine(bin, "C.dll"), [| 9uy |])
        hash () |> Expect.notEqual "added" baseline
        File.Delete(Path.Combine(bin, "C.dll"))
        File.Move(Path.Combine(bin, "B.dll"), Path.Combine(bin, "D.dll"))
        hash () |> Expect.notEqual "renamed" baseline)

    testCase "files that are not assemblies, and downloaded tooling, are not part of it" <| fun _ ->
      withTempDirectory (fun root ->
        let bin = Path.Combine(root, "bin")
        Directory.CreateDirectory(Path.Combine(bin, ".playwright")) |> ignore
        File.WriteAllBytes(Path.Combine(bin, "A.dll"), [| 1uy |])
        let baseline = PassRecord.closureHash root [ "bin" ]
        File.WriteAllText(Path.Combine(bin, "notes.txt"), "x")
        File.WriteAllText(Path.Combine(bin, "A.pdb"), "x")
        File.WriteAllBytes(Path.Combine(bin, ".playwright", "driver.dll"), [| 7uy |])
        PassRecord.closureHash root [ "bin" ] |> Expect.equal "unchanged" baseline)

    testCase "a directory that is not there contributes nothing, so a missing build changes the hash only by being missing" <| fun _ ->
      withTempDirectory (fun root ->
        PassRecord.closureHash root [ "nowhere" ] |> Expect.equal "empty closure" (PassRecord.closureHash root []))
  ]

let storeTests =
  testList "Pass record store" [
    testCase "a record is written whole, read back, and leaves no temp file" <| fun _ ->
      withTempDirectory (fun dir ->
        let path = PassRecord.recordPath dir inputs.Sha "tier-2of5"
        PassRecord.read path |> Expect.equal "nothing yet" Stored.Absent
        PassRecord.write path record
        PassRecord.read path |> Expect.equal "what was written" (Stored.Present record)
        Directory.GetFiles(Path.GetDirectoryName path, "*.tmp") |> Expect.isEmpty "no half-written file is left")

    testCase "a damaged file is unreadable, not absent and not a record" <| fun _ ->
      withTempDirectory (fun dir ->
        let path = PassRecord.recordPath dir inputs.Sha "tier-2of5"
        PassRecord.write path record
        File.WriteAllText(path, (File.ReadAllText path).Substring(0, 40))
        match PassRecord.read path with
        | Stored.Unreadable _ -> ()
        | other -> failtestf "expected unreadable, got %A" other)

    testCase "only the newest commits' records are kept" <| fun _ ->
      withTempDirectory (fun dir ->
        for i in 1 .. PassRecord.keepCommits + 3 do
          let commit = sprintf "commit%02d" i
          PassRecord.write (PassRecord.recordPath dir commit "t") record
          Directory.SetLastWriteTimeUtc(Path.Combine(dir, commit), DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(float i))
        PassRecord.prune dir
        let kept = Directory.GetDirectories dir |> Array.map Path.GetFileName |> Array.sort
        kept |> Expect.hasLength "the cap" PassRecord.keepCommits
        kept |> Array.contains "commit01" |> Expect.isFalse "the oldest is gone"
        kept |> Array.contains (sprintf "commit%02d" (PassRecord.keepCommits + 3)) |> Expect.isTrue "the newest stays")
  ]

[<Tests>]
let tests = testList "Pass record suite" [ decisionTests; formatTests; closureTests; storeTests ]
