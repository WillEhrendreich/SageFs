module SageFs.Tests.ReplGuardTests

/// The sagefs-repl-guard hook's decision (tools/agent-hooks/ReplGuard.fs,
/// linked into this project). The hook script only does IO around this
/// function, so this is where the rules get pinned.

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.AgentHooks.ReplGuard

let private live = { Project = FSharpWorkspace; Daemon = DaemonAnswering }

let private isDeny =
  function
  | Deny _ -> true
  | Allow -> false

/// Plain argument words: no quotes, separators or spaces, so they never
/// change how the command splits.
let private argWord =
  Gen.elements [ "-c"; "Release"; "--no-restore"; "SageFs.Tests"; "--project"; "X.fsproj"; "--filter"; "Foo"; "-v"; "q" ]

let private args = Gen.listOfLength 3 argWord |> Gen.map (String.concat " ")

let private slowVerb = Gen.elements SlowVerb.all

let private contexts =
  Gen.elements [
    { Project = FSharpWorkspace; Daemon = DaemonAnswering }
    { Project = FSharpWorkspace; Daemon = DaemonNotAnswering }
    { Project = NotFSharpWorkspace; Daemon = DaemonAnswering }
    { Project = NotFSharpWorkspace; Daemon = DaemonNotAnswering }
  ]

let private prop gen body = Prop.forAll (Arb.fromGen gen) body

[<Tests>]
let replGuardTests = testList "sagefs-repl-guard decision" [

  testProperty "WHY — a slow-loop dotnet verb in an F# repo with SageFs up is denied, because that's the drift the hook exists to catch" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "dotnet %s %s" (SlowVerb.toToken verb) a) live |> isDeny)

  testProperty "WHY — SAGEFS_FINAL_GATE=1 in the env prefix always passes, because the final gate has to be runnable" <|
    prop (Gen.zip3 slowVerb args contexts) (fun (verb, a, ctx) ->
      decide (sprintf "SAGEFS_FINAL_GATE=1 dotnet %s %s" (SlowVerb.toToken verb) a) ctx = Allow)

  testProperty "WHY — with no SageFs daemon answering, everything passes, because there's no REPL to go back to" <|
    prop (Gen.zip slowVerb args) (fun (verb, a) ->
      decide (sprintf "dotnet %s %s" (SlowVerb.toToken verb) a) { live with Daemon = DaemonNotAnswering } = Allow)

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
      "env FOO=1 dotnet build -c Release"
      "ls; /usr/bin/dotnet fsi script.fsx"
      "dotnet pack -o nupkg && dotnet build" ]
    |> List.filter (fun c -> decide c live = Allow)
    |> Expect.isEmpty "every one of these runs a slow-loop verb"

  testCase "WHY — an escape hatch on one segment doesn't excuse a later undeclared one" <| fun _ ->
    decide "SAGEFS_FINAL_GATE=1 dotnet build && dotnet test" live
    |> isDeny
    |> Expect.isTrue "the second segment never declared the final gate"

  testCase "WHY — export SAGEFS_FINAL_GATE=1 before the call counts as the declaration" <| fun _ ->
    decide "export SAGEFS_FINAL_GATE=1 && dotnet test" live
    |> Expect.equal "an exported gate applies to what follows" Allow

  testCase "WHY — the deny reason restates the loop and names the escape hatch" <| fun _ ->
    match decide "dotnet test" live with
    | Allow -> failtest "dotnet test with SageFs up must be denied"
    | Deny reason ->
      [ "send_fsharp_code"; "hard_reset_fsi_session"; "rebuild=true"; "final gate"; "SAGEFS_FINAL_GATE=1 dotnet test" ]
      |> List.filter (fun needle -> not (reason.Contains needle))
      |> Expect.isEmpty "the reason should carry the loop and the way out"

  testCase "WHY — needsContext is false for anything the guard would allow without looking, so the hook skips the probe" <| fun _ ->
    [ "git status"; "dotnet pack"; "SAGEFS_FINAL_GATE=1 dotnet test" ]
    |> List.filter needsContext
    |> Expect.isEmpty "no probe needed"
]
