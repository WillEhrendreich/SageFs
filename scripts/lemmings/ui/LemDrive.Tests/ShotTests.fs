module LemDrive.Tests.ShotTests

open System
open System.IO
open Expecto
open Expecto.Flip
open LemDrive
open LemDrive.Shot

let private temp () : string =
  let d = Path.Combine(Path.GetTempPath(), "lemdrive-tests-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory d |> ignore
  d

[<Tests>]
let tests =
  testList "shots" [
    testCase "names are safe in a file name" <| fun _ ->
      sanitize "Session Ready!" |> Expect.equal "lowered and dashed" "session-ready"
      sanitize "  ../../x " |> Expect.equal "no path escapes" "..-..-x" |> ignore
      sanitize "" |> Expect.equal "empty becomes the default" "shot"
      (sanitize (String.replicate 200 "a")).Length |> Expect.equal "bounded" LongestShotName

    testCase "a sanitized name never contains a path separator" <| fun _ ->
      for n in [ "a/b"; "a\\b"; "../x"; "x y/z" ] do
        (sanitize n).Contains "/" |> Expect.isFalse (sprintf "'%s' has no slash" n)

    testCase "shots are numbered in order and never reuse a number" <| fun _ ->
      let dir = temp ()
      try
        let a = Reservation.reserve dir "first-run" |> Result.defaultWith failtest
        let b = Reservation.reserve dir "session ready" |> Result.defaultWith failtest
        a.Stem |> Expect.equal "first" "001-first-run"
        b.Stem |> Expect.equal "second" "002-session-ready"
        Reservation.png b |> Expect.equal "png path" (Path.Combine(dir, "002-session-ready.png"))
        Reservation.text b |> Expect.equal "sidecar path" (Path.Combine(dir, "002-session-ready.txt"))
        Reservation.region b VscCommand.SideBar |> Expect.equal "crop path" (Path.Combine(dir, "002-session-ready.sidebar.png"))
      finally
        Directory.Delete(dir, true)

    testCase "numbering carries on after files from an earlier call" <| fun _ ->
      let dir = temp ()
      try
        File.WriteAllText(Path.Combine(dir, "007-old.png"), "")
        let r = Reservation.reserve dir "next" |> Result.defaultWith failtest
        r.Number |> Expect.equal "after the highest" 8
      finally
        Directory.Delete(dir, true)

    testCase "the shots directory sits beside the screens directory" <| fun _ ->
      Environment.SetEnvironmentVariable(ShotsDirVar, null)
      shotsDir "/tmp/lem/x/out/screens" |> Expect.equal "OUT/shots" "/tmp/lem/x/out/shots"

    testCase "activation reads the gap between host start and the extension" <| fun _ ->
      let log =
        "2026-10-01 17:14:33.323 [info] Extension host with pid 134 started\n"
        + "2026-10-01 17:14:33.735 [info] ExtensionService#_doActivateExtension willehrendreich.sagefs, startup: true, activationEvent: 'x'\n"
      activationFromLog log |> Expect.stringContains "412 ms" "412 ms"

    testCase "activation says so when the log has none" <| fun _ ->
      activationFromLog "2026-10-01 17:14:33.323 [info] Extension host with pid 134 started\n"
      |> Expect.stringContains "not yet" "no activation"
  ]
