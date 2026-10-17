module SageFs.Tests.ReplGuardConfigTests

/// Issue #145: the three things the sagefs-repl-guard hook was missing.
///   1. A per-repository allowlist, `<git root>/.SageFs/repl-guard.json`.
///   2. `dotnet build -c Release` always passes (it still asks for a lease, like any declared final gate).
///   3. `SAGEFS_REPL_GUARD=advise` lets the command run and adds the reminder as `additionalContext`.
/// The pure half (parsing, matching, deciding) is tested straight on ReplGuard.fs. The run around it is tested
/// through `Verdict.verdict` with the stdin JSON the harness sends, a real temp git checkout on disk, and a fake daemon.

open System
open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.AgentHooks.ReplGuard
open SageFs.AgentHooks.Verdict

let private live = { Project = FSharpWorkspace; Daemon = DaemonAnswering; Session = Some ReadySessionCoversCwd; Lease = None }
let private liveMustWait = { live with Lease = Some(LeaseMustWait(12.0, "1 lease(s) active (cap 1 at tight pressure)")) }

let private words (pattern: string) = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

let private allowing (patterns: string list) =
  { Policy.strict with Allow = { Patterns = patterns |> List.map words } }

let private advising = { Policy.strict with Mode = Advising }

let private buildScript = "dotnet run --project Build.fsproj --"

let private isDeny =
  function
  | Deny _ -> true
  | Allow
  | AllowWithAdvice _ -> false

let private isAdvice =
  function
  | AllowWithAdvice _ -> true
  | Allow
  | Deny _ -> false

// ---- the world the run is tested in ----

/// What is on disk at `<repo>/.SageFs/repl-guard.json`.
type private ConfigOnDisk =
  | NoConfigFile
  | ConfigFileText of string

type private Checkout =
  { Root: string
    /// A directory two levels down, where the hook's payload says the agent is.
    Cwd: string }

let private withCheckout (config: ConfigOnDisk) (body: Checkout -> unit) =
  let root = Path.Combine(Path.GetTempPath(), "sagefs-repl-guard-" + Guid.NewGuid().ToString("N"))
  let cwd = Path.Combine(root, "src", "lib")
  Directory.CreateDirectory(Path.Combine(root, ".git")) |> ignore
  Directory.CreateDirectory cwd |> ignore
  match config with
  | NoConfigFile -> ()
  | ConfigFileText text ->
    Directory.CreateDirectory(Path.Combine(root, ConfigDirectory)) |> ignore
    File.WriteAllText(Path.Combine(root, ConfigDirectory, ConfigFileName), text)
  try body { Root = root; Cwd = cwd }
  finally Directory.Delete(root, true)

/// A daemon that is up with a Ready session at the checkout, so an undeclared slow verb is a real deny.
let private worldFor (checkout: Checkout) (env: (string * string) list) (lease: LeaseProbe option) : World =
  { GetEnv = fun name -> env |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd |> Option.defaultValue ""
    ReadRepoConfig = readRepoConfig
    ProjectScope = fun _ -> FSharpWorkspace
    Health = fun () -> DaemonAnswering, [ { WorkingDirectory = checkout.Root; Status = "Ready" } ]
    Lease = fun _ -> lease }

/// The JSON Claude Code sends a PreToolUse hook on stdin.
let private payload (cwd: string) (command: string) =
  JsonSerializer.Serialize
    {| hook_event_name = "PreToolUse"
       tool_name = "Bash"
       cwd = cwd
       tool_input = {| command = command |} |}

let private run (world: World) (checkout: Checkout) (command: string) = verdict world (payload checkout.Cwd command)

let private hookOutput (json: string) =
  use doc = JsonDocument.Parse json
  doc.RootElement.GetProperty("hookSpecificOutput").Clone()

let private prop (el: JsonElement) (name: string) =
  match el.TryGetProperty name with
  | true, v -> v.GetString()
  | _ -> ""

let private hasProp (el: JsonElement) (name: string) =
  match el.TryGetProperty name with
  | true, _ -> true
  | _ -> false

let private buildAllowedConfig = ConfigFileText """{ "allow": ["dotnet run --project Build.fsproj --"] }"""

[<Tests>]
let replGuardConfigTests = testList "sagefs-repl-guard: allowlist, Release builds, advise mode" [

  // ---- ask 1: the repository's allowlist ----

  testList "allowlist matching" [
    testCase "WHY — the repository's own build CLI passes, because the repo's CLAUDE.md tells the agent to run exactly that" <| fun _ ->
      decideWith (allowing [ buildScript ]) "dotnet run --project Build.fsproj -- test" live
      |> Expect.equal "listed, so allowed" Allow

    testCase "WHY — the allowlist is checked per command, so a cd and an env prefix in front do not hide it" <| fun _ ->
      decideWith (allowing [ buildScript ]) "cd lib && FOO=1 timeout 600 dotnet run --project Build.fsproj -- pack" live
      |> Expect.equal "the prefix words are not part of the dotnet command" Allow

    testCase "WHY — a different project is still denied, because the list names a command, not the verb" <| fun _ ->
      decideWith (allowing [ buildScript ]) "dotnet run --project Other.fsproj -- test" live
      |> isDeny
      |> Expect.isTrue "Other.fsproj is not on the list"

    testCase "WHY — a pattern matches whole words, so Build.fsproj.bak is not Build.fsproj" <| fun _ ->
      decideWith (allowing [ "dotnet run --project Build.fsproj" ]) "dotnet run --project Build.fsproj.bak" live
      |> isDeny
      |> Expect.isTrue "a longer word is a different word"

    testCase "WHY — an allowed command chained to an unlisted one is still denied, because each command is checked on its own" <| fun _ ->
      decideWith (allowing [ buildScript ]) "dotnet run --project Build.fsproj -- test && dotnet test" live
      |> isDeny
      |> Expect.isTrue "the second command is not on the list"

    testCase "WHY — an allowed command is not asked for a lease, because the repo said it always passes" <| fun _ ->
      decideWith (allowing [ buildScript ]) "dotnet run --project Build.fsproj -- test" liveMustWait
      |> Expect.equal "listed commands skip the lease check too" Allow

    testCase "WHY — a listed command needs no probe at all, so the hook leaves before the daemon call" <| fun _ ->
      needsContextWith (allowing [ buildScript ]).Allow "dotnet run --project Build.fsproj -- test"
      |> Expect.isFalse "nothing left that is a slow-loop verb"
  ]

  testList "config file parsing" [
    testCase "WHY — a list of dotnet command patterns parses into their words" <| fun _ ->
      parseConfig """{ "allow": ["dotnet run --project Build.fsproj --", "dotnet build Foo.fsproj"] }"""
      |> Expect.equal "two patterns as word lists"
           (Ok { Patterns = [ [ "dotnet"; "run"; "--project"; "Build.fsproj"; "--" ]; [ "dotnet"; "build"; "Foo.fsproj" ] ] })

    testCase "WHY — an empty object is a valid config that allows nothing" <| fun _ ->
      parseConfig "{}" |> Expect.equal "no allow key means an empty list" (Ok Allowlist.none)

    testCase "WHY — each way a file can be wrong is an Error that names what is wrong" <| fun _ ->
      [ "{ not json", "JSON"
        "[]", "object"
        """{ "allow": "dotnet build" }""", "allow"
        """{ "allow": [ 7 ] }""", "string"
        """{ "allow": [ "" ] }""", "empty"
        """{ "allow": [ "git status" ] }""", "dotnet"
        """{ "allow": [ "dotnet build && rm -rf x" ] }""", "one command"
        """{ "allows": [ "dotnet build" ] }""", "allows" ]
      |> List.choose (fun (text, mustMention) ->
        match parseConfig text with
        | Ok _ -> Some(sprintf "%s parsed" text)
        | Error why when why.Contains(mustMention, StringComparison.OrdinalIgnoreCase) -> None
        | Error why -> Some(sprintf "%s: error did not mention '%s': %s" text mustMention why))
      |> Expect.isEmpty "every bad file is an Error that says why"
  ]

  testList "a malformed or odd configuration fails closed and says so" [
    testCase "WHY — a malformed file ignores its allowlist, so the repo's build CLI is denied and the message names the file and the problem" <| fun _ ->
      let policy = Policy.build "" (RepoConfigMalformed("/repo/.SageFs/repl-guard.json", "the pattern \"git status\" must start with dotnet"))
      match decideWith policy "dotnet run --project Build.fsproj -- test" live with
      | Deny reason ->
        reason |> Expect.stringContains "names the file" "/repo/.SageFs/repl-guard.json"
        reason |> Expect.stringContains "says what is wrong" "must start with dotnet"
        reason |> Expect.stringContains "still carries the loop" "send_fsharp_code"
      | Allow
      | AllowWithAdvice _ -> failtest "a file the guard cannot use must not open anything up"

    testCase "WHY — an unrecognised SAGEFS_REPL_GUARD value enforces, and the deny says which values exist" <| fun _ ->
      match decideWith (Policy.build "sometimes" NoRepoConfig) "dotnet test" live with
      | Deny reason ->
        reason |> Expect.stringContains "quotes the bad value" "sometimes"
        reason |> Expect.stringContains "lists the real ones" "advise"
      | Allow
      | AllowWithAdvice _ -> failtest "an unknown mode must be strict"

    testCase "WHY — a config problem on a command that would pass anyway stays quiet" <| fun _ ->
      decideWith (Policy.build "" (RepoConfigMalformed("/repo/x.json", "bad"))) "dotnet pack" live
      |> Expect.equal "nothing to deny, nothing to say" Allow
  ]

  // ---- ask 2: Release builds ----

  testList "Release builds" [
    testCase "WHY — dotnet build in Release is not the REPL's job, so every spelling of it counts as a final gate" <| fun _ ->
      [ "dotnet build -c Release"
        "dotnet build --configuration Release"
        "dotnet build SageFs.Tests -c release --no-restore"
        "dotnet build --configuration=Release"
        "dotnet build -c:Release"
        "dotnet build -c Debug -c Release" ]
      |> List.filter (fun c -> classify c <> DeclaredFinalGate Build)
      |> Expect.isEmpty "each one is a declared Build"

    testCase "WHY — a Release build passes the REPL check even with a Ready session, because a deny only costs a retry" <| fun _ ->
      decide "dotnet build -c Release" live |> Expect.equal "allowed" Allow

    testCase "WHY — a Release build still asks for a lease, so five agents cannot each run one against one daemon" <| fun _ ->
      decide "dotnet build -c Release" liveMustWait
      |> isDeny
      |> Expect.isTrue "BUSY, same as any declared final gate"

    testCase "WHY — only a build gets this, and only when Release is what is asked for" <| fun _ ->
      [ "dotnet build -c Debug"
        "dotnet build -c Release -c Debug"
        "dotnet build"
        "dotnet test -c Release"
        "dotnet run -c Release"
        "dotnet run -- -c Release"
        "dotnet build -- -c Release" ]
      |> List.filter (fun c -> classify c = DeclaredFinalGate Build || classify c = DeclaredFinalGate Test || classify c = DeclaredFinalGate Run)
      |> Expect.isEmpty "every one of these is still undeclared"

    testCase "WHY — a Release build chained to an undeclared test is still denied" <| fun _ ->
      decide "dotnet build -c Release && dotnet test" live
      |> isDeny
      |> Expect.isTrue "the test never declared anything"
  ]

  // ---- ask 3: advise mode ----

  testList "advise mode" [
    testCase "WHY — the mode parses from its token, case-insensitively, and nothing else parses" <| fun _ ->
      [ GuardMode.tryParse "advise"; GuardMode.tryParse " Advise "; GuardMode.tryParse "enforce"; GuardMode.tryParse "warn" ]
      |> Expect.equal "advise, advise, enforce, nothing" [ Some Advising; Some Advising; Some Enforcing; None ]

    testCase "WHY — SAGEFS_REPL_GUARD=advise lets the command run and hands back the reminder instead of a deny" <| fun _ ->
      match decideWith (Policy.build "advise" NoRepoConfig) "dotnet test" live with
      | AllowWithAdvice reminder ->
        reminder |> Expect.stringContains "carries the loop" "send_fsharp_code"
        reminder |> Expect.stringContains "says it was allowed" "allowed"
      | Allow
      | Deny _ -> failtest "advise must neither deny nor stay silent"

    testCase "WHY — advice only appears where a deny would have, so a pack or an unrooted build gets no reminder" <| fun _ ->
      let policy = Policy.build "advise" NoRepoConfig
      decideWith policy "dotnet pack" live |> Expect.equal "pack is never a slow verb" Allow
      decideWith policy "dotnet test" { live with Session = Some NoReadySessionCoversCwd }
      |> Expect.equal "no REPL here, so nothing to recommend" Allow

    testCase "WHY — advise is about the REPL, not memory, so a busy daemon still says wait" <| fun _ ->
      decideWith advising "SAGEFS_FINAL_GATE=1 dotnet build" liveMustWait
      |> isDeny
      |> Expect.isTrue "BUSY is not a recommendation"

    testCase "WHY — enforce is the default and the word enforce is accepted" <| fun _ ->
      [ ""; "enforce" ]
      |> List.map (fun raw -> decideWith (Policy.build raw NoRepoConfig) "dotnet test" live |> isDeny)
      |> Expect.equal "both deny" [ true; true ]
  ]

  // ---- the real run: stdin JSON, a git checkout on disk, the repo root from the payload's cwd ----

  testList "the whole run" [
    testCase "WHY — with no config file, nothing changes: the REPL deny reads the same as before" <| fun _ ->
      withCheckout NoConfigFile (fun co ->
        match run (worldFor co [] None) co "dotnet test" with
        | None -> failtest "should deny"
        | Some json ->
          let out = hookOutput json
          prop out "permissionDecision" |> Expect.equal "a deny" "deny"
          prop out "permissionDecisionReason" |> Expect.stringContains "the loop" "send_fsharp_code")

    testCase "WHY — the config found at the git root, from a cwd two directories down, lets the repo's build CLI through" <| fun _ ->
      withCheckout buildAllowedConfig (fun co ->
        run (worldFor co [] None) co "dotnet run --project Build.fsproj -- test"
        |> Expect.isNone "allowed by .SageFs/repl-guard.json")

    testCase "WHY — the root comes from the payload's cwd, not the hook process's own directory" <| fun _ ->
      withCheckout buildAllowedConfig (fun co ->
        Directory.GetCurrentDirectory() |> Expect.notEqual "the process is not in the checkout" co.Cwd
        run (worldFor co [] None) co "dotnet run --project Build.fsproj -- test"
        |> Expect.isNone "found through the payload cwd")

    testCase "WHY — a git worktree's .git is a FILE, and its root counts" <| fun _ ->
      withCheckout buildAllowedConfig (fun co ->
        Directory.Delete(Path.Combine(co.Root, ".git"))
        File.WriteAllText(Path.Combine(co.Root, ".git"), "gitdir: /elsewhere/.git/worktrees/x")
        run (worldFor co [] None) co "dotnet run --project Build.fsproj -- test"
        |> Expect.isNone "worktree root found")

    testCase "WHY — a config with no git root above it is ignored, because the repo root is the git root and nothing else" <| fun _ ->
      withCheckout buildAllowedConfig (fun co ->
        Directory.Delete(Path.Combine(co.Root, ".git"))
        run (worldFor co [] None) co "dotnet run --project Build.fsproj -- test"
        |> Expect.isSome "no git root, so no allowlist")

    testCase "WHY — a malformed config denies with the file path and the problem in the message" <| fun _ ->
      withCheckout (ConfigFileText """{ "allow": [ "git status" ] }""") (fun co ->
        match run (worldFor co [] None) co "dotnet run --project Build.fsproj -- test" with
        | None -> failtest "a file the guard cannot use must not allow"
        | Some json ->
          let reason = prop (hookOutput json) "permissionDecisionReason"
          reason |> Expect.stringContains "names the file" (Path.Combine(co.Root, ConfigDirectory, ConfigFileName))
          reason |> Expect.stringContains "names the problem" "dotnet")

    testCase "WHY — Release build passes through the run when the lease is granted" <| fun _ ->
      withCheckout NoConfigFile (fun co ->
        run (worldFor co [] (Some LeaseGranted)) co "dotnet build -c Release"
        |> Expect.isNone "allowed")

    testCase "WHY — advise mode prints additionalContext and no permissionDecision, so the user's own prompts still apply" <| fun _ ->
      withCheckout NoConfigFile (fun co ->
        match run (worldFor co [ GuardMode.Variable, "advise" ] None) co "dotnet test" with
        | None -> failtest "advise should print the reminder"
        | Some json ->
          let out = hookOutput json
          prop out "hookEventName" |> Expect.equal "the event" "PreToolUse"
          prop out "additionalContext" |> Expect.stringContains "the reminder" "send_fsharp_code"
          hasProp out "permissionDecision" |> Expect.isFalse "not deciding permission")

    testCase "WHY — SAGEFS_FINAL_GATE=1 still works exactly as before" <| fun _ ->
      withCheckout NoConfigFile (fun co ->
        run (worldFor co [] (Some LeaseGranted)) co "SAGEFS_FINAL_GATE=1 dotnet test"
        |> Expect.isNone "granted, so allowed"
        run (worldFor co [] (Some(LeaseMustWait(5.0, "busy")))) co "SAGEFS_FINAL_GATE=1 dotnet test"
        |> Expect.isSome "busy still denies")

    testCase "WHY — a command that is not a slow verb never reads the config or asks the world anything" <| fun _ ->
      withCheckout buildAllowedConfig (fun co ->
        let asked = ref 0
        let ask result = asked.Value <- asked.Value + 1; result
        let nosy =
          { worldFor co [] None with
              GetEnv = fun _ -> ask ""
              ReadRepoConfig = fun _ -> ask NoRepoConfig
              Health = fun () -> ask (DaemonNotAnswering, []) }
        [ "dotnet pack"; "echo dotnet build"; "dotnet tool update sagefs -g" ]
        |> List.iter (fun c -> run nosy co c |> Expect.isNone "allowed")
        asked.Value |> Expect.equal "no environment, disk or daemon lookups" 0)
  ]
]
