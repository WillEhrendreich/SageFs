/// The disk protocol under the nudge door, run against an in-memory disk that
/// crashes where a test says. Two promises are checked at every step a crash can
/// land on: the source file is wholly old or wholly new, and the journal never
/// yields a partial event.
module SageFs.Tests.NudgeIoTests

open System
open System.IO
open System.Text
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Tests.SharedGenerators
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.TweakLog
open SageFs.Features.Tweak.NudgeIo
open SageFs.Simulation
open SageFs.Simulation.NudgeDisk

let utf8 = UTF8Encoding(false)
let bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]

let target = "/repo/src/Tuning.fs"
let journalPath = "/data/tweaks/s1/abc.events"

let address : TweakAddress = { ModulePath = [ "M" ]; BindingName = "x"; Path = [] }

let saved (id: int) (before: string) (after: string) : LoggedEvent =
  { Id = id
    At = int64 id
    Event = TweakLogEvent.TweakSaved(address, before, after, contentHash after, "file-hash") }

let oldBytes = utf8.GetBytes "module M\nlet x = 1\n"
let newBytes = utf8.GetBytes "module M\nlet x = 2\n"

let freshDisk () =
  let disk = Disk()
  disk.Put(target, oldBytes)
  disk

/// Run `work`, and say whether the process died in it. Anything else propagates.
let diesIn (work: unit -> unit) : bool =
  try
    work ()
    false
  with SimulatedCrash _ -> true

let validUtf8Text : Gen<string> =
  Gen.listOf (Gen.elements [ "a"; "x"; " "; "é"; "日本"; "\r\n"; "\n"; "𝄞"; "<m/s>" ]) |> Gen.map (String.concat "")

[<Tests>]
let nudgeIoTests =
  testList "NudgeIo" [

    testList "FileImage" [
      testCase "WHY - plain UTF-8 decodes to its text and says it had no byte-order mark" <| fun _ ->
        let image = FileImage.decode target (utf8.GetBytes "let x = 1\n") |> Expect.wantOk "valid UTF-8"
        image |> Expect.equal "text and encoding" { Text = "let x = 1\n"; Encoding = FileEncoding.Utf8 }

      testCase "WHY - a byte-order mark is not part of the text, and is remembered so it can be put back" <| fun _ ->
        let image = FileImage.decode target (Array.append bom (utf8.GetBytes "let x = 1\n")) |> Expect.wantOk "valid UTF-8 with a mark"
        image |> Expect.equal "the mark is the encoding, not a character" { Text = "let x = 1\n"; Encoding = FileEncoding.Utf8WithBom }

      testCase "WHY - bytes that are not UTF-8 are refused, never decoded into replacement characters that would be written back" <| fun _ ->
        match FileImage.decode target [| 0xC3uy; 0x28uy |] with
        | Error fault ->
          fault.Operation |> Expect.equal "the decode is what failed" FileOperation.Decoding
          fault.Path |> Expect.equal "the file is named" target
        | Ok image -> failtestf "invalid UTF-8 decoded to %A" image

      testPropertyWithConfig propConfig "PROPERTY, encoding a decoded file gives back the exact bytes, with or without a mark, CRLF or LF or mixed" <| fun () ->
        Prop.forAll (Arb.fromGen validUtf8Text) (fun text ->
          [ utf8.GetBytes text; Array.append bom (utf8.GetBytes text) ]
          |> List.forall (fun bytes ->
            match FileImage.decode target bytes with
            | Ok image -> FileImage.encode image = bytes
            | Error _ -> false))
    ]

    testList "AtomicWrite" [
      testCase "WHY - the temp file is a sibling of the target and ends in .tmp, so the watcher ignores it and the rename is atomic" <| fun _ ->
        let temp = AtomicWrite.tempPathFor target
        Path.GetDirectoryName temp |> Expect.equal "same directory, so the rename never crosses a volume" (Path.GetDirectoryName target)
        temp.EndsWith(".tmp", StringComparison.Ordinal) |> Expect.isTrue "the file watcher skips .tmp"
        temp |> Expect.notEqual "and it is not the target" target

      testCase "WHY - a clean write replaces the file and leaves no temp behind" <| fun _ ->
        let disk = freshDisk ()
        AtomicWrite.write disk.Steps target newBytes |> Expect.wantOk "writes"
        disk.BytesOf target |> Expect.equal "the new bytes" newBytes
        disk.Paths |> Expect.equal "only the target is left" [ target ]

      testCase "WHY - a failed temp write leaves the file byte-identical and no temp" <| fun _ ->
        let disk = freshDisk ()
        disk.Arm [ 1, Fault.FailCleanly ]
        match AtomicWrite.write disk.Steps target newBytes with
        | Error fault -> fault.Operation |> Expect.equal "the temp write is what failed" FileOperation.WritingTemp
        | Ok() -> failtest "a failed write reported success"
        disk.BytesOf target |> Expect.equal "byte-identical" oldBytes
        disk.Paths |> Expect.equal "the temp was discarded" [ target ]

      testCase "WHY - a failed rename leaves the file byte-identical and discards the temp" <| fun _ ->
        let disk = freshDisk ()
        disk.Arm [ 2, Fault.FailCleanly ]
        match AtomicWrite.write disk.Steps target newBytes with
        | Error fault -> fault.Operation |> Expect.equal "the rename is what failed" FileOperation.Renaming
        | Ok() -> failtest "a failed rename reported success"
        disk.BytesOf target |> Expect.equal "byte-identical" oldBytes
        disk.Paths |> Expect.equal "the temp was discarded" [ target ]

      testCase "WHY - a crash at any step, in any way, leaves the file wholly old or wholly new, never partial" <| fun _ ->
        for step in [ 1; 2 ] do
          for fault in [ Fault.CrashBefore; Fault.CrashTorn 50; Fault.CrashTorn 1; Fault.CrashAfter ] do
            let disk = freshDisk ()
            disk.Arm [ step, fault ]
            diesIn (fun () -> AtomicWrite.write disk.Steps target newBytes |> ignore) |> Expect.isTrue "the process died"
            let now = disk.BytesOf target
            (now = oldBytes || now = newBytes)
            |> Expect.isTrue (sprintf "step %d, %A: the file is old or new, never torn" step fault)

      testCase "WHY - the twin that writes in place is torn by the same crash, so the check above has teeth" <| fun _ ->
        let disk = freshDisk ()
        disk.Arm [ 1, Fault.CrashTorn 50 ]
        diesIn (fun () -> AtomicWrite.writeInPlaceTwin disk.Steps target newBytes |> ignore) |> Expect.isTrue "died"
        let now = disk.BytesOf target
        (now <> oldBytes && now <> newBytes) |> Expect.isTrue "an in-place write is left half written"
    ]

    testList "Journal" [
      testCase "WHY - no journal yet is an empty one, not an error" <| fun _ ->
        let loaded = Journal.load (Disk().Steps) target journalPath |> Expect.wantOk "missing is empty"
        loaded.Log |> Expect.equal "empty" EventLog.empty
        loaded.Healing |> Expect.equal "nothing to heal" TailHealing.Intact

      testCase "WHY - what is appended is what loads, in order, and the next id carries on" <| fun _ ->
        let disk = Disk()
        Journal.append disk.Steps target journalPath (saved 1 "1" "2") |> Expect.wantOk "first"
        Journal.append disk.Steps target journalPath (saved 2 "2" "3") |> Expect.wantOk "second"
        let loaded = Journal.load disk.Steps target journalPath |> Expect.wantOk "loads"
        loaded.Log.Events |> List.map _.Id |> Expect.equal "both events, oldest first" [ 1; 2 ]
        loaded.Log.NextId |> Expect.equal "ids keep counting" 3

      testCase "WHY - the file carries the engine's own segment header, so the engine's reader grades it" <| fun _ ->
        let disk = Disk()
        Journal.append disk.Steps target journalPath (saved 1 "1" "2") |> Expect.wantOk "appends"
        let decoded =
          TweakLogFormat.decodeSegment (Journal.fingerprintFor target) (disk.BytesOf journalPath)
          |> Expect.wantOk "the engine decodes it"
        decoded.Grade |> Expect.equal "this build, this file" LogGrade.Fine
        decoded.TornTail |> Expect.isFalse "whole"

      testCase "WHY - a journal is about one file: another path's journal fingerprints differently" <| fun _ ->
        (Journal.fingerprintFor target <> Journal.fingerprintFor "/repo/src/Other.fs")
        |> Expect.isTrue "different targets, different fingerprints"

      testCase "WHY - a crash mid-append leaves a torn tail that load removes and reports, and a later append reads back" <| fun _ ->
        let disk = Disk()
        Journal.append disk.Steps target journalPath (saved 1 "1" "2") |> Expect.wantOk "first"
        disk.Arm [ 1, Fault.CrashTorn 40 ]
        diesIn (fun () -> Journal.append disk.Steps target journalPath (saved 2 "2" "3") |> ignore) |> Expect.isTrue "died mid-append"
        disk.Disarm()
        let loaded = Journal.load disk.Steps target journalPath |> Expect.wantOk "loads"
        loaded.Log.Events |> List.map _.Id |> Expect.equal "the torn record is not an event" [ 1 ]
        loaded.Healing |> Expect.equal "the tail was removed" TailHealing.TornTailRemoved
        Journal.append disk.Steps target journalPath (saved 2 "2" "3") |> Expect.wantOk "appends after healing"
        let again = Journal.load disk.Steps target journalPath |> Expect.wantOk "loads again"
        again.Log.Events |> List.map _.Id |> Expect.equal "the new record sits after a clean boundary" [ 1; 2 ]
        again.Healing |> Expect.equal "nothing left to heal" TailHealing.Intact

      testCase "WHY - a crash after the header and before the first record leaves a journal that loads as empty" <| fun _ ->
        let disk = Disk()
        disk.Arm [ 3, Fault.CrashBefore ]
        diesIn (fun () -> Journal.append disk.Steps target journalPath (saved 1 "1" "2") |> ignore) |> Expect.isTrue "died before the record"
        disk.Disarm()
        let loaded = Journal.load disk.Steps target journalPath |> Expect.wantOk "header only"
        loaded.Log.Events |> Expect.isEmpty "no events"

      testCase "WHY - a journal another build wrote, or another file's, is refused and never appended to" <| fun _ ->
        let disk = Disk()
        let foreign = { Journal.fingerprintFor target with SchemaVersion = Fingerprint.schemaVersion + 1 }
        disk.Put(journalPath, TweakLogFormat.encodeSegment foreign [ saved 1 "1" "2" ])
        match Journal.load disk.Steps target journalPath with
        | Error(JournalFault.Untrusted grade) -> grade |> Expect.equal "another layout cannot be read" LogGrade.Impossible
        | other -> failtestf "expected Untrusted Impossible, got %A" other
        match Journal.append disk.Steps target journalPath (saved 2 "2" "3") with
        | Error(JournalFault.Untrusted _) -> ()
        | other -> failtestf "appending to an untrusted journal must be refused, got %A" other
        let otherFile = Journal.fingerprintFor "/repo/src/Other.fs"
        disk.Put(journalPath, TweakLogFormat.encodeSegment otherFile [ saved 1 "1" "2" ])
        match Journal.load disk.Steps target journalPath with
        | Error(JournalFault.Untrusted grade) -> grade |> Expect.equal "another file's journal is a caveat, and a caveat is not enough" LogGrade.Risky
        | other -> failtestf "expected Untrusted Risky, got %A" other

      testCase "WHY - bytes that are not a journal at all are unreadable, named, and left alone" <| fun _ ->
        let disk = Disk()
        disk.Put(journalPath, [| 1uy; 2uy; 3uy |])
        match Journal.load disk.Steps target journalPath with
        | Error(JournalFault.Unreadable _) -> ()
        | other -> failtestf "expected Unreadable, got %A" other
        disk.BytesOf journalPath |> Expect.equal "not rewritten" [| 1uy; 2uy; 3uy |]
    ]
  ]
