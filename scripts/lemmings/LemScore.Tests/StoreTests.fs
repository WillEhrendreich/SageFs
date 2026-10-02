module LemScore.Tests.StoreTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open LemRun

let private scratch () : string =
  let dir = Path.Combine(Path.GetTempPath(), "lem-store-" + Guid.NewGuid().ToString("N").Substring(0, 8))
  Directory.CreateDirectory dir |> ignore
  dir

let private cleanup (dir: string) = try Store.removeTree dir with _ -> ()

[<Tests>]
let storeTests =
  testList "the store of builds a run uses (once, read-only, off the run)" [
    testCase "a build is made once, kept, and handed out again without being made again" <| fun _ ->
      let root = scratch ()
      try
        let made = ref 0
        let make (dir: string) =
          made.Value <- made.Value + 1
          File.WriteAllText(Path.Combine(dir, "SageFs.dll"), "the build")
          Ok ()
        let first = Store.ensure root Store.Bridge "0.6.1-abc" make
        let second = Store.ensure root Store.Bridge "0.6.1-abc" make
        made.Value |> Expect.equal "made once" 1
        first |> Expect.equal "the same place" second
        match first with
        | Ok dir -> File.ReadAllText(Path.Combine(dir, "SageFs.dll")) |> Expect.equal "content" "the build"
        | Error e -> failtest e
      finally cleanup root

    testCase "what is stored cannot be written: a trial must not change what the next one runs" <| fun _ ->
      let root = scratch ()
      try
        match Store.ensure root Store.Bridge "k" (fun dir -> File.WriteAllText(Path.Combine(dir, "a.dll"), "x"); Ok ()) with
        | Ok dir ->
          let write () = File.WriteAllText(Path.Combine(dir, "a.dll"), "evil")
          // root can write anywhere, and so can a test run as root: only assert it where permissions apply
          if Environment.UserName <> "root" then Expect.throws "read-only" write
          File.ReadAllText(Path.Combine(dir, "a.dll")) |> Expect.equal "unchanged" "x"
        | Error e -> failtest e
      finally cleanup root

    testCase "a build that fails to make leaves nothing behind, and the next try makes it" <| fun _ ->
      let root = scratch ()
      try
        Store.ensure root Store.Drive "k" (fun _ -> Error "boom") |> Expect.equal "refused" (Error "boom")
        Directory.Exists(Store.entryDir root Store.Drive "k") |> Expect.isFalse "nothing kept"
        Directory.Exists(Store.entryDir root Store.Drive "k" + ".building") |> Expect.isFalse "no half-built directory"
        Store.ensure root Store.Drive "k" (fun dir -> File.WriteAllText(Path.Combine(dir, "x"), ""); Ok ()) |> Result.isOk |> Expect.isTrue "made"
      finally cleanup root

    testCase "two runs that start together make a build once" <| fun _ ->
      let root = scratch ()
      try
        let made = ref 0
        let make (dir: string) =
          Threading.Interlocked.Increment made |> ignore
          Threading.Thread.Sleep 300
          File.WriteAllText(Path.Combine(dir, "x"), "")
          Ok ()
        Task.WaitAll [| for _ in 1..4 -> Task.Run(fun () -> Store.ensure root Store.Bridge "same" make |> ignore) |]
        made.Value |> Expect.equal "made once" 1
      finally cleanup root

    testCase "a key is one path segment of plain characters" <| fun _ ->
      Store.sanitize "0.6.882+d7e11077/../x y" |> Expect.equal "plain" "0.6.882-d7e11077-..-x-y"
      Store.sanitize "../evil" |> Expect.equal "no escape" "..-evil"

    testCase "the key of a set of sources changes with any edit and not otherwise" <| fun _ ->
      let dir = scratch ()
      try
        let a, b = Path.Combine(dir, "a.fs"), Path.Combine(dir, "b.fs")
        File.WriteAllText(a, "1")
        File.WriteAllText(b, "2")
        let k1 = Store.hashFiles dir [ a; b ] 16
        Store.hashFiles dir [ b; a ] 16 |> Expect.equal "order does not matter" k1
        File.WriteAllText(b, "3")
        Store.hashFiles dir [ a; b ] 16 |> Expect.notEqual "an edit changes it" k1
        k1.Length |> Expect.equal "width" 16
      finally cleanup dir

    testCase "only builds that nobody used for a week and are not among the newest are collected" <| fun _ ->
      let now = DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc)
      let days (n: float) = now - TimeSpan.FromDays n
      let entries = [ "new", days 0.0; "mid", days 1.0; "mid2", days 3.0; "fourth-but-recent", days 5.0; "nine-days", days 9.0; "old", days 30.0 ]
      Store.collectable now entries |> Expect.equal "the stale ones beyond the newest three" [ "nine-days"; "old" ]
      Store.collectable now [ "a", days 100.0; "b", days 200.0 ] |> Expect.isEmpty "never below the newest three"
  ]
