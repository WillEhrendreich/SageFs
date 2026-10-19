module SageFs.Tests.ReplGuardTests

/// The sagefs-repl-guard hook's decision (tools/agent-hooks/ReplGuard.fs,
/// linked into this project). The hook script only does IO around this
/// function, so this is where the rules get pinned.
///
/// Three things this guard decides, layered:
///   1. An UNDECLARED slow verb ("you should be using the REPL") denies —
///      but only when there is a Ready daemon session whose working
///      directory covers the command's cwd. With SageFs up but no session
///      there yet, there is no REPL to send this code to — that is exactly
///      the one build `create_session` itself needs, so it must pass
///      (issue #144).
///   2. A DECLARED final gate now ALSO asks for an expensive-work lease —
///      legitimate, but still costs memory the daemon needs to know about.
///      `LeaseGranted` allows it; `LeaseRefused`/`LeaseMustWait` denies it
///      as BUSY (never as broken), with the reason and the wait.
///   3. The session-covers-cwd question itself is decided by `sessionProbe`,
///      layered on the pure path-containment check `directoryContains`.

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.AgentHooks.ReplGuard

let private live = { Project = FSharpWorkspace; Daemon = DaemonAnswering; Session = Some ReadySessionCoversCwd; Lease = None }
let private liveNoSessionHere = { live with Session = Some NoReadySessionCoversCwd }

/// What a session on a multi-targeted project with no build says in its own `faultReason` (issue #153, ProjectLoading's
/// FrameworkPicker.describe): the folders it looked in and the exact command to run.
let private no153Reason =
  "SageFs.Core.fsproj has no build output for any framework this host can load. Looked in these framework folders:\n"
  + "SageFs.Core/bin/Debug/net10.0\nSageFs.Core/bin/Release/net10.0\n"
  + "It would accept net11.0, net10.0 (in that order). Build one with: dotnet build SageFs.Core -c Release -f net11.0"

let private liveFaulted = { live with Session = Some(FaultedSessionCoversCwd no153Reason) }

let private advisingPolicy = { Policy.strict with Mode = Advising }

let private session (workingDirectory: string) (status: string) : SessionSummary =
  { WorkingDirectory = workingDirectory; Status = status; FaultReason = "" }

let private faulted (workingDirectory: string) (reason: string) : SessionSummary =
  { WorkingDirectory = workingDirectory; Status = "Faulted"; FaultReason = reason }

let private isAdviceMentioning (needle: string) =
  function
  | AllowWithAdvice text -> text.Contains needle
  | Allow
  | Deny _ -> false
let private liveGranted = { live with Lease = Some LeaseGranted }
let private liveMustWait = { live with Lease = Some(LeaseMustWait(12.0, "1 lease(s) active (cap 1 at tight pressure)")) }
let private liveRefused = { live with Lease = Some(LeaseRefused "you already hold 1/1 leases") }

let private isDeny =
  function
  | Deny _ -> true
  | Allow
  | AllowWithAdvice _ -> false

/// Plain argument words: no quotes, separators or spaces, so they never
/// change how the command splits.
let private argWord =
  Gen.elements [ "-c"; "Debug"; "--no-restore"; "SageFs.Tests"; "--project"; "X.fsproj"; "--filter"; "Foo"; "-v"; "q" ]

let private args = Gen.listOfLength 3 argWord |> Gen.map (String.concat " ")

let private slowVerb = Gen.elements SlowVerb.all

let private contexts =
  Gen.elements [
    { Project = FSharpWorkspace; Daemon = DaemonAnswering; Session = None; Lease = None }
    { Project = FSharpWorkspace; Daemon = DaemonNotAnswering; Session = None; Lease = None }
    { Project = NotFSharpWorkspace; Daemon = DaemonAnswering; Session = None; Lease = None }
    { Project = NotFSharpWorkspace; Daemon = DaemonNotAnswering; Session = None; Lease = None }
  ]

let private prop gen body = Prop.forAll (Arb.fromGen gen) body

[<Tests>]
let replGuardTests = testList "sagefs-repl-guard decision" [

  testProperty "WHY — an UNDECLARED slow-loop verb in an F# repo with a Ready session covering cwd is denied, because that's the drift the hook exists to catch" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "dotnet %s %s" (SlowVerb.toToken verb) a) live |> isDeny)

  testProperty "WHY — issue #144: an UNDECLARED slow-loop verb with SageFs up but NO Ready session covering cwd is allowed, because that's exactly the one build create_session itself needs" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "dotnet %s %s" (SlowVerb.toToken verb) a) liveNoSessionHere = Allow)

  testProperty "WHY — a Session that was never probed (Session=None) is treated the same as no covering session, fail-open rather than fail-closed" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "dotnet %s %s" (SlowVerb.toToken verb) a) { live with Session = None } = Allow)

  testProperty "WHY — a DECLARED final gate with the lease GRANTED always passes, because the final gate has to be runnable" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "SAGEFS_FINAL_GATE=1 dotnet %s %s" (SlowVerb.toToken verb) a) liveGranted = Allow)

  testProperty "WHY — a DECLARED final gate the daemon asks to WAIT for is denied as BUSY, not silently allowed" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "SAGEFS_FINAL_GATE=1 dotnet %s %s" (SlowVerb.toToken verb) a) liveMustWait |> isDeny)

  testProperty "WHY — a DECLARED final gate the daemon REFUSES a lease for is denied too" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "SAGEFS_FINAL_GATE=1 dotnet %s %s" (SlowVerb.toToken verb) a) liveRefused |> isDeny)

  testProperty "WHY — with no SageFs daemon answering, everything passes, because there's no REPL and no pool to ask" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "dotnet %s %s" (SlowVerb.toToken verb) a) { live with Daemon = DaemonNotAnswering } = Allow)

  testProperty "WHY — a daemon that answered but was never asked for a lease (Lease=None) still allows the final gate" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "SAGEFS_FINAL_GATE=1 dotnet %s %s" (SlowVerb.toToken verb) a) live = Allow)

  testProperty "WHY — outside an F# repo everything passes, because SageFs has nothing to load there" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "dotnet %s %s" (SlowVerb.toToken verb) a) { live with Project = NotFSharpWorkspace } = Allow)

  testProperty "WHY — packaging and tool management pass even with SageFs up, because the REPL can't do them" <|
    prop (Gen.zip (Gen.elements [ "pack"; "tool update sagefs -g"; "tool install"; "--version"; "--list-sdks"; "restore" ]) args) (fun (verb, a) ->
      decide (sprintf "dotnet %s %s" verb a) live = Allow)

  testProperty "WHY — a command that never runs dotnet passes in any context" <|
    prop (Gen.zip (Gen.elements [ "ls -la"; "git status"; "echo dotnet build"; "grep -rn \"dotnet test\" ."; "git commit -m 'dotnet run'" ]) contexts) (fun (cmd, ctx) ->
      decide cmd ctx = Allow)

  testCase "WHY — a slow verb after a cd or a wrapper is still caught" <| fun _ ->
    [ "cd SageFs.Tests && dotnet test"
      "timeout 600 dotnet run --project X.fsproj"
      "env FOO=1 dotnet build -c Debug"
      "ls; /usr/bin/dotnet fsi script.fsx"
      "dotnet pack -o nupkg && dotnet build" ]
    |> List.filter (fun c -> decide c live = Allow)
    |> Expect.isEmpty "every one of these runs a slow-loop verb"

  testCase "WHY — an escape hatch on one segment doesn't excuse a later undeclared one" <| fun _ ->
    decide "SAGEFS_FINAL_GATE=1 dotnet build && dotnet test" liveGranted
    |> isDeny
    |> Expect.isTrue "the second segment never declared the final gate"

  testCase "WHY — export SAGEFS_FINAL_GATE=1 before the call counts as the declaration" <| fun _ ->
    decide "export SAGEFS_FINAL_GATE=1 && dotnet test" liveGranted
    |> Expect.equal "an exported gate applies to what follows" Allow

  testCase "WHY — the undeclared deny reason restates the loop and names the escape hatch" <| fun _ ->
    match decide "dotnet test" live with
    | Allow
    | AllowWithAdvice _ -> failtest "dotnet test with SageFs up must be denied"
    | Deny reason ->
      [ "send_fsharp_code"; "hard_reset_fsi_session"; "rebuild=true"; "final gate"; "SAGEFS_FINAL_GATE=1 dotnet test" ]
      |> List.filter (fun needle -> not (reason.Contains needle))
      |> Expect.isEmpty "the reason should carry the loop and the way out"

  testCase "WHY — the busy deny reason says BUSY, names the wait, and never says to fall back to dotnet" <| fun _ ->
    match decide "SAGEFS_FINAL_GATE=1 dotnet build" liveMustWait with
    | Allow
    | AllowWithAdvice _ -> failtest "a MustWait lease outcome must deny"
    | Deny reason ->
      reason |> Expect.stringContains "explicitly says BUSY, not BROKEN" "BUSY, not broken"
      reason |> Expect.stringContains "carries the wait" "wait 12"
      (reason.Contains "1 lease(s) active") |> Expect.isTrue "carries the pool's own reason"

  testCase "WHY — the busy deny reason for a Refused lease says so too" <| fun _ ->
    match decide "SAGEFS_FINAL_GATE=1 dotnet build" liveRefused with
    | Allow
    | AllowWithAdvice _ -> failtest "a Refused lease outcome must deny"
    | Deny reason -> reason |> Expect.stringContains "carries the refusal reason" "you already hold 1/1 leases"

  testCase "WHY — needsContext is true for a DECLARED final gate now too, since it still has to ask for a lease" <| fun _ ->
    [ "git status"; "dotnet pack" ]
    |> List.filter needsContext
    |> Expect.isEmpty "these never touch dotnet's slow loop at all"
    needsContext "SAGEFS_FINAL_GATE=1 dotnet test" |> Expect.isTrue "a final gate still needs a lease check"
    needsContext "dotnet test" |> Expect.isTrue "an undeclared slow verb needs the daemon-answering check"

  testCase "WHY — directoryContains: a session rooted exactly at cwd covers it" <| fun _ ->
    directoryContains "/repo" "/repo" |> Expect.isTrue "same directory"

  testCase "WHY — directoryContains: a session rooted ABOVE cwd covers a build run from a subdirectory" <| fun _ ->
    directoryContains "/repo" "/repo/SageFs.Tests" |> Expect.isTrue "cwd nested under the session root"

  testCase "WHY — directoryContains: a session rooted BELOW cwd does not cover it" <| fun _ ->
    directoryContains "/repo/SageFs.Tests" "/repo" |> Expect.isFalse "the session is the narrower directory, not cwd"

  testCase "WHY — directoryContains: an unrelated directory never counts, even with a shared prefix string" <| fun _ ->
    directoryContains "/repo" "/repo-other/SageFs.Tests" |> Expect.isFalse "textual prefix, not a real path ancestor"

  testCase "WHY — directoryContains: trailing slashes and backslashes normalize the same way" <| fun _ ->
    directoryContains "/repo/" "/repo/SageFs.Tests/" |> Expect.isTrue "trailing slash on either side"
    directoryContains @"C:\repo" @"C:\repo\SageFs.Tests" |> Expect.isTrue "backslash-separated paths"

  testCase "WHY — sessionProbe: a Ready session covering cwd is ReadySessionCoversCwd" <| fun _ ->
    sessionProbe "/repo/SageFs.Tests" [ session "/repo" "Ready" ]
    |> Expect.equal "the one Ready session roots above cwd" ReadySessionCoversCwd

  testCase "WHY — sessionProbe: status match is case-insensitive, since the wire label's casing isn't this file's contract to police" <| fun _ ->
    sessionProbe "/repo" [ session "/repo" "ready" ]
    |> Expect.equal "case-insensitive status match" ReadySessionCoversCwd

  testCase "WHY — sessionProbe: a Faulted session at the same directory is not a REPL, and carries its own faultReason" <| fun _ ->
    sessionProbe "/repo" [ faulted "/repo" no153Reason ]
    |> Expect.equal "faulted, so no REPL to send code to, and the reason is the session's own" (FaultedSessionCoversCwd no153Reason)

  testCase "WHY — sessionProbe: a Faulted session below cwd does not cover it" <| fun _ ->
    sessionProbe "/repo" [ faulted "/repo/SageFs.Tests" no153Reason ]
    |> Expect.equal "the session is the narrower directory, not cwd" NoReadySessionCoversCwd

  testCase "WHY — sessionProbe: a Ready session for an unrelated directory does not count" <| fun _ ->
    sessionProbe "/repo" [ session "/somewhere/else" "Ready" ]
    |> Expect.equal "wrong directory entirely" NoReadySessionCoversCwd

  testCase "WHY — sessionProbe: a session still Starting is not Ready and not Faulted" <| fun _ ->
    sessionProbe "/repo" [ session "/repo" "Starting" ]
    |> Expect.equal "warming up is neither" NoReadySessionCoversCwd

  testCase "WHY — sessionProbe: an empty session list never counts, whatever cwd is" <| fun _ ->
    sessionProbe "/repo" [] |> Expect.equal "no sessions at all" NoReadySessionCoversCwd

  testCase "WHY — sessionProbe: one Ready match among several sessions is enough" <| fun _ ->
    sessionProbe "/repo/X"
      [ session "/elsewhere" "Ready"
        faulted "/repo" no153Reason
        session "/repo" "Ready" ]
    |> Expect.equal "the third session is Ready and covers cwd" ReadySessionCoversCwd

  testCase "WHY — sessionProbe: a Ready session beats a Faulted one in any order, because a REPL exists" <| fun _ ->
    [ [ faulted "/repo" no153Reason; session "/repo" "Ready" ]
      [ session "/repo" "Ready"; faulted "/repo" no153Reason ] ]
    |> List.map (sessionProbe "/repo")
    |> Expect.equal "both orders" [ ReadySessionCoversCwd; ReadySessionCoversCwd ]

  testCase "WHY — sessionProbe: of several Faulted sessions the nearest to cwd speaks, whichever order they arrive in, because it is the one the agent is standing in" <| fun _ ->
    let outer = faulted "/repo" "the outer one"
    let inner = faulted "/repo/worktree" "the inner one"
    [ [ outer; inner ]; [ inner; outer ] ]
    |> List.map (sessionProbe "/repo/worktree/src")
    |> Expect.equal "the deeper working directory wins in both orders" (List.replicate 2 (FaultedSessionCoversCwd "the inner one"))

  testCase "WHY — sessionProbe: a Faulted session whose wire reason is blank still says something, never an empty string" <| fun _ ->
    match sessionProbe "/repo" [ faulted "/repo" "  " ] with
    | FaultedSessionCoversCwd reason ->
      reason |> Expect.isNotEmpty "an empty reason would leave the agent guessing"
      reason.Trim() |> Expect.isNotEmpty "and not just whitespace"
    | ReadySessionCoversCwd
    | NoReadySessionCoversCwd -> failtest "a Faulted session covering cwd is Faulted"

  // ---- #160 ask 1: a Faulted session still lets the build through, and says why and what to do ----

  testProperty "WHY — a slow verb with a Faulted session covering cwd is allowed with the session's faultReason, in either mode, because the hatch is right and the guess is the problem" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      let command = sprintf "dotnet %s %s" (SlowVerb.toToken verb) a
      [ Policy.strict; advisingPolicy ]
      |> List.forall (fun policy ->
        match decideWith policy command liveFaulted with
        | AllowWithAdvice text -> text.Contains no153Reason
        | Allow
        | Deny _ -> false))

  testCase "WHY — the Faulted advice names the REPL way back (hard_reset_fsi_session rebuild=true) and does not tell the agent to stop building" <| fun _ ->
    match decide "dotnet build SageFs.Core -f net11.0" liveFaulted with
    | AllowWithAdvice text ->
      text |> Expect.stringContains "says the session is Faulted" "Faulted"
      text |> Expect.stringContains "the way back to the REPL" "hard_reset_fsi_session"
      text |> Expect.stringContains "with the flag that rebuilds" "rebuild=true"
      text |> Expect.stringContains "the command the reason named is quoted whole" "dotnet build SageFs.Core -c Release -f net11.0"
    | Allow
    | Deny _ -> failtest "a Faulted session must advise"

  testCase "WHY — a DECLARED final gate with a Faulted session covering cwd and the lease granted is allowed with the same advice" <| fun _ ->
    decide "SAGEFS_FINAL_GATE=1 dotnet build" { liveFaulted with Lease = Some LeaseGranted }
    |> isAdviceMentioning no153Reason
    |> Expect.isTrue "the reason rides along on the hatch too"

  testCase "WHY — a busy daemon still says wait for a declared gate, whatever the session did, because memory is not a preference" <| fun _ ->
    decide "SAGEFS_FINAL_GATE=1 dotnet build" { liveFaulted with Lease = Some(LeaseMustWait(12.0, "busy")) }
    |> isDeny
    |> Expect.isTrue "the lease refusal outranks the fault note"

  testCase "WHY — a Faulted session says nothing to a command that is not a slow dotnet verb" <| fun _ ->
    decide "dotnet pack" liveFaulted |> Expect.equal "pack never needed the REPL" Allow

  testCase "WHY — policy problems ride along under the Faulted advice like under every other message" <| fun _ ->
    let policy = { Policy.strict with Problems = [ "the config is wrong" ] }
    match decideWith policy "dotnet test" liveFaulted with
    | AllowWithAdvice text -> text |> Expect.stringContains "the problem is named" "the config is wrong"
    | Allow
    | Deny _ -> failtest "a Faulted session must advise"

  // ---- #160 ask 2: every use of the hatch while SageFs is up is a report the daemon can count ----

  testCase "WHY — the Faulted fallback is reported as an undeclared use with a Faulted REPL" <| fun _ ->
    hatchReportWith Policy.strict "dotnet build SageFs.Core" liveFaulted
    |> Expect.equal "counted" (Counted { Declaration = GateDeclaration.UndeclaredGate; Repl = ReplAtTheTime.Faulted })

  testCase "WHY — a declared final gate is reported as Declared, with the REPL state it ran against" <| fun _ ->
    [ liveGranted
      { liveGranted with Session = Some(FaultedSessionCoversCwd no153Reason) }
      { liveGranted with Session = Some NoReadySessionCoversCwd } ]
    |> List.map (hatchReportWith Policy.strict "SAGEFS_FINAL_GATE=1 dotnet test")
    |> Expect.equal "one report per REPL state"
         [ Counted { Declaration = GateDeclaration.DeclaredGate; Repl = ReplAtTheTime.Ready }
           Counted { Declaration = GateDeclaration.DeclaredGate; Repl = ReplAtTheTime.Faulted }
           Counted { Declaration = GateDeclaration.DeclaredGate; Repl = ReplAtTheTime.NotReady } ]

  testCase "WHY — a Release build is a declared use, because the guard treats it as the final gate" <| fun _ ->
    hatchReportWith Policy.strict "dotnet build -c Release" liveGranted
    |> Expect.equal "declared" (Counted { Declaration = GateDeclaration.DeclaredGate; Repl = ReplAtTheTime.Ready })

  testCase "WHY — the build before the first session is reported as an undeclared use against a REPL that is not ready" <| fun _ ->
    hatchReportWith Policy.strict "dotnet build" liveNoSessionHere
    |> Expect.equal "counted, and honest that nothing was ready" (Counted { Declaration = GateDeclaration.UndeclaredGate; Repl = ReplAtTheTime.NotReady })

  testCase "WHY — advise mode letting a build run with a Ready REPL is a use of the hatch too" <| fun _ ->
    hatchReportWith advisingPolicy "dotnet test" live
    |> Expect.equal "the REPL was there and the agent did not use it" (Counted { Declaration = GateDeclaration.UndeclaredGate; Repl = ReplAtTheTime.Ready })

  testCase "WHY — a denied command is not a use of the hatch, because it did not run" <| fun _ ->
    hatchReportWith Policy.strict "dotnet test" live
    |> Expect.equal "enforce denied it" NotCounted
    hatchReportWith Policy.strict "SAGEFS_FINAL_GATE=1 dotnet build" liveMustWait
    |> Expect.equal "a busy daemon denied it" NotCounted

  testProperty "WHY — nothing is reported when SageFs is not answering, outside an F# repo, or for a command that is not a slow verb" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      let command = sprintf "dotnet %s %s" (SlowVerb.toToken verb) a
      [ hatchReportWith Policy.strict command { live with Daemon = DaemonNotAnswering }
        hatchReportWith Policy.strict command { live with Project = NotFSharpWorkspace }
        hatchReportWith Policy.strict "dotnet pack" live
        hatchReportWith Policy.strict "git status" live ]
      |> List.forall ((=) NotCounted))

  testCase "WHY — a listed command is not a use of the hatch, because the repository said it always passes" <| fun _ ->
    let policy = { Policy.strict with Allow = { Patterns = [ [ "dotnet"; "run"; "--project"; "Build.fsproj"; "--" ] ] } }
    hatchReportWith policy "dotnet run --project Build.fsproj -- test" liveFaulted
    |> Expect.equal "not even counted" NotCounted

  testCase "WHY — a session that was never probed cannot be classified, so it is not reported" <| fun _ ->
    hatchReportWith Policy.strict "dotnet test" { live with Session = None }
    |> Expect.equal "no REPL state to name" NotCounted
]
