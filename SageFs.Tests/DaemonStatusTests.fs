/// `scripts/install-local.fsx --status` said "not running" about a daemon that was up and answering on both ports. `sagefs status` had
/// exited 1 because a component was degraded (another agent's worktree vanished under a file watcher), and the script read exit 1 as
/// "not running". These tests run the real command in every state and hold the classifier to what it actually prints.
module SageFs.Tests.DaemonStatusTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Build.DaemonStatus

let private installDirectory = Path.Combine(Path.GetTempPath(), "sagefs-daemon-status-tests")

let private infoAt pid directory version failures : DaemonInfo =
  { Pid = pid
    Port = 37749
    DashboardPort = 37750
    StartedAt = DateTime.UtcNow
    WorkingDirectory = directory
    Version = version
    ApiVersion = None
    SessionCount = None
    ComponentFailures = failures }

let private info version failures : DaemonInfo = infoAt 4242 installDirectory version failures

let private daemonOf pid directory : RunningDaemon =
  { Version = "0.6.937+abc1234"; Failing = 0; Pid = pid; Directory = directory }

/// What the real command prints and exits with, with the daemon lookup injected as the existing CLI tests do.
let private realCommand (readOnPort: int -> DaemonInfo option) (wedgedPid: int -> int option) : int * string =
  let original = Console.Out
  use writer = new StringWriter()
  Console.SetOut writer
  try
    let code = Program.statusCommand readOnPort wedgedPid (fun _ -> None) 37749
    code, writer.ToString()
  finally
    Console.SetOut original

let private healthy = realCommand (fun _ -> Some(info "0.6.937+abc1234" [])) (fun _ -> None)

let private degraded =
  realCommand
    (fun _ -> Some(info "0.6.937+abc1234" [ "file-watcher:/work/a: watcher error"; "mcp: restarting" ]))
    (fun _ -> None)

let private wedged = realCommand (fun _ -> None) (fun _ -> Some 9001)
let private absent = realCommand (fun _ -> None) (fun _ -> None)

/// The rule the script used: exit 0 means a version, anything else means not running.
let private ruleByExitCode (code: int, output: string) : string =
  match code with
  | 0 -> versionIn output
  | _ -> "not running"

/// The rule the script used after a stop: any daemon answering means the old one is still up.
let private ruleAnythingAnswering (report: DaemonReport) : AfterStop =
  match report with
  | NotRunning -> OldGone
  | _ -> OldStillRunning

[<Tests>]
let daemonStatusTests =
  testSequenced
  <| testList "DaemonStatus" [

    testCase "WHY — a daemon that is running but degraded is reported as running, with its version and how many components fail" <| fun _ ->
      let code, output = degraded
      code |> Expect.equal "the command itself exits 1 for a degraded daemon" 1
      classify output
      |> Expect.equal "so the exit code cannot be what says it is running" (Running { Version = "0.6.937+abc1234"; Failing = 2; Pid = 4242; Directory = installDirectory })
      describe (classify output)
      |> Expect.equal "and the status line says both" "0.6.937+abc1234 (degraded: 2 components failing)"

    testCase "a healthy daemon is its version and nothing else" <| fun _ ->
      let code, output = healthy
      code |> Expect.equal "healthy exits 0" 0
      classify output |> Expect.equal "running, nothing failing" (Running(daemonOf 4242 installDirectory))
      describe (classify output) |> Expect.equal "the line is just the version" "0.6.937+abc1234"

    testCase "a wedged daemon is not running and not healthy, and says so" <| fun _ ->
      let _, output = wedged
      classify output |> Expect.equal "wedged" Wedged
      (describe Wedged).Contains "not running" |> Expect.isFalse "a daemon holding the port is not described as absent"

    testCase "no daemon at all is the one case reported as not running" <| fun _ ->
      let _, output = absent
      classify output |> Expect.equal "nothing there" NotRunning
      describe NotRunning |> Expect.equal "the status line" "not running"

    testCase "WHY — output nobody recognises is reported as unknown, never as not running, because a guess is how the lie happened" <| fun _ ->
      classify "Unhandled exception: something broke before the first line"
      |> Expect.equal "kept as it was" (Unreadable "Unhandled exception: something broke before the first line")
      classify "" |> Expect.equal "silence is unreadable too" (Unreadable "")
      (describe (classify "garbage")).Contains "not running" |> Expect.isFalse "never claims the daemon is absent"

    testCase "TWIN — the rule by exit code calls the degraded daemon not running, which is the bug these tests exist to catch" <| fun _ ->
      ruleByExitCode degraded |> Expect.equal "the old rule's answer" "not running"
      ruleByExitCode healthy |> Expect.equal "and it was right only for a healthy daemon" "0.6.937+abc1234"
      (ruleByExitCode degraded = describe (classify (snd degraded)))
      |> Expect.isFalse "so the classifier and the old rule disagree on exactly the case that was wrong"

    testProperty "any daemon the real command calls running is classified as running, whatever its version and failures" <| fun (versionSeed: PositiveInt) (failureCount: byte) ->
      let version = sprintf "0.%d.%d+sha%d" (versionSeed.Get % 900) (versionSeed.Get % 97) (versionSeed.Get % 9999)
      let failures = [ for i in 1 .. int failureCount % 6 -> sprintf "component-%d: failed" i ]
      let _, output = realCommand (fun _ -> Some(info version failures)) (fun _ -> None)
      classify output = Running { Version = version; Failing = failures.Length; Pid = 4242; Directory = installDirectory }

    testCase "WHY — after a stop, a different daemon answering is a replacement and not the old one still running, because an agent's bridge starts one the moment there is none" <| fun _ ->
      let stoppedPid = 4242
      let replacement = realCommand (fun _ -> Some(infoAt 9100 "/home/will/Work/another-agents-worktree" "0.6.937+abc1234" [])) (fun _ -> None)
      match afterStop stoppedPid (classify (snd replacement)) with
      | ReplacedBy d ->
        d.Pid |> Expect.equal "names the process that started it" 9100
        d.Directory |> Expect.equal "and where it was started" "/home/will/Work/another-agents-worktree"
      | other -> failtestf "expected a replacement, got %A" other
      afterStop stoppedPid (classify (snd healthy)) |> Expect.equal "the same pid is the old daemon" OldStillRunning
      afterStop stoppedPid (classify (snd absent)) |> Expect.equal "nothing answering is gone" OldGone
      afterStop stoppedPid (classify (snd wedged)) |> Expect.equal "a wedged daemon may still hold the port" OldStillRunning
      afterStop stoppedPid (Unreadable "x") |> Expect.equal "output nobody can read is not proof it is gone" OldStillRunning

    testCase "TWIN — the old rule, any daemon answering means the old one is up, calls a replacement the old daemon" <| fun _ ->
      let replacement = classify (snd (realCommand (fun _ -> Some(infoAt 9100 "/elsewhere" "0.6.937+abc1234" [])) (fun _ -> None)))
      ruleAnythingAnswering replacement |> Expect.equal "the old rule's answer" OldStillRunning
      (afterStop 4242 replacement = ruleAnythingAnswering replacement) |> Expect.isFalse "and the new rule tells them apart"

    testCase "a daemon is the install script's own only when it was started in the install directory" <| fun _ ->
      startedIn installDirectory (daemonOf 1 installDirectory) |> Expect.isTrue "started where the script starts it"
      startedIn installDirectory (daemonOf 1 (installDirectory + "/")) |> Expect.isTrue "a trailing slash is the same place"
      startedIn installDirectory (daemonOf 1 "/home/will/Work/grugschool-wt/analyzers-door") |> Expect.isFalse "an agent's worktree is not"
  ]
