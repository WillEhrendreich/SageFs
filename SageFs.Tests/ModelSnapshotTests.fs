module SageFs.Tests.ModelSnapshotTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.ModelSnapshot
open SageFs.Measures

// ── Test model ──

type TestModel = { Value: int; Label: string }

let model v = { Value = v; Label = sprintf "v%d" v }

let makeRing cap =
  create { Capacity = cap; Enabled = true }

let recordModel label ms v (ring: SnapshotRing<TestModel>) =
  record label (floatMs ms) (model v) ring

// ── Tests ──

[<Tests>]
let modelSnapshotTests =
  testList "ModelSnapshot" [

    testList "create" [
      test "creates empty ring with config" {
        let ring: SnapshotRing<TestModel> = makeRing 50
        ring |> count |> Expect.equal "empty" 0
        ring |> totalRecorded |> Expect.equal "none recorded" 0L
        ring.Config.Capacity |> Expect.equal "capacity" 50
        ring.Config.Enabled |> Expect.isTrue "enabled"
      }

      test "defaultConfig has capacity 100 and is enabled" {
        defaultConfig.Capacity |> Expect.equal "capacity" 100
        defaultConfig.Enabled |> Expect.isTrue "enabled"
      }
    ]

    testList "record and retrieve" [
      test "record adds snapshot" {
        let ring =
          makeRing 10
          |> recordModel "Msg1" 1.5 42
        ring |> count |> Expect.equal "count" 1
        ring |> totalRecorded |> Expect.equal "total" 1L
      }

      test "current returns most recent snapshot" {
        let ring =
          makeRing 10
          |> recordModel "First" 1.0 1
          |> recordModel "Second" 2.0 2
        let snap = ring |> current |> Option.get
        snap.MsgLabel |> Expect.equal "label" "Second"
        snap.Model.Value |> Expect.equal "value" 2
        snap.SequenceNumber |> Expect.equal "seq" 1L
      }

      test "tryGet navigates by age" {
        let ring =
          makeRing 10
          |> recordModel "A" 1.0 10
          |> recordModel "B" 2.0 20
          |> recordModel "C" 3.0 30
        let snap0 = ring |> tryGet 0 |> Option.get
        let snap1 = ring |> tryGet 1 |> Option.get
        let snap2 = ring |> tryGet 2 |> Option.get
        snap0.MsgLabel |> Expect.equal "age 0" "C"
        snap1.MsgLabel |> Expect.equal "age 1" "B"
        snap2.MsgLabel |> Expect.equal "age 2" "A"
      }

      test "navigateTo returns model at age" {
        let ring =
          makeRing 10
          |> recordModel "X" 1.0 99
          |> recordModel "Y" 2.0 100
        let m = ring |> navigateTo 1 |> Option.get
        m.Value |> Expect.equal "previous model" 99
      }

      test "records respect capacity eviction" {
        let ring =
          makeRing 3
          |> recordModel "A" 1.0 1
          |> recordModel "B" 2.0 2
          |> recordModel "C" 3.0 3
          |> recordModel "D" 4.0 4
        ring |> count |> Expect.equal "capped at 3" 3
        ring |> totalRecorded |> Expect.equal "total" 4L
        let labels =
          [ ring |> tryGet 0; ring |> tryGet 1; ring |> tryGet 2 ]
          |> List.choose id
          |> List.map (fun s -> s.MsgLabel)
        labels |> Expect.equal "oldest evicted" [ "D"; "C"; "B" ]
      }
    ]

    testList "disabled recording" [
      test "disabled ring ignores records" {
        let ring =
          makeRing 10
          |> setEnabled false
          |> recordModel "Ignored" 1.0 42
        ring |> count |> Expect.equal "still empty" 0
        ring |> current |> Expect.equal "no snapshot" None
      }

      test "re-enabling allows recording" {
        let ring =
          makeRing 10
          |> setEnabled false
          |> recordModel "Ignored" 1.0 1
          |> setEnabled true
          |> recordModel "Visible" 2.0 2
        ring |> count |> Expect.equal "one snapshot" 1
        (ring |> current |> Option.get).MsgLabel
        |> Expect.equal "only visible" "Visible"
      }
    ]

    testList "sequence numbers" [
      test "sequence numbers are monotonically increasing" {
        let ring =
          makeRing 10
          |> recordModel "A" 1.0 1
          |> recordModel "B" 2.0 2
          |> recordModel "C" 3.0 3
        let seqs =
          [ ring |> tryGet 0; ring |> tryGet 1; ring |> tryGet 2 ]
          |> List.choose id
          |> List.map (fun s -> s.SequenceNumber)
        seqs |> Expect.equal "decreasing by age" [ 2L; 1L; 0L ]
      }
    ]

    testList "summary and labels" [
      test "summary of empty ring" {
        let ring: SnapshotRing<TestModel> = makeRing 10
        ring |> summary |> Expect.equal "empty" "No snapshots"
      }

      test "summary shows latest info" {
        let ring =
          makeRing 10
          |> recordModel "Update" 3.5 42
        let s = ring |> summary
        s |> Expect.stringContains "has count" "1/10"
        s |> Expect.stringContains "has label" "Update"
        s |> Expect.stringContains "has timing" "3.5ms"
      }

      test "recentLabels returns formatted list" {
        let ring =
          makeRing 10
          |> recordModel "A" 1.0 1
          |> recordModel "B" 2.0 2
          |> recordModel "C" 3.0 3
        let labels = ring |> recentLabels 2
        labels |> Expect.hasLength "limited to 2" 2
        labels.[0] |> Expect.stringContains "has C" "C"
        labels.[1] |> Expect.stringContains "has B" "B"
      }
    ]

    testList "timing" [
      test "snapshot records update timing" {
        let ring =
          makeRing 10
          |> recordModel "Slow" 42.5 1
        let snap = ring |> current |> Option.get
        snap.UpdateMs |> Expect.equal "timing" (floatMs 42.5)
      }

      test "snapshot records timestamp" {
        let before = System.DateTimeOffset.UtcNow
        let ring = makeRing 10 |> recordModel "Now" 1.0 1
        let after = System.DateTimeOffset.UtcNow
        let snap = ring |> current |> Option.get
        (snap.Timestamp, before)
        |> Expect.isGreaterThanOrEqual "after start"
        (after, snap.Timestamp)
        |> Expect.isGreaterThanOrEqual "before end"
      }
    ]
  ]
