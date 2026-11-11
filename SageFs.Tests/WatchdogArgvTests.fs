/// The watchdog hands the daemon its argv one element per argument, so a path with a space survives a restart.
module SageFs.Tests.WatchdogArgvTests

open Expecto
open Expecto.Flip
open SageFs.Server

[<Tests>]
let watchdogArgvTests =
  testList "WatchdogRunner restart argv" [
    testCase "WHY — daemonStartInfo — a working directory with a space reaches the daemon as one argument" <| fun _ ->
      let args = [ "--dir"; "/home/will/my project"; "--supervised" ]
      let psi = WatchdogRunner.daemonStartInfo { ExePath = "/usr/bin/sagefs"; Args = args; WorkingDirectory = "/work" }
      List.ofSeq psi.ArgumentList
      |> Expect.equal "each argument is its own element, so the child never re-splits the path" args
      psi.Arguments
      |> Expect.equal "no joined Arguments string is set, because it would be split again" ""

    testCase "WHY — daemonStartInfo — the executable and the working directory are carried as given" <| fun _ ->
      let psi = WatchdogRunner.daemonStartInfo { ExePath = "/usr/bin/sagefs"; Args = []; WorkingDirectory = "/work dir" }
      psi.FileName |> Expect.equal "the executable" "/usr/bin/sagefs"
      psi.WorkingDirectory |> Expect.equal "the working directory" "/work dir"
      psi.UseShellExecute |> Expect.isFalse "no shell re-parses the argv"
  ]
