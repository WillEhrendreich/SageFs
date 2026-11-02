module SageFs.Tests.OperatorStoreTests

/// How `operator.token` comes to exist: the pure machine in SageFs.Core/OperatorFile.fs, driven by a scripted
/// world, and the shell in SageFs/OperatorStore.fs, driven against real files in a temporary directory.

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.EdgeCredential
open SageFs.OperatorFile

let credentialOf (n: int) : string = OperatorCredential.ofEntropy (Array.create OperatorCredential.entropyBytes (byte n))
let entropyOf (n: int) : byte[] = Array.create OperatorCredential.entropyBytes (byte n)

/// What the machine asked for, in order, when `world` answered each effect.
let drive (entropy: byte[]) (world: Effect -> Answer) : State * Effect list =
  let rec go (state: State, effect: Effect option) (asked: Effect list) =
    match effect with
    | None -> state, List.rev asked
    | Some pending -> go (next entropy state (world pending)) (pending :: asked)
  go start []

let finishedWith (state: State) : Result<OperatorHash, Refusal> =
  match state with
  | State.Finished result -> result
  | other -> failtestf "the machine should have finished, and is at %A" other

let withTempDir (body: string -> unit) : unit =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-operator-%s" (Guid.NewGuid().ToString "N"))
  Directory.CreateDirectory dir |> ignore
  try body dir
  finally (try Directory.Delete(dir, true) with _ -> ())

let litterIn (dir: string) : string list =
  Directory.GetFiles dir |> Array.map Path.GetFileName |> Array.filter (fun name -> name <> OperatorStore.FileName) |> List.ofArray

let ownerOnlyOnThisPlatform : bool = not (OperatingSystem.IsWindows())

[<Tests>]
let tests =
  testList "OperatorFile" [

    testList "the machine, against a scripted world" [
      test "WHY - an empty directory: read, write a temporary file, check its mode, publish without replacing, done" {
        let state, asked =
          drive (entropyOf 1) (function
            | Effect.ReadTarget -> Answer.Observed Observation.Missing
            | Effect.WriteTemp _ -> Answer.TempWritten
            | Effect.ReadTempMode -> Answer.TempModeIs FileMode.OwnerOnly
            | Effect.PublishExclusive -> Answer.Published
            | Effect.DiscardTemp -> Answer.Discarded)
        asked
        |> List.map (function
          | Effect.ReadTarget -> "read"
          | Effect.WriteTemp _ -> "write"
          | Effect.ReadTempMode -> "mode"
          | Effect.PublishExclusive -> "publish"
          | Effect.DiscardTemp -> "discard")
        |> Expect.equal "the steps, in order" [ "read"; "write"; "mode"; "publish" ]
        finishedWith state |> Expect.equal "the credential is the one written" (Ok(OperatorHash.ofCredential (credentialOf 1)))
      }

      test "WHY - a good file is the credential, and nothing is written" {
        let state, asked =
          drive (entropyOf 1) (function
            | Effect.ReadTarget -> Answer.Observed(Observation.Present(credentialOf 9 + "\n", FileMode.OwnerOnly))
            | other -> failtestf "nothing else should be asked, and %A was" other)
        asked |> Expect.equal "only the read" [ Effect.ReadTarget ]
        finishedWith state |> Expect.equal "the file's credential, a trailing newline forgiven" (Ok(OperatorHash.ofCredential (credentialOf 9)))
      }

      test "WHY - the loser of a race discards its credential and takes the winner's" {
        let mutable reads = 0
        let state, asked =
          drive (entropyOf 1) (function
            | Effect.ReadTarget ->
              reads <- reads + 1
              match reads with
              | 1 -> Answer.Observed Observation.Missing
              | _ -> Answer.Observed(Observation.Present(credentialOf 2, FileMode.OwnerOnly))
            | Effect.WriteTemp _ -> Answer.TempWritten
            | Effect.ReadTempMode -> Answer.TempModeIs FileMode.OwnerOnly
            | Effect.PublishExclusive -> Answer.TargetAlreadyExists
            | Effect.DiscardTemp -> Answer.Discarded)
        asked |> List.contains Effect.DiscardTemp |> Expect.isTrue "its own temporary file is removed"
        finishedWith state |> Expect.equal "the winner's credential" (Ok(OperatorHash.ofCredential (credentialOf 2)))
      }

      test "WHY - a file that is not a credential is refused as corrupt, and nothing replaces it" {
        let state, asked =
          drive (entropyOf 1) (function
            | Effect.ReadTarget -> Answer.Observed(Observation.Present("sfo_cut-short", FileMode.OwnerOnly))
            | other -> failtestf "nothing else should be asked, and %A was" other)
        asked |> Expect.equal "only the read" [ Effect.ReadTarget ]
        finishedWith state |> Expect.equal "refused" (Error Refusal.Corrupt)
      }

      test "WHY - a file others can read is refused with its mode, and nothing replaces it" {
        let state, _ =
          drive (entropyOf 1) (function
            | Effect.ReadTarget -> Answer.Observed(Observation.Present(credentialOf 3, FileMode.TooWide "644"))
            | other -> failtestf "nothing else should be asked, and %A was" other)
        finishedWith state |> Expect.equal "refused" (Error(Refusal.ModeTooWide "644"))
      }

      test "WHY - a filesystem that does not keep owner-only is refused, the temporary file is discarded, and nothing is published" {
        let state, asked =
          drive (entropyOf 1) (function
            | Effect.ReadTarget -> Answer.Observed Observation.Missing
            | Effect.WriteTemp _ -> Answer.TempWritten
            | Effect.ReadTempMode -> Answer.TempModeIs(FileMode.TooWide "677")
            | Effect.DiscardTemp -> Answer.Discarded
            | Effect.PublishExclusive -> failtest "a credential on a filesystem that cannot hold it must not be published")
        asked |> List.contains Effect.DiscardTemp |> Expect.isTrue "cleaned up"
        finishedWith state |> Expect.equal "refused with the mode the filesystem kept" (Error(Refusal.FilesystemCannotHoldOwnerOnly "677"))
      }

      test "WHY - a target that keeps appearing and vanishing ends in a refusal, not a loop" {
        let state, _ =
          drive (entropyOf 1) (function
            | Effect.ReadTarget -> Answer.Observed Observation.Missing
            | Effect.WriteTemp _ -> Answer.TempWritten
            | Effect.ReadTempMode -> Answer.TempModeIs FileMode.OwnerOnly
            | Effect.PublishExclusive -> Answer.TargetAlreadyExists
            | Effect.DiscardTemp -> Answer.Discarded)
        finishedWith state |> Expect.equal "gave up" (Error Refusal.GaveUp)
      }

      test "WHY - a failed step finishes with the failure named, after the temporary file is discarded" {
        let state, asked =
          drive (entropyOf 1) (function
            | Effect.ReadTarget -> Answer.Observed Observation.Missing
            | Effect.WriteTemp _ -> Answer.Failed "disk full"
            | Effect.DiscardTemp -> Answer.Discarded
            | other -> failtestf "unexpected %A" other)
        asked |> List.contains Effect.DiscardTemp |> Expect.isTrue "cleaned up"
        match finishedWith state with
        | Error(Refusal.Io reason) -> reason |> Expect.stringContains "names the failure" "disk full"
        | other -> failtestf "expected an Io refusal, got %A" other
      }

      test "WHY - every refusal says what to do, and none carries a credential" {
        let secret = credentialOf 5
        [ Refusal.Corrupt
          Refusal.ModeTooWide "644"
          Refusal.FilesystemCannotHoldOwnerOnly "677"
          Refusal.Io "disk full"
          Refusal.GaveUp ]
        |> List.iter (fun refusal ->
          let text = describe refusal
          String.IsNullOrWhiteSpace text |> Expect.isFalse "it says something"
          text.Contains secret |> Expect.isFalse "and not the credential")
      }

      testProperty "WHY - whatever the world answers, the machine finishes within a bounded number of steps and never throws" <| fun () ->
        let genAnswer =
          Gen.oneof [
            Gen.constant(Answer.Observed Observation.Missing)
            Gen.constant(Answer.Observed(Observation.Present(credentialOf 1, FileMode.OwnerOnly)))
            Gen.constant(Answer.Observed(Observation.Present("junk", FileMode.OwnerOnly)))
            Gen.constant(Answer.Observed(Observation.Present(credentialOf 1, FileMode.TooWide "666")))
            Gen.constant Answer.TempWritten
            Gen.constant(Answer.TempModeIs FileMode.OwnerOnly)
            Gen.constant(Answer.TempModeIs(FileMode.TooWide "666"))
            Gen.constant Answer.Published
            Gen.constant Answer.TargetAlreadyExists
            Gen.constant Answer.Discarded
            Gen.constant(Answer.Failed "boom") ]
        Prop.forAll (Arb.fromGen (Gen.listOfLength 60 genAnswer)) (fun answers ->
          let finished =
            answers
            |> List.fold
              (fun (state, effect) answer ->
                match effect with
                | None -> state, None
                | Some _ -> next (entropyOf 1) state answer)
              start
            |> fst
          match finished with
          | State.Finished _ -> true
          | _ -> false)
    ]

    testList "the shell, against real files" [
      test "WHY - the first start makes the file owner-only and well formed, and leaves nothing else behind" {
        withTempDir (fun dir ->
          let hash = OperatorStore.load dir |> Result.defaultWith (fun refusal -> failtestf "load refused: %A" refusal)
          let credential = File.ReadAllText(OperatorStore.pathIn dir)
          OperatorCredential.isWellFormed credential |> Expect.isTrue "the file holds a credential"
          OperatorHash.ofCredential credential |> Expect.equal "and the hash is of that credential" hash
          litterIn dir |> Expect.equal "no temporary file is left" []
          match ownerOnlyOnThisPlatform with
          | true ->
            File.GetUnixFileMode(OperatorStore.pathIn dir)
            |> Expect.equal "mode 600" (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
          | false -> ())
      }

      test "WHY - a second start reads the same credential" {
        withTempDir (fun dir ->
          let first = OperatorStore.load dir |> Result.defaultWith (fun refusal -> failtestf "refused: %A" refusal)
          let second = OperatorStore.load dir |> Result.defaultWith (fun refusal -> failtestf "refused: %A" refusal)
          second |> Expect.equal "the same hash" first)
      }

      test "WHY - eight daemons starting on one directory at once all end with one credential" {
        withTempDir (fun dir ->
          let hashes =
            Array.init 8 (fun _ -> Task.Run(fun () -> OperatorStore.load dir))
            |> Array.map (fun task -> task.Result |> Result.defaultWith (fun refusal -> failtestf "refused: %A" refusal))
          hashes |> Array.distinct |> Array.length |> Expect.equal "one credential" 1
          litterIn dir |> Expect.equal "and no temporary file" [])
      }

      test "WHY - a file that is not a credential is refused and left exactly as it was" {
        withTempDir (fun dir ->
          let path = OperatorStore.pathIn dir
          File.WriteAllText(path, "sfo_not-a-credential")
          OperatorStore.load dir |> Expect.equal "refused as corrupt" (Error Refusal.Corrupt)
          File.ReadAllText path |> Expect.equal "untouched" "sfo_not-a-credential")
      }

      test "WHY - a credential file others can read is refused, and its mode is named" {
        match ownerOnlyOnThisPlatform with
        | false -> ()
        | true ->
          withTempDir (fun dir ->
            let path = OperatorStore.pathIn dir
            File.WriteAllText(path, credentialOf 4)
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead)
            match OperatorStore.load dir with
            | Error(Refusal.ModeTooWide octal) -> octal |> Expect.equal "the mode as chmod shows it" "640"
            | other -> failtestf "expected ModeTooWide, got %A" other)
      }

      test "WHY - the person's tools read the credential text, and are told what to do when it is missing or wrong" {
        withTempDir (fun dir ->
          match OperatorStore.readCredential dir with
          | Error(OperatorStore.ReadRefusal.NoFile path) -> path |> Expect.equal "names the file" (OperatorStore.pathIn dir)
          | other -> failtestf "expected NoFile, got %A" other
          File.WriteAllText(OperatorStore.pathIn dir, "junk")
          match OperatorStore.readCredential dir with
          | Error(OperatorStore.ReadRefusal.NotACredential _) -> ()
          | other -> failtestf "expected NotACredential, got %A" other
          File.WriteAllText(OperatorStore.pathIn dir, credentialOf 6 + "\n")
          OperatorStore.readCredential dir |> Expect.equal "the credential, without the newline" (Ok(credentialOf 6)))
      }
    ]
  ]
