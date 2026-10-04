/// The System.IO side of the nudge door, on a real temp directory: the same
/// protocol the in-memory disk runs, against the file system a user has.
module SageFs.Tests.NudgeFsTests

open System
open System.IO
open System.Text
open Expecto
open Expecto.Flip
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.TweakLog
open SageFs.Features.Tweak.NudgeIo
open SageFs.Features.Tweak.NudgeFs

let utf8 = UTF8Encoding(false)

let withTempDir (body: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-nudgefs-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory dir |> ignore
  try body dir
  finally (try Directory.Delete(dir, true) with _ -> ())

let address : TweakAddress = { ModulePath = [ "M" ]; BindingName = "x"; Path = [] }

let saved (id: int) : LoggedEvent =
  { Id = id; At = int64 id; Event = TweakLogEvent.TweakSaved(address, "1", "2", contentHash "2", "file-hash") }

[<Tests>]
let nudgeFsTests =
  testList "NudgeFs" [

    testCase "WHY - a clean atomic write replaces the file's bytes and leaves nothing else in the directory" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "Tuning.fs")
        File.WriteAllText(path, "module M\nlet x = 1\n")
        AtomicWrite.write steps path (utf8.GetBytes "module M\nlet x = 2\n") |> Expect.wantOk "writes"
        File.ReadAllText path |> Expect.equal "new text" "module M\nlet x = 2\n"
        Directory.GetFileSystemEntries dir |> Array.map Path.GetFileName |> Array.toList
        |> Expect.equal "no temp file is left" [ "Tuning.fs" ])

    testCase "WHY - an atomic write keeps the file's permissions, so a nudge never loosens or locks a file" <| fun _ ->
      match OperatingSystem.IsWindows() with
      | true -> skiptest "Unix file modes only"
      | false ->
        withTempDir (fun dir ->
          let path = Path.Combine(dir, "Tuning.fs")
          File.WriteAllText(path, "module M\nlet x = 1\n")
          let mode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead
          File.SetUnixFileMode(path, mode)
          AtomicWrite.write steps path (utf8.GetBytes "module M\nlet x = 2\n") |> Expect.wantOk "writes"
          File.GetUnixFileMode path |> Expect.equal "the mode survives the rename" mode)

    testCase "WHY - a failed write to a missing directory is an error, not an exception, and names the step" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "nowhere", "Tuning.fs")
        match AtomicWrite.write steps path (utf8.GetBytes "x") with
        | Error fault -> fault.Operation |> Expect.equal "the temp write is what failed" FileOperation.WritingTemp
        | Ok() -> failtest "writing into a missing directory reported success")

    testCase "WHY - a file with a byte-order mark gets its mark back, so the bytes outside an edit never change" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "Tuning.fs")
        let original = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (utf8.GetBytes "module M\nlet x = 1\n")
        File.WriteAllBytes(path, original)
        let image = steps.ReadBytes path |> Result.bind (FileImage.decode path) |> Expect.wantOk "reads"
        let edited = { image with Text = image.Text.Replace("= 1", "= 2") }
        AtomicWrite.write steps path (FileImage.encode edited) |> Expect.wantOk "writes"
        File.ReadAllBytes path
        |> Expect.equal "the mark and every other byte are as they were"
          (Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (utf8.GetBytes "module M\nlet x = 2\n")))

    testCase "WHY - the kind of a path tells a file from a missing path, a directory and a symbolic link" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "Tuning.fs")
        File.WriteAllText(path, "x")
        steps.KindOf path |> Expect.equal "a regular file" FileKind.RegularFile
        steps.KindOf(Path.Combine(dir, "gone.fs")) |> Expect.equal "nothing there" FileKind.Missing
        steps.KindOf dir |> Expect.equal "a directory is not a file" FileKind.NotAFile
        match OperatingSystem.IsWindows() with
        | true -> ()
        | false ->
          let link = Path.Combine(dir, "link.fs")
          File.CreateSymbolicLink(link, path) |> ignore
          steps.KindOf link |> Expect.equal "a link is named as one, not followed" FileKind.SymbolicLink)

    testCase "WHY - making a directory makes the ones above it, and making one that exists is not an error" <| fun _ ->
      withTempDir (fun dir ->
        let nested = Path.Combine(dir, "a", "b", "c")
        steps.MakeDirectory nested |> Expect.wantOk "creates the chain"
        Directory.Exists nested |> Expect.isTrue "it is there"
        steps.MakeDirectory nested |> Expect.wantOk "and again is fine")

    testCase "WHY - reading a path that is not there is an error value naming the read" <| fun _ ->
      match steps.ReadBytes(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))) with
      | Error fault -> fault.Operation |> Expect.equal "reading" FileOperation.Reading
      | Ok _ -> failtest "a missing file read as success"

    testCase "WHY - the journal protocol works on the real disk: append, tear the tail by hand, load heals it" <| fun _ ->
      withTempDir (fun dir ->
        let target = Path.Combine(dir, "Tuning.fs")
        let journal = Path.Combine(dir, "tweaks", "s1", "abc.events")
        Journal.append steps target journal (saved 1) |> Expect.wantOk "first, into a folder that is not there yet"
        Journal.append steps target journal (saved 2) |> Expect.wantOk "second"
        let whole = File.ReadAllBytes journal
        File.WriteAllBytes(journal, Array.sub whole 0 (whole.Length - 3))
        let loaded = Journal.load steps target journal |> Expect.wantOk "loads"
        loaded.Log.Events |> List.map _.Id |> Expect.equal "only the whole record survives" [ 1 ]
        loaded.Healing |> Expect.equal "and the tear is reported" TailHealing.TornTailRemoved)

    testList "journalPathFor" [
      testCase "WHY - the journal sits under the tweaks directory, in its session's folder, as an .events file" <| fun _ ->
        let tweaks = Path.Combine(Path.GetTempPath(), "tweaks")
        let path = journalPathFor tweaks "s1" "/repo/src/Tuning.fs"
        Path.GetDirectoryName path |> Expect.equal "tweaks/<session>" (Path.Combine(tweaks, "s1"))
        path.EndsWith(".events", StringComparison.Ordinal) |> Expect.isTrue "the extension the format uses"

      testCase "WHY - one file always gets one journal, and two files never share one" <| fun _ ->
        let tweaks = Path.Combine(Path.GetTempPath(), "tweaks")
        journalPathFor tweaks "s1" "/repo/src/Tuning.fs" |> Expect.equal "stable" (journalPathFor tweaks "s1" "/repo/src/Tuning.fs")
        journalPathFor tweaks "s1" "/repo/src/Tuning.fs" |> Expect.notEqual "per file" (journalPathFor tweaks "s1" "/repo/src/Other.fs")
        journalPathFor tweaks "s1" "/repo/src/Tuning.fs" |> Expect.notEqual "per session" (journalPathFor tweaks "s2" "/repo/src/Tuning.fs")
    ]
  ]
